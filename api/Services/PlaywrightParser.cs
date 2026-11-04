using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Triage.Api.Services;

public record ParsedAttachment(string Name, string ContentType, string? Path, byte[]? Body);

public record ParsedFailure(
    string Title, string File, int Line, string Project, string Status, int Retries, double DurationMs,
    string ErrorMessage, string? Stack, string? Snippet, List<ParsedAttachment> Attachments, string? StoryKey = null);

public record ParsedRun(int Total, int Passed, int Failed, int Skipped, int Flaky, double DurationMs, List<ParsedFailure> Failures);

/// <summary>Parses the output of Playwright's built-in "json" reporter.</summary>
public static partial class PlaywrightParser
{
    public static ParsedRun Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("suites", out _)
            && root.TryGetProperty("results", out var zr) && zr.ValueKind == JsonValueKind.Array)
            return ParseZephyr(root, zr);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("suites", out var suites) || suites.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This does not look like a Playwright JSON report (no top-level \"suites\" array) or a Zephyr results file (no \"results\" array).");

        int total = 0, passed = 0, failed = 0, skipped = 0, flaky = 0;
        var failures = new List<ParsedFailure>();
        foreach (var s in suites.EnumerateArray())
            Walk(s, new List<string>(), null, ref total, ref passed, ref failed, ref skipped, ref flaky, failures);

        double duration = 0;
        if (root.TryGetProperty("stats", out var stats) && stats.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dv))
            duration = dv;
        return new ParsedRun(total, passed, failed, skipped, flaky, duration, failures);
    }

    /// <summary>Parses the flat zephyr-results.json written by the automation framework (one entry per mapped test).</summary>
    private static ParsedRun ParseZephyr(JsonElement root, JsonElement results)
    {
        int total = 0, passed = 0, failed = 0, skipped = 0, flaky = 0;
        double duration = 0;
        var failures = new List<ParsedFailure>();
        var project = Str(root, "browser") ?? "";

        foreach (var r in results.EnumerateArray())
        {
            total++;
            var dur = r.TryGetProperty("durationMs", out var dv) && dv.TryGetDouble(out var d) ? d : 0;
            duration += dur;
            var retries = r.TryGetProperty("retryCount", out var rc) && rc.TryGetInt32(out var rci) ? rci : 0;
            var status = Str(r, "status") ?? "";
            var pw = Str(r, "playwrightStatus") ?? "";

            var isFail = status.Equals("Fail", StringComparison.OrdinalIgnoreCase) || pw is "failed" or "timedOut";
            if (!isFail)
            {
                if (status.Equals("Pass", StringComparison.OrdinalIgnoreCase) || pw is "passed") passed++;
                else skipped++;
                continue;
            }
            failed++;

            var msg = Clean(Str(r, "errorMessage") ?? "");
            if (msg.Length == 0) msg = $"Test ended with status '{(pw.Length > 0 ? pw : status)}' and no error message.";

            var attachments = new List<ParsedAttachment>();
            if (r.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                foreach (var s in shots.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } p)
                        attachments.Add(new ParsedAttachment(System.IO.Path.GetFileName(p.Replace('\\', '/')), "image/png", p, null));

            failures.Add(new ParsedFailure(Str(r, "title") ?? Str(r, "key") ?? "Untitled test", Str(r, "specFile") ?? "", 0,
                project, pw.Length > 0 ? pw : "failed", retries, dur, msg.Trim(), null, null, attachments, StoryKeys.Join(StoryKeys.FromJson(r))));
        }
        return new ParsedRun(total, passed, failed, skipped, flaky, duration, failures);
    }

    private static void Walk(JsonElement suite, List<string> path, string? file,
        ref int total, ref int passed, ref int failed, ref int skipped, ref int flaky, List<ParsedFailure> failures)
    {
        var title = Str(suite, "title");
        var suiteFile = Str(suite, "file") ?? file;
        var here = new List<string>(path);
        // The top-level suite title is the spec file name; it adds no information to the test path.
        if (!string.IsNullOrEmpty(title) && title != suiteFile) here.Add(title);

        if (suite.TryGetProperty("specs", out var specs))
            foreach (var spec in specs.EnumerateArray())
            {
                var specTitle = Str(spec, "title") ?? "";
                var line = spec.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : 0;
                var specFile = Str(spec, "file") ?? suiteFile ?? "";
                if (!spec.TryGetProperty("tests", out var tests)) continue;
                foreach (var test in tests.EnumerateArray())
                {
                    total++;
                    var status = Str(test, "status") ?? "";
                    switch (status)
                    {
                        case "expected": passed++; continue;
                        case "skipped": skipped++; continue;
                        case "flaky": flaky++; continue;
                    }
                    var results = test.TryGetProperty("results", out var r) ? r.EnumerateArray().ToList() : new List<JsonElement>();
                    // "unexpected" is Playwright's term for failed; also guard against reports lacking test.status.
                    if (status != "unexpected" && !(results.Count > 0 && Str(results[^1], "status") is "failed" or "timedOut")) { passed++; continue; }
                    failed++;
                    failures.Add(BuildFailure(here, specTitle, specFile, line, test, results));
                }
            }

        if (suite.TryGetProperty("suites", out var children))
            foreach (var c in children.EnumerateArray())
                Walk(c, here, suiteFile, ref total, ref passed, ref failed, ref skipped, ref flaky, failures);
    }

    private static ParsedFailure BuildFailure(List<string> suitePath, string specTitle, string file, int line, JsonElement test, List<JsonElement> results)
    {
        var project = Str(test, "projectName") ?? "";
        var last = results.Count > 0 ? results[^1] : default;
        var msg = new StringBuilder();
        string? stack = null, snippet = null;

        if (last.ValueKind == JsonValueKind.Object)
        {
            if (last.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                foreach (var e in errs.EnumerateArray())
                {
                    if (msg.Length > 0) msg.AppendLine().AppendLine("---");
                    msg.Append(Clean(Str(e, "message") ?? ""));
                    stack ??= Str(e, "stack") is { } st ? Clean(st) : null;
                    snippet ??= Str(e, "snippet") is { } sn ? Clean(sn) : null;
                }
            }
            else if (last.TryGetProperty("error", out var e))
            {
                msg.Append(Clean(Str(e, "message") ?? ""));
                stack = Str(e, "stack") is { } st ? Clean(st) : null;
                snippet = Str(e, "snippet") is { } sn ? Clean(sn) : null;
            }
        }
        if (msg.Length == 0) msg.Append($"Test ended with status '{Str(last, "status") ?? "unknown"}' and no error message.");

        var attachments = new List<ParsedAttachment>();
        foreach (var res in results)
        {
            var retry = res.TryGetProperty("retry", out var rt) && rt.TryGetInt32(out var rv) ? rv : 0;
            if (!res.TryGetProperty("attachments", out var atts)) continue;
            foreach (var a in atts.EnumerateArray())
            {
                var name = Str(a, "name") ?? "attachment";
                var ct = Str(a, "contentType") ?? "application/octet-stream";
                var p = Str(a, "path");
                byte[]? body = null;
                if (p == null && Str(a, "body") is { } b)
                {
                    try { body = Convert.FromBase64String(b); } catch (FormatException) { body = Encoding.UTF8.GetBytes(b); }
                }
                if (p == null && body == null) continue;
                var label = retry > 0 ? $"retry{retry}-{name}" : name;
                attachments.Add(new ParsedAttachment(label, ct, p, body));
            }
        }

        var fullTitle = string.Join(" › ", suitePath.Append(specTitle).Where(s => !string.IsNullOrWhiteSpace(s)));
        var duration = results.Sum(x => x.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dv) ? dv : 0);
        return new ParsedFailure(fullTitle, file, line, project, Str(last, "status") ?? "failed",
            Math.Max(0, results.Count - 1), duration, msg.ToString().Trim(), stack, snippet, attachments, StoryKeys.Join(StoryKeys.FromJson(test)));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Strips ANSI colour codes that Playwright embeds in messages.</summary>
    public static string Clean(string s) => AnsiRegex().Replace(s, "").Replace("\r\n", "\n");

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
