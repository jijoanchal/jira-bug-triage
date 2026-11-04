using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;

namespace Triage.Api.Services;

/// <summary>AI triage of a run, bug merging, and pushing bugs to Jira.</summary>
public class TriageService(AppDb db, AnthropicService ai, JiraService jira, IntegrationConfig cfg, ILogger<TriageService> log)
{
    public async Task<int> TriageRunAsync(int runId, CancellationToken ct)
    {
        var run = await db.Runs.FindAsync([runId], ct) ?? throw new KeyNotFoundException("Run not found.");
        if (run.Status == "Triaging") throw new InvalidOperationException("This run is already being triaged.");
        run.Status = "Triaging"; run.Error = null;
        await db.SaveChangesAsync(ct);

        try
        {
            var pending = await db.Failures.Include(f => f.Attachments)
                .Where(f => f.RunId == runId && f.BugId == null).ToListAsync(ct);

            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(pending.Select(async f =>
            {
                await gate.WaitAsync(ct);
                try { return (f, draft: (TriageDraft?)await ai.TriageAsync(f, new AiCallInfo("triage", runId, f.BugId, f.Id), ct), error: (string?)null); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "AI triage failed for failure {Id}", f.Id);
                    return (f, draft: null, error: ex.Message);
                }
                finally { gate.Release(); }
            }));

            foreach (var (f, draft, error) in results)
            {
                var bug = draft != null ? FromDraft(runId, draft) : Fallback(runId, f, error!);
                bug.Failures.Add(f);
                bug.StoryKey = StoryOf(bug.Failures);
                db.Bugs.Add(bug);
            }
            var aiFailures = results.Count(r => r.draft == null);
            run.Status = "Triaged";
            run.Error = aiFailures > 0 ? $"{aiFailures} failure(s) could not be triaged by AI; drafts were created from the raw error. Use Re-triage on them." : null;
            await db.SaveChangesAsync(ct);
            return results.Length;
        }
        catch (Exception ex)
        {
            run.Status = "TriageFailed"; run.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RetriageAsync(Bug bug, CancellationToken ct)
    {
        if (bug.Failures.Count != 1) throw new InvalidOperationException("Only single-failure bugs can be re-triaged.");
        var f = bug.Failures[0];
        var d = await ai.TriageAsync(f, new AiCallInfo("retriage", bug.RunId, bug.Id, f.Id), ct);
        bug.Summary = d.Summary; bug.Description = d.Description; bug.Labels = d.Labels; bug.Severity = d.Severity;
        bug.AiNote = null; bug.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Bug> MergeAsync(List<Bug> bugs, CancellationToken ct)
    {
        var d = await ai.MergeAsync(bugs, new AiCallInfo("merge", bugs[0].RunId), ct);
        var merged = FromDraft(bugs[0].RunId, d);
        db.Bugs.Add(merged);
        await db.SaveChangesAsync(ct);

        foreach (var b in bugs)
        {
            foreach (var f in b.Failures.ToList()) f.BugId = merged.Id;
            b.Status = BugStatus.Merged; b.MergedIntoId = merged.Id; b.UpdatedAt = DateTime.UtcNow;
        }
        // Keep what the user entered on the originals if the reports themselves named no story.
        merged.StoryKey = StoryOf(bugs.SelectMany(b => b.Failures)) ?? StoryKeys.Join(bugs.SelectMany(b => StoryKeys.Extract(b.StoryKey)));
        await db.SaveChangesAsync(ct);
        return merged;
    }

    /// <summary>Creates the Jira issue if needed, then uploads any attachments that have not been uploaded yet. Safe to call repeatedly.</summary>
    public async Task PushToJiraAsync(Bug bug, string projectKey, string? affectsVersion, string? fixVersion, string by, bool allowDuplicate, CancellationToken ct)
    {
        bug.JiraAttempts++; bug.JiraLastAttemptAt = DateTime.UtcNow;
        if (bug.JiraKey == null) // once the issue exists these describe it and must not drift
        {
            bug.JiraProjectKey = projectKey.Length > 0 ? projectKey : bug.JiraProjectKey;
            bug.JiraAffectsVersion = string.IsNullOrWhiteSpace(affectsVersion) ? null : affectsVersion.Trim();
            bug.JiraFixVersion = string.IsNullOrWhiteSpace(fixVersion) ? null : fixVersion.Trim();
        }
        string message;
        try
        {
            string? skipped = null;
            if (bug.JiraKey == null && !allowDuplicate && cfg.JiraDuplicateCheck && projectKey.Length > 0)
            {
                JiraService.DuplicateMatch? dup = null;
                try { dup = await jira.FindDuplicateAsync(bug, projectKey, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { skipped = $"Duplicate check skipped ({ex.Message})."; log.LogWarning(ex, "Jira duplicate check failed for bug {Id}", bug.Id); }
                if (dup != null)
                {
                    // Already tracked by an issue that is still being handled: point at it instead of filing another one.
                    bug.JiraKey = dup.Key; bug.JiraUrl = dup.Url; bug.JiraState = JiraState.Linked; bug.JiraError = null;
                    db.JiraAttempts.Add(new JiraAttempt { BugId = bug.Id, Success = true, JiraKey = dup.Key, By = by,
                        Message = $"Linked to existing {dup.Key} ({dup.Status}, {dup.Reason}): \"{dup.Summary}\". No new issue was created." });
                    bug.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    return;
                }
            }

            if (bug.JiraKey == null)
            {
                var issue = await jira.CreateIssueAsync(bug, projectKey, affectsVersion, fixVersion, ct);
                bug.JiraKey = issue.Key; bug.JiraUrl = issue.Url;
                await db.SaveChangesAsync(ct); // never lose the key once the issue exists
            }

            var all = bug.Failures.SelectMany(f => f.Attachments).ToList();
            var missing = all.Count(a => a.StoragePath == null);
            var failed = new List<string>();
            foreach (var a in all.Where(a => a.StoragePath != null && !a.JiraUploaded))
            {
                try { await jira.UploadAttachmentAsync(bug.JiraKey!, a, ct); a.JiraUploaded = true; a.JiraError = null; }
                catch (Exception ex) when (ex is not OperationCanceledException) { a.JiraError = ex.Message; failed.Add($"{a.FileName} ({ex.Message})"); }
            }

            // Link the new bug to its story. A failure here leaves the bug "partially created" so Retry can try the link again.
            string? storyNote = null; string? storyError = null;
            if (bug.JiraState != JiraState.Linked && !bug.StoryLinked && StoryKeys.Extract(bug.StoryKey) is { Count: > 0 } stories)
            {
                try
                {
                    var linked = await jira.LinkToStoriesAsync(bug.JiraKey!, stories, ct);
                    bug.StoryLinked = true;
                    storyNote = linked.Count > 0 ? $"linked to story {string.Join(", ", linked)}" : $"already linked to {string.Join(", ", stories)}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { storyError = $"Could not link {bug.JiraKey} to story {string.Join(", ", stories)}: {ex.Message}"; }
            }

            if (failed.Count == 0 && storyError == null)
            {
                bug.JiraState = JiraState.Created; bug.JiraError = null;
                message = $"Created {bug.JiraKey}; {all.Count(a => a.JiraUploaded)} attachment(s) uploaded" +
                          (storyNote != null ? $"; {storyNote}" : "") +
                          (missing > 0 ? $"; {missing} referenced artifact(s) were not provided at upload and were skipped." : ".");
            }
            else if (failed.Count == 0)
            {
                bug.JiraState = JiraState.PartiallyCreated;
                bug.JiraError = storyError;
                message = storyError!;
            }
            else
            {
                bug.JiraState = JiraState.PartiallyCreated;
                bug.JiraError = $"{failed.Count} attachment(s) failed to upload: {string.Join("; ", failed)}";
                message = bug.JiraError;
            }
            if (skipped != null) message = $"{skipped} {message}";
            db.JiraAttempts.Add(new JiraAttempt { BugId = bug.Id, Success = failed.Count == 0, Message = message, JiraKey = bug.JiraKey, By = by });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bug.JiraState = bug.JiraKey == null ? JiraState.Failed : JiraState.PartiallyCreated;
            bug.JiraError = ex.Message;
            db.JiraAttempts.Add(new JiraAttempt { BugId = bug.Id, Success = false, Message = ex.Message, JiraKey = bug.JiraKey, By = by });
        }
        bug.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The story key(s) named by the reports for these failures, or null when none of them names one.</summary>
    public static string? StoryOf(IEnumerable<TestFailure> failures) =>
        StoryKeys.Join(failures.SelectMany(f => StoryKeys.Extract(f.StoryKey)));

    private static Bug FromDraft(int runId, TriageDraft d) => new()
    {
        RunId = runId, Summary = d.Summary, Description = d.Description, Labels = d.Labels, Severity = d.Severity
    };

    private static Bug Fallback(int runId, TestFailure f, string error)
    {
        var title = f.Title.Length > 200 ? f.Title[..200] : f.Title;
        var labels = new List<string> { "automation-failure" };
        if (f.Project.Length > 0) labels.AddRange(AnthropicService.NormalizeLabels([f.Project]));
        return new Bug
        {
            RunId = runId,
            Summary = $"Automated test failed: {title}",
            Description = $"## Summary\nAutomated test \"{f.Title}\" failed.\n\n## Error Details\n```\n{f.ErrorMessage}\n```",
            Labels = labels,
            AiNote = $"AI triage failed: {error}"
        };
    }
}
