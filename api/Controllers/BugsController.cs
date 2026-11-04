using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;
using Triage.Api.Services;

namespace Triage.Api.Controllers;

public record UpdateBugRequest(string Summary, string Description, List<string> Labels, string? Severity, bool? Discard);
public record StoryRequest(string? StoryKey);
public record MergeRequest(List<int> BugIds);
public record JiraCreateRequest(List<int> BugIds, string? ProjectKey, string? AffectsVersion = null, string? FixVersion = null, bool AllowDuplicate = false);
public record JiraRetryRequest(string? ProjectKey, string? AffectsVersion = null, string? FixVersion = null, bool AllowDuplicate = false);

[ApiController, Route("api/bugs"), Authorize]
public class BugsController(AppDb db, TriageService triage, IntegrationConfig cfg) : ControllerBase
{
    private IQueryable<Bug> Full() => db.Bugs.AsSplitQuery().Include(b => b.Run).Include(b => b.Failures).ThenInclude(f => f.Attachments);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateBugRequest r)
    {
        var bug = await Full().FirstOrDefaultAsync(b => b.Id == id);
        if (bug == null) return NotFound();
        if (bug.Status == BugStatus.Merged) return Conflict(new { message = "Merged bugs cannot be edited." });
        if (bug.JiraState is JiraState.Created or JiraState.PartiallyCreated or JiraState.Linked) return Conflict(new { message = "This bug is already in Jira and can no longer be edited here." });
        if (string.IsNullOrWhiteSpace(r.Summary)) return BadRequest(new { message = "Summary is required." });

        bug.Summary = r.Summary.Trim();
        bug.Description = r.Description ?? "";
        bug.Labels = AnthropicService.NormalizeLabels(r.Labels ?? []);
        if (r.Severity != null) bug.Severity = AnthropicService.NormalizeSeverity(r.Severity);
        if (r.Discard.HasValue) bug.Status = r.Discard.Value ? BugStatus.Discarded : BugStatus.Draft;
        bug.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(Dto.Bug(bug));
    }

    /// <summary>
    /// Sets (or clears) the story the bug is linked to. Allowed while the bug is active, including after it is in Jira; in that case
    /// the link is made by the next retry (the card offers "Link to story").
    /// </summary>
    [HttpPut("{id:int}/story")]
    public async Task<IActionResult> SetStory(int id, StoryRequest r)
    {
        var bug = await db.Bugs.FirstOrDefaultAsync(b => b.Id == id);
        if (bug == null) return NotFound();
        if (bug.Status != BugStatus.Draft) return Conflict(new { message = "Only active bugs can be linked to a story." });
        if (bug.JiraState == JiraState.Linked) return Conflict(new { message = "This bug points at an existing Jira issue; it is not linked to a story here." });
        string? key;
        try { key = StoryKeys.Normalize(r.StoryKey); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        if (key != bug.StoryKey) { bug.StoryKey = key; bug.StoryLinked = false; bug.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
        return Ok(Dto.Bug(await Full().FirstAsync(b => b.Id == id)));
    }

    /// <summary>
    /// Shows why a bug did or did not match an open Jira ticket: the dossier size, how it was filtered, and the closest tickets with their scores.
    /// </summary>
    [HttpGet("{id:int}/duplicates")]
    public async Task<IActionResult> Duplicates(int id, string? projectKey, bool refresh, [FromServices] JiraService jira, CancellationToken ct)
    {
        var bug = await Full().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bug == null) return NotFound();
        var project = ResolveProject(string.IsNullOrWhiteSpace(projectKey) ? bug.JiraProjectKey : projectKey);
        if (project == null) return BadRequest(new { message = "Enter a Jira project key." });
        if (!cfg.JiraConfigured) return StatusCode(503, new { message = "Jira credentials are not configured on the server." });
        try
        {
            var snap = await jira.GetDossierAsync(project, refresh, ct);
            var top = JiraService.ScoreAll(bug, snap).Take(5).Select(x => new
            {
                x.Issue.Key, x.Issue.Summary, x.Issue.Status, score = Math.Round(x.Score, 2), x.Reason,
                wouldLink = x.Score >= cfg.JiraDuplicateThreshold, url = $"{cfg.JiraBaseUrl}/browse/{x.Issue.Key}"
            }).ToList();
            return Ok(new
            {
                projectKey = project, openTickets = snap.Issues.Count, fetchedAtUtc = snap.FetchedAtUtc, truncated = snap.Truncated,
                issueType = cfg.JiraIssueType, countedStatuses = cfg.JiraDuplicateStatuses, excludedStatuses = snap.Excluded,
                threshold = cfg.JiraDuplicateThreshold, checkEnabled = cfg.JiraDuplicateCheck, candidates = top
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        { return StatusCode(502, new { message = ex.Message }); }
    }

    [HttpPost("{id:int}/retriage")]
    public async Task<IActionResult> Retriage(int id, CancellationToken ct)
    {
        var bug = await Full().FirstOrDefaultAsync(b => b.Id == id);
        if (bug == null) return NotFound();
        if (bug.Status != BugStatus.Draft || bug.JiraState != JiraState.NotSent) return Conflict(new { message = "Only unsent draft bugs can be re-triaged." });
        try { await triage.RetriageAsync(bug, ct); return Ok(Dto.Bug(bug)); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("merge")]
    public async Task<IActionResult> Merge(MergeRequest r, CancellationToken ct)
    {
        var ids = r.BugIds?.Distinct().ToList() ?? [];
        if (ids.Count < 2) return BadRequest(new { message = "Select at least two bugs to merge." });
        var bugs = await Full().Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        if (bugs.Count != ids.Count) return NotFound(new { message = "One or more bugs were not found." });
        if (bugs.Select(b => b.RunId).Distinct().Count() > 1) return BadRequest(new { message = "Only bugs from the same run can be merged." });
        if (bugs.Any(b => b.Status != BugStatus.Draft || b.JiraState != JiraState.NotSent))
            return BadRequest(new { message = "Only draft bugs that have not been sent to Jira can be merged." });
        if (!cfg.AnthropicConfigured) return StatusCode(503, new { message = "ANTHROPIC_API_KEY is not set on the server." });

        try
        {
            var merged = await triage.MergeAsync(bugs, ct);
            var fresh = await Full().FirstAsync(b => b.Id == merged.Id, ct);
            return Ok(Dto.Bug(fresh));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        { return StatusCode(502, new { message = "AI merge failed: " + ex.Message }); }
    }

    [HttpPost("jira")]
    public async Task<IActionResult> CreateInJira(JiraCreateRequest r, CancellationToken ct)
    {
        var ids = r.BugIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return BadRequest(new { message = "Select at least one bug." });
        var project = ResolveProject(r.ProjectKey);
        if (project == null) return BadRequest(new { message = "Enter a Jira project key (or set JIRA_PROJECT_KEY on the server)." });
        if (!cfg.JiraConfigured) return StatusCode(503, new { message = "Jira credentials are not configured on the server." });

        var bugs = await Full().Where(b => ids.Contains(b.Id) && b.Status == BugStatus.Draft && b.JiraState == JiraState.NotSent).ToListAsync(ct);
        foreach (var b in bugs) await triage.PushToJiraAsync(b, project, r.AffectsVersion, r.FixVersion, CurrentUser.Name(User), r.AllowDuplicate, ct);
        return Ok(bugs.Select(Dto.Bug));
    }

    [HttpPost("{id:int}/jira/retry")]
    public async Task<IActionResult> Retry(int id, JiraRetryRequest r, CancellationToken ct)
    {
        var bug = await Full().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bug == null) return NotFound();
        // A created bug can also be "retried" to add a story link that was missing or has since been entered.
        var linkPending = bug.JiraState == JiraState.Created && bug.JiraKey != null && !bug.StoryLinked && StoryKeys.Extract(bug.StoryKey).Count > 0;
        if (bug.JiraState is not (JiraState.Failed or JiraState.PartiallyCreated) && !linkPending) return Conflict(new { message = "Only failed or partially created bugs can be retried." });
        var project = ResolveProject(string.IsNullOrWhiteSpace(r.ProjectKey) ? bug.JiraProjectKey : r.ProjectKey);
        if (bug.JiraKey == null && project == null) return BadRequest(new { message = "Enter a Jira project key (or set JIRA_PROJECT_KEY on the server)." });
        if (!cfg.JiraConfigured) return StatusCode(503, new { message = "Jira credentials are not configured on the server." });

        await triage.PushToJiraAsync(bug, project ?? "", r.AffectsVersion ?? bug.JiraAffectsVersion, r.FixVersion ?? bug.JiraFixVersion, CurrentUser.Name(User), r.AllowDuplicate, ct);
        return Ok(Dto.Bug(bug));
    }

    /// <summary>
    /// Takes a submitted bug back to "not sent" so its project key / versions can be changed and it can be submitted again.
    /// A Jira issue that was already created is left alone in Jira (it is not deleted) but detached from this bug.
    /// </summary>
    [HttpPost("{id:int}/jira/reopen")]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct)
    {
        var bug = await Full().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bug == null) return NotFound();
        if (bug.Status != BugStatus.Draft) return Conflict(new { message = "Only active bugs can be reopened." });
        if (bug.JiraState == JiraState.NotSent) return Conflict(new { message = "This bug has not been submitted to Jira." });

        var was = bug.JiraKey == null ? ""
            : bug.JiraState == JiraState.Linked ? $" (was linked to {bug.JiraKey}; that issue is not changed in Jira)"
            : $" (was {bug.JiraKey}; that issue is not deleted in Jira)";
        db.JiraAttempts.Add(new JiraAttempt { BugId = bug.Id, Success = true, JiraKey = bug.JiraKey, By = CurrentUser.Name(User),
            Message = $"Reopened{was}. A new issue will be created when it is submitted again." });
        bug.JiraState = JiraState.NotSent; bug.JiraKey = null; bug.JiraUrl = null; bug.JiraError = null; bug.StoryLinked = false;
        foreach (var a in bug.Failures.SelectMany(f => f.Attachments)) { a.JiraUploaded = false; a.JiraError = null; }
        bug.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(Dto.Bug(bug));
    }

    [HttpGet("{id:int}/attempts")]
    public async Task<IActionResult> Attempts(int id) =>
        Ok(await db.JiraAttempts.Where(a => a.BugId == id).OrderByDescending(a => a.At)
            .Select(a => new { a.Id, a.At, a.Success, a.Message, a.JiraKey, a.By }).ToListAsync());

    private string? ResolveProject(string? requested)
    {
        var p = string.IsNullOrWhiteSpace(requested) ? cfg.JiraProjectKey : requested.Trim();
        return p != null && System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Za-z][A-Za-z0-9_]{1,19}$") ? p.ToUpperInvariant() : null;
    }
}

[ApiController, Route("api/system"), Authorize]
public class SystemController(IntegrationConfig cfg) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult Config() => Ok(new
    {
        anthropicConfigured = cfg.AnthropicConfigured, model = cfg.Model,
        jiraConfigured = cfg.JiraConfigured, jiraBaseUrl = cfg.JiraBaseUrl, jiraProjectKey = cfg.JiraProjectKey, jiraIssueType = cfg.JiraIssueType,
        jiraAffectsVersion = cfg.JiraAffectsVersion
    });
}
