using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Triage.Api.Services;

/// <summary>
/// One open Jira bug in a project dossier. Summary and description are kept so duplicate checks never have to fetch them again;
/// <see cref="Updated"/> is Jira's own change stamp, used to refetch a ticket only when it was edited. Tokens are derived, not stored.
/// </summary>
public record DossierIssue(string Key, string Summary, string Description, string Status, string Updated, HashSet<string> Labels, HashSet<string> Tokens);

/// <summary>The open bugs of one Jira project as they were at <see cref="FetchedAtUtc"/>.</summary>
/// <param name="Excluded">Not-done tickets that were left out because their status is not in <c>JIRA_DUPLICATE_STATUSES</c>, by status name.</param>
public record DossierSnapshot(string ProjectKey, DateTime FetchedAtUtc, List<DossierIssue> Issues, bool Truncated, Dictionary<string, int>? Excluded = null);

/// <summary>
/// Per-project dossier of the open Jira bugs, kept in memory and saved as JSON under <c>{DataDir}/jira-dossier/{KEY}.json</c> so it
/// survives restarts and redeploys. A sync only asks Jira which tickets are open (key, status, updated stamp) and downloads
/// summary / description just for tickets it has not seen or that changed; the rest come from the saved copy. A sync runs when the copy is
/// older than <c>JIRA_DOSSIER_MINUTES</c> (default 10; 0 = every check) and issues this app creates are added immediately.
/// </summary>
public class JiraDossier(IConfiguration config, IntegrationConfig cfg, ILogger<JiraDossier> log)
{
    private readonly ConcurrentDictionary<string, DossierSnapshot> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    private record Stored(string ProjectKey, DateTime FetchedAtUtc, bool Truncated, List<StoredIssue> Issues, Dictionary<string, int>? Excluded = null);
    private record StoredIssue(string Key, string Summary, string Description, string Status, string Updated, List<string> Labels);

    private string FileFor(string projectKey) =>
        Path.Combine(Path.GetFullPath(config["DataDir"] ?? "App_Data"), "jira-dossier", Regex.Replace(projectKey, "[^A-Za-z0-9_]", "_") + ".json");

    /// <summary>Returns the dossier, syncing it first when it is older than the cache window or <paramref name="refresh"/> is set.</summary>
    public async Task<DossierSnapshot> GetAsync(string projectKey, bool refresh, Func<DossierSnapshot?, Task<DossierSnapshot>> sync, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(projectKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!cache.TryGetValue(projectKey, out var snap) && Load(projectKey) is { } fromFile) cache[projectKey] = snap = fromFile;
            if (!refresh && cfg.JiraDossierMinutes > 0 && snap != null
                && DateTime.UtcNow - snap.FetchedAtUtc < TimeSpan.FromMinutes(cfg.JiraDossierMinutes))
                return snap;
            var fresh = await sync(snap);
            cache[projectKey] = fresh;
            Save(fresh);
            return fresh;
        }
        finally { gate.Release(); }
    }

    /// <summary>Records an issue created through this app so later bugs in the same session match it without waiting for the next sync.</summary>
    public void Add(string projectKey, DossierIssue issue)
    {
        if (!cache.TryGetValue(projectKey, out var snap)) return;
        lock (snap.Issues)
        {
            if (snap.Issues.Any(i => i.Key == issue.Key)) return;
            snap.Issues.Add(issue);
        }
        Save(snap);
    }

    private DossierSnapshot? Load(string projectKey)
    {
        try
        {
            var path = FileFor(projectKey);
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (s == null) return null;
            return new DossierSnapshot(s.ProjectKey, s.FetchedAtUtc, s.Issues.Select(i => new DossierIssue(i.Key, i.Summary, i.Description, i.Status,
                i.Updated, i.Labels.ToHashSet(StringComparer.OrdinalIgnoreCase), JiraService.Tokens(i.Summary))).ToList(), s.Truncated, s.Excluded);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Ignoring unreadable Jira dossier file for {Project}; it will be rebuilt from Jira.", projectKey);
            return null;
        }
    }

    private void Save(DossierSnapshot snap)
    {
        try
        {
            var path = FileFor(snap.ProjectKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Stored s;
            lock (snap.Issues)
                s = new Stored(snap.ProjectKey, snap.FetchedAtUtc, snap.Truncated, snap.Issues
                    .Select(i => new StoredIssue(i.Key, i.Summary, i.Description, i.Status, i.Updated, i.Labels.ToList())).ToList(), snap.Excluded);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s)); // write then move, so a crash never leaves a half-written file
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log.LogWarning(ex, "Could not save the Jira dossier for {Project}; it stays in memory only.", snap.ProjectKey); }
    }
}
