using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Triage.Api.Data;

namespace Triage.Api.Services;

public record JiraIssueResult(string Key, string Url);

public class JiraService(HttpClient http, IntegrationConfig cfg, ArtifactStore store, JiraDossier dossier)
{
    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, cfg.JiraBaseUrl + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.JiraUser}:{cfg.JiraToken}")));
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    public async Task<JiraIssueResult> CreateIssueAsync(Bug bug, string projectKey, string? affectsVersion, string? fixVersion, CancellationToken ct)
    {
        EnsureConfigured();
        var description = $"Severity (AI assessed): {bug.Severity}\nTriage iteration: {bug.Run?.Name ?? bug.RunId.ToString()}\n\n{bug.Description}";
        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = projectKey },
            ["issuetype"] = new { name = cfg.JiraIssueType },
            ["summary"] = bug.Summary.Length > 250 ? bug.Summary[..250] : bug.Summary,
            ["description"] = AdfConverter.ToAdf(description),
            // The tf-* labels fingerprint the failing tests so a later run can find this issue again, however the AI words the summary.
            ["labels"] = AnthropicService.NormalizeLabels(bug.Labels).Concat(FailureLabels(bug)).Distinct().ToList()
        };
        // Some projects require "Affects Version/s"; names are comma-separated and must already exist in the project.
        var versions = (string.IsNullOrWhiteSpace(affectsVersion) ? cfg.JiraAffectsVersion : affectsVersion)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => new { name = v }).ToList();
        if (versions is { Count: > 0 }) fields["versions"] = versions;
        var fixes = fixVersion?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => new { name = v }).ToList();
        if (fixes is { Count: > 0 }) fields["fixVersions"] = fixes;
        if (cfg.JiraExtraFields != null)
        {
            try
            {
                foreach (var p in JsonDocument.Parse(cfg.JiraExtraFields).RootElement.EnumerateObject()) fields[p.Name] = p.Value.Clone();
            }
            catch (JsonException ex) { throw new InvalidOperationException("JIRA_EXTRA_FIELDS is not a valid JSON object: " + ex.Message); }
        }
        var payload = new { fields };
        using var req = Req(HttpMethod.Post, "/rest/api/3/issue");
        req.Content = JsonContent.Create(payload);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, text));
        using var doc = JsonDocument.Parse(text);
        var key = doc.RootElement.GetProperty("key").GetString()!;
        // later bugs in the same batch must see this one as already open
        var created = fields["labels"] as List<string> ?? [];
        dossier.Add(projectKey, new DossierIssue(key, (string)fields["summary"]!, bug.Description, cfg.JiraDuplicateStatuses.FirstOrDefault() ?? "Open", "", created.ToHashSet(StringComparer.OrdinalIgnoreCase), Tokens((string)fields["summary"]!)));
        return new JiraIssueResult(key, $"{cfg.JiraBaseUrl}/browse/{key}");
    }

    public record DuplicateMatch(string Key, string Url, string Summary, string Status, string Reason);

    private const string FingerprintPrefix = "tf-";

    /// <summary>One stable label per failing test (file + title, ignoring browser project and line), at most 10 per issue.</summary>
    public static List<string> FailureLabels(Bug bug) => bug.Failures.Take(10).Select(f =>
        FingerprintPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.File}|{f.Title}".ToLowerInvariant())))[..12].ToLowerInvariant())
        .Distinct().ToList();

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "from", "when", "that", "this", "test", "tests", "fail", "fails", "failed", "failing", "failure",
        "error", "automated", "automation", "should", "does", "was", "are", "has", "have", "bug", "issue"
    };

    internal static HashSet<string> Tokens(string s) =>
        Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length >= 3 && !Stop.Contains(t)).ToHashSet();

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    private static string Jql(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// The project's open bugs (issue type <c>JIRA_ISSUE_TYPE</c>, default Bug, in one of the "being handled" statuses). Served from the saved
    /// dossier while it is fresh; otherwise synced: Jira is asked only which tickets are open, and summary / description are downloaded
    /// just for new or edited tickets.
    /// </summary>
    public Task<DossierSnapshot> GetDossierAsync(string projectKey, bool refresh, CancellationToken ct)
    {
        EnsureConfigured();
        return dossier.GetAsync(projectKey, refresh, prev => SyncDossierAsync(projectKey, prev, ct), ct);
    }

    private async Task<JsonDocument> SearchAsync(string jql, string fields, int max, string? pageToken, CancellationToken ct)
    {
        var url = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields={fields}&maxResults={max}" +
                  (pageToken != null ? $"&nextPageToken={Uri.EscapeDataString(pageToken)}" : "");
        using var req = Req(HttpMethod.Get, url);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, text));
        return JsonDocument.Parse(text);
    }

    private async Task<DossierSnapshot> SyncDossierAsync(string projectKey, DossierSnapshot? prev, CancellationToken ct)
    {
        const int maxPages = 20; // 2,000 open bugs is far beyond a normal backlog
        var known = prev?.Issues.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, DossierIssue>();
        var baseJql = $"project = \"{Jql(projectKey)}\" AND issuetype = \"{Jql(cfg.JiraIssueType)}\" AND statusCategory != Done";

        // 1. Which tickets are open right now? Only key, status and Jira's updated stamp are read.
        var open = new List<(string Key, string Status, string Updated)>();
        var excluded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? token = null; var truncated = false;
        for (var page = 0; ; page++)
        {
            using var doc = await SearchAsync(baseJql + " ORDER BY updated DESC", "status,updated", 100, token, ct);
            if (doc.RootElement.TryGetProperty("issues", out var arr))
                foreach (var i in arr.EnumerateArray())
                {
                    var f = i.GetProperty("fields");
                    var status = f.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                    if (!cfg.JiraDuplicateStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) { excluded[status] = excluded.GetValueOrDefault(status) + 1; continue; }
                    open.Add((i.GetProperty("key").GetString()!, status, f.TryGetProperty("updated", out var u) ? u.GetString() ?? "" : ""));
                }
            if (!doc.RootElement.TryGetProperty("nextPageToken", out var nt) || nt.ValueKind != JsonValueKind.String) break;
            token = nt.GetString();
            if (page + 1 >= maxPages) { truncated = true; break; }
        }

        // 2. Download summary / description only for tickets not in the dossier yet or edited since (different updated stamp).
        var need = open.Where(o => !known.TryGetValue(o.Key, out var k) || k.Updated != o.Updated).Select(o => o.Key).ToList();
        var fetched = new Dictionary<string, DossierIssue>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in need.Chunk(50))
        {
            using var doc = await SearchAsync($"key in ({string.Join(",", batch.Select(k => $"\"{Jql(k)}\""))})", "summary,description,labels,status,updated", 50, null, ct);
            if (!doc.RootElement.TryGetProperty("issues", out var arr)) continue;
            foreach (var i in arr.EnumerateArray())
            {
                var f = i.GetProperty("fields");
                var key = i.GetProperty("key").GetString()!;
                var summary = f.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                var desc = f.TryGetProperty("description", out var d) ? AdfText(d) : "";
                var status = f.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                var labels = f.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Array
                    ? lb.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
                fetched[key] = new DossierIssue(key, summary, desc, status, f.TryGetProperty("updated", out var u) ? u.GetString() ?? "" : "", labels, Tokens(summary));
            }
        }

        // 3. Tickets that are no longer open fall out; the rest reuse the saved copy.
        var issues = open.Select(o => fetched.TryGetValue(o.Key, out var f) ? f : known[o.Key] with { Status = o.Status }).ToList();
        return new DossierSnapshot(projectKey, DateTime.UtcNow, issues, truncated, excluded);
    }

    /// <summary>Plain text of an Atlassian Document Format value (descriptions), capped so the dossier file stays small.</summary>
    private static string AdfText(JsonElement e)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement n)
        {
            if (sb.Length > 2000) return;
            if (n.ValueKind == JsonValueKind.String) { sb.Append(n.GetString()); return; }
            if (n.ValueKind != JsonValueKind.Object) return;
            if (n.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
            if (n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var child in c.EnumerateArray()) Walk(child);
            if (n.TryGetProperty("type", out var ty) && ty.GetString() is "paragraph" or "heading" or "listItem" or "codeBlock") sb.Append('\n');
        }
        Walk(e);
        return sb.Length > 2000 ? sb.ToString(0, 2000) : sb.ToString();
    }

    public record ScoredIssue(DossierIssue Issue, double Score, string Reason);

    /// <summary>
    /// Rates every open bug in the dossier against this bug, best first. Signals, strongest first: the failing test's fingerprint label;
    /// the Zephyr test-case key (e.g. GCR-T1) or the full test name in the ticket's summary / description; the test's name words all
    /// appearing in the summary; and how alike the two summaries are.
    /// </summary>
    public static List<ScoredIssue> ScoreAll(Bug bug, DossierSnapshot snap)
    {
        var labels = FailureLabels(bug);
        var summaryTokens = Tokens(bug.Summary);
        var titles = bug.Failures.Select(f => f.Title.ToLowerInvariant()).Where(t => t.Length >= 8).ToList();
        var caseKeys = bug.Failures.Select(f => ZephyrCsvParser.KeyOf(f.Title)).Where(k => k != null).Select(k => k!).Distinct().ToList();
        // name words of each failing test without its [KEY-T123] prefix
        var titleTokens = bug.Failures.Select(f => Tokens(Regex.Replace(f.Title, @"\[[A-Za-z]+-T\d+\]", ""))).Where(t => t.Count >= 3).ToList();

        DossierIssue[] issues;
        lock (snap.Issues) issues = snap.Issues.ToArray();
        return issues.Select(i =>
        {
            bool Mentions(string text, string what) => Regex.IsMatch(text, $@"(?<![A-Za-z0-9-]){Regex.Escape(what)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
            if (labels.Any(i.Labels.Contains)) return new ScoredIssue(i, 1, "same failing test (label)");
            if (caseKeys.FirstOrDefault(k => Mentions(i.Summary, k) || Mentions(i.Description, k)) is { } ck)
                return new ScoredIssue(i, 0.97, $"mentions test case {ck}");
            if (titles.Any(t => i.Summary.Contains(t, StringComparison.OrdinalIgnoreCase))) return new ScoredIssue(i, 0.95, "summary names the failing test");
            if (titles.Any(t => i.Description.Contains(t, StringComparison.OrdinalIgnoreCase))) return new ScoredIssue(i, 0.9, "description names the failing test");
            if (titleTokens.Any(t => (double)t.Intersect(i.Tokens).Count() / t.Count >= 0.8)) return new ScoredIssue(i, 0.85, "summary covers the failing test's name");
            var jac = Similarity(summaryTokens, i.Tokens);
            var min = Math.Min(summaryTokens.Count, i.Tokens.Count);
            var overlap = min >= 3 ? 0.9 * summaryTokens.Intersect(i.Tokens).Count() / min : 0; // a short ticket inside a longer summary
            var sim = Math.Max(jac, overlap);
            return new ScoredIssue(i, sim, $"summary {sim:P0} alike");
        }).OrderByDescending(x => x.Score).ToList();
    }

    /// <summary>The open bug that already tracks this bug (score at or above the threshold), or null. No Jira call when the dossier is fresh.</summary>
    public async Task<DuplicateMatch?> FindDuplicateAsync(Bug bug, string projectKey, CancellationToken ct)
    {
        var snap = await GetDossierAsync(projectKey, refresh: false, ct);
        var best = ScoreAll(bug, snap).FirstOrDefault();
        return best != null && best.Score >= cfg.JiraDuplicateThreshold
            ? new DuplicateMatch(best.Issue.Key, $"{cfg.JiraBaseUrl}/browse/{best.Issue.Key}", best.Issue.Summary, best.Issue.Status, best.Reason)
            : null;
    }

    /// <summary>
    /// Links the issue to each story with the configured link type, skipping stories it is already linked to (so a retry never
    /// duplicates a link). Returns the keys that were newly linked; throws on the first failure.
    /// </summary>
    public async Task<List<string>> LinkToStoriesAsync(string issueKey, IEnumerable<string> storyKeys, CancellationToken ct)
    {
        EnsureConfigured();
        using (var get = Req(HttpMethod.Get, $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields=issuelinks"))
        using (var res = await http.SendAsync(get, ct))
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, text));
            using var doc = JsonDocument.Parse(text);
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.GetProperty("fields").TryGetProperty("issuelinks", out var links))
                foreach (var l in links.EnumerateArray())
                    foreach (var side in new[] { "inwardIssue", "outwardIssue" })
                        if (l.TryGetProperty(side, out var i) && i.TryGetProperty("key", out var k) && k.GetString() is { } ks) existing.Add(ks);
            storyKeys = storyKeys.Where(s => !existing.Contains(s)).ToList();
        }

        var done = new List<string>();
        foreach (var story in storyKeys)
        {
            using var req = Req(HttpMethod.Post, "/rest/api/3/issueLink");
            req.Content = JsonContent.Create(new
            {
                type = new { name = cfg.JiraStoryLinkType },
                inwardIssue = new { key = issueKey },
                outwardIssue = new { key = story }
            });
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, await res.Content.ReadAsStringAsync(ct)));
            done.Add(story);
        }
        return done;
    }

    public record JiraVersion(string Name, bool Released);

    /// <summary>The project's versions that can still be chosen (archived ones are hidden), unreleased first.</summary>
    public async Task<List<JiraVersion>> GetVersionsAsync(string projectKey, CancellationToken ct)
    {
        EnsureConfigured();
        using var req = Req(HttpMethod.Get, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}/versions");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, text));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.EnumerateArray()
            .Where(v => !(v.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True))
            .Select(v => new JiraVersion(v.GetProperty("name").GetString() ?? "", v.TryGetProperty("released", out var r) && r.ValueKind == JsonValueKind.True))
            .Where(v => v.Name.Length > 0).OrderBy(v => v.Released).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Uploads one attachment; throws with a readable message on failure.</summary>
    public async Task UploadAttachmentAsync(string issueKey, FailureAttachment att, CancellationToken ct)
    {
        EnsureConfigured();
        if (att.StoragePath == null) throw new FileNotFoundException("The artifact file was not provided at upload time.");
        var abs = store.Abs(att.StoragePath);
        if (!File.Exists(abs)) throw new FileNotFoundException("Stored artifact file is missing from disk.");

        using var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(abs));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(att.ContentType);
        form.Add(file, "file", att.FileName);

        using var req = Req(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/attachments");
        req.Headers.Add("X-Atlassian-Token", "no-check");
        req.Content = form;
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Describe(res, await res.Content.ReadAsStringAsync(ct)));
    }

    private void EnsureConfigured()
    {
        if (!cfg.JiraConfigured)
            throw new InvalidOperationException("Jira is not configured: set CONFLUENCE_API_URL / CONFLUENCE_API_USER / CONFLUENCE_API_TOKEN (or JIRA_BASE_URL / JIRA_API_USER / JIRA_API_TOKEN).");
    }

    private static string Describe(HttpResponseMessage res, string body)
    {
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("errorMessages", out var em)) parts.AddRange(em.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (doc.RootElement.TryGetProperty("errors", out var er) && er.ValueKind == JsonValueKind.Object)
                parts.AddRange(er.EnumerateObject().Select(p => $"{p.Name}: {p.Value.GetString()}"));
            if (parts.Count > 0) detail = string.Join("; ", parts);
        }
        catch (JsonException) { /* not JSON, keep raw */ }
        if (detail.Length > 500) detail = detail[..500];
        return $"Jira returned {(int)res.StatusCode} {res.ReasonPhrase}: {detail}";
    }
}
