using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Triage.Api.Data;

namespace Triage.Api.Services;

public record TriageDraft(string Summary, string Description, List<string> Labels, string Severity);

public class AnthropicService(HttpClient http, IntegrationConfig cfg, ILogger<AnthropicService> log,
    AiOptions ai, UsageRecorder recorder, BudgetService budget, IHttpContextAccessor httpContext)
{
    private const string FormatRules = """
        Respond with ONE JSON object and nothing else, with exactly these keys:
        {"summary": string, "description": string, "labels": string[], "severity": "Critical"|"High"|"Medium"|"Low"}

        Rules:
        - summary: a single line, max 110 characters, describing the defect (what is broken, where). No test-file paths, no trailing period.
        - description: Jira-ready text using ONLY this markup: "## Heading" lines, "- " bullets, "1. " numbered items, ``` fenced code blocks, and plain paragraphs. No tables, no bold/italic, no links.
          Use these sections in order: "## Summary", "## Steps to Reproduce", "## Expected Result", "## Actual Result", "## Error Details" (short code block with the essential error lines only), "## Possible Cause". Do not add an Environment section.
        - Never invent facts. Steps to Reproduce and Possible Cause must be derived from the test title, error and stack; where you are inferring, say "Inferred from the test name".
        - labels: 2 to 6 short lowercase tags using letters, digits and hyphens only (no spaces). Include an area/feature tag, a failure-type tag (e.g. timeout, assertion, locator, network) and the browser/project when known. Always include "automation-failure".
        - severity: your judgement of user impact.
        """;

    public async Task<TriageDraft> TriageAsync(TestFailure f, AiCallInfo info, CancellationToken ct = default)
    {
        var user = $"""
            Triage this failed automated Playwright test into a bug report.

            Test: {f.Title}
            File: {f.File}:{f.Line}
            Browser/project: {f.Project}
            Retries used: {f.Retries}
            Duration: {f.DurationMs / 1000:0.0}s
            Attachments available: {string.Join(", ", f.Attachments.Select(a => a.FileName).DefaultIfEmpty("none"))}

            Error message:
            {Truncate(f.ErrorMessage, 5000)}

            Code snippet / test case steps:
            {Truncate(f.Snippet ?? "(none)", 4000)}

            Stack:
            {Truncate(f.Stack ?? "(none)", 2500)}
            """;
        return await CallAsync(FormatRules, user, info, ct);
    }

    public async Task<TriageDraft> MergeAsync(IReadOnlyList<Bug> bugs, AiCallInfo info, CancellationToken ct = default)
    {
        var parts = bugs.Select((b, i) => $"""
            --- Bug {i + 1} (severity {b.Severity}; labels: {string.Join(", ", b.Labels)}) ---
            Summary: {b.Summary}
            Affected tests: {string.Join("; ", b.Failures.Select(x => x.Title))}
            Description:
            {Truncate(b.Description, 3500)}
            """);
        var system = "You consolidate duplicate or closely related bug reports into one.\n" + FormatRules +
                     "\n- When merging: unify the summary around the shared root problem, list all affected tests in \"## Summary\" as bullets, keep distinct error details, union the labels (deduplicated, max 8) and use the highest severity.";
        var user = "Merge these bug reports into a single bug report.\n\n" + string.Join("\n\n", parts);
        return await CallAsync(system, user, info, ct);
    }

    private async Task<TriageDraft> CallAsync(string system, string user, AiCallInfo info, CancellationToken ct)
    {
        if (!cfg.AnthropicConfigured) throw new InvalidOperationException("ANTHROPIC_API_KEY is not set on the server.");

        var principal = httpContext.HttpContext?.User;
        Guid? userId = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
        var entry = new AiUsage
        {
            UserId = userId, UserName = principal?.FindFirstValue(ClaimTypes.Name) ?? "system",
            Operation = info.Operation, RunId = info.RunId, BugId = info.BugId, FailureId = info.FailureId, Model = cfg.Model
        };

        try { await budget.EnforceAsync(userId, ct); }
        catch (BudgetExceededException ex)
        {
            entry.Status = "blocked"; entry.Attempts = 0; entry.Error = ex.Message;
            await recorder.RecordAsync(entry);
            throw;
        }

        var body = new
        {
            model = cfg.Model,
            max_tokens = 3000,
            system,
            messages = new[] { new { role = "user", content = user } }
        };

        var clock = Stopwatch.StartNew();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                entry.Attempts = attempt;
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                { Content = JsonContent.Create(body) };
                req.Headers.Add("x-api-key", cfg.AnthropicKey);
                req.Headers.Add("anthropic-version", "2023-06-01");

                using var res = await http.SendAsync(req, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                entry.HttpStatus = (int)res.StatusCode;
                if (res.Headers.TryGetValues("request-id", out var rid)) entry.RequestId = rid.FirstOrDefault();

                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    ReadUsage(doc.RootElement, entry);
                    var content = doc.RootElement.GetProperty("content").EnumerateArray()
                        .Where(c => c.GetProperty("type").GetString() == "text")
                        .Select(c => c.GetProperty("text").GetString()).FirstOrDefault() ?? "";
                    try { return ParseDraft(content); }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException)
                    {
                        entry.Status = "bad-output"; entry.Error = ex.Message; // the tokens were still spent
                        throw;
                    }
                }

                var transient = res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500;
                if (transient && attempt < 4)
                {
                    log.LogWarning("Anthropic returned {Status}; retry {Attempt}", (int)res.StatusCode, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }
                throw new InvalidOperationException($"Anthropic API error {(int)res.StatusCode}: {Truncate(text, 300)}");
            }
        }
        catch (Exception ex)
        {
            if (entry.Status == "ok") { entry.Status = "error"; entry.Error = Truncate(ex.Message, 500); }
            throw;
        }
        finally
        {
            entry.DurationMs = clock.ElapsedMilliseconds;
            await recorder.RecordAsync(entry);
            log.LogInformation("Anthropic {Operation} on {Model}: {Status}, in {In} / cache-write {Cw} / cache-read {Cr} / out {Out} tokens, ${Cost:0.0000}",
                entry.Operation, entry.Model, entry.Status, entry.InputTokens, entry.CacheWriteTokens, entry.CacheReadTokens, entry.OutputTokens, entry.CostUsd);
        }
    }

    /// <summary>Copies the token counts from the response and prices them. Cached input is reported separately from input_tokens.</summary>
    private void ReadUsage(JsonElement root, AiUsage entry)
    {
        if (root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } model) entry.Model = model;
        if (!root.TryGetProperty("usage", out var u)) return;
        long N(string k) => u.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
        entry.InputTokens = N("input_tokens");
        entry.OutputTokens = N("output_tokens");
        entry.CacheWriteTokens = N("cache_creation_input_tokens");
        entry.CacheReadTokens = N("cache_read_input_tokens");
        entry.TotalTokens = entry.InputTokens + entry.CacheWriteTokens + entry.CacheReadTokens + entry.OutputTokens;
        var price = ai.Price(entry.Model);
        entry.Priced = price != null;
        entry.CostUsd = price?.Cost(entry.InputTokens, entry.CacheWriteTokens, entry.CacheReadTokens, entry.OutputTokens) ?? 0;
        if (price == null) log.LogWarning("No price configured for model {Model}; add it under Ai:Models so its cost is tracked", entry.Model);
    }

    private static TriageDraft ParseDraft(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("AI response did not contain JSON.");
        using var doc = JsonDocument.Parse(text[start..(end + 1)]);
        var r = doc.RootElement;
        var summary = r.GetProperty("summary").GetString()?.Trim() ?? "";
        var desc = r.GetProperty("description").GetString()?.Trim() ?? "";
        if (summary.Length == 0 || desc.Length == 0) throw new InvalidDataException("AI response had an empty summary or description.");
        var labels = r.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
        var sev = r.TryGetProperty("severity", out var s) ? s.GetString() : null;
        return new TriageDraft(summary.Length > 250 ? summary[..250] : summary, desc, NormalizeLabels(labels), NormalizeSeverity(sev));
    }

    public static List<string> NormalizeLabels(IEnumerable<string> labels) =>
        labels.Select(x => Regex.Replace(x.Trim().ToLowerInvariant(), @"[^a-z0-9_.\-]+", "-").Trim('-'))
              .Where(x => x.Length > 0).Select(x => x.Length > 60 ? x[..60] : x).Distinct().ToList();

    public static string NormalizeSeverity(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "critical" => "Critical", "high" => "High", "low" => "Low", _ => "Medium"
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…[truncated]";
}
