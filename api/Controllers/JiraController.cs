using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Triage.Api.Services;

namespace Triage.Api.Controllers;

[ApiController, Route("api/jira"), Authorize]
public class JiraController(JiraService jira, IntegrationConfig cfg) : ControllerBase
{
    /// <summary>The cached open bugs of a project that duplicate detection compares against; <c>refresh=true</c> re-reads them from Jira.</summary>
    [HttpGet("dossier")]
    public async Task<IActionResult> Dossier(string? projectKey, bool refresh, CancellationToken ct)
    {
        if (!cfg.JiraConfigured) return StatusCode(503, new { message = "Jira credentials are not configured on the server." });
        if (projectKey == null || !System.Text.RegularExpressions.Regex.IsMatch(projectKey, "^[A-Za-z][A-Za-z0-9_]{1,19}$"))
            return BadRequest(new { message = "Enter a valid Jira project key." });
        try
        {
            var d = await jira.GetDossierAsync(projectKey.ToUpperInvariant(), refresh, ct);
            return Ok(new
            {
                d.ProjectKey, d.FetchedAtUtc, d.Truncated, count = d.Issues.Count, cacheMinutes = cfg.JiraDossierMinutes,
                issueType = cfg.JiraIssueType, countedStatuses = cfg.JiraDuplicateStatuses, threshold = cfg.JiraDuplicateThreshold,
                excludedStatuses = d.Excluded,
                issues = d.Issues.Select(i => new { i.Key, i.Summary, i.Status })
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        { return StatusCode(502, new { message = ex.Message }); }
    }

    /// <summary>The versions of a Jira project, for the Affects / Fix version pickers.</summary>
    [HttpGet("versions")]
    public async Task<IActionResult> Versions(string? projectKey, CancellationToken ct)
    {
        if (!cfg.JiraConfigured) return StatusCode(503, new { message = "Jira credentials are not configured on the server." });
        if (projectKey == null || !System.Text.RegularExpressions.Regex.IsMatch(projectKey, "^[A-Za-z][A-Za-z0-9_]{1,19}$"))
            return BadRequest(new { message = "Enter a valid Jira project key." });
        try { return Ok(await jira.GetVersionsAsync(projectKey.ToUpperInvariant(), ct)); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        { return StatusCode(502, new { message = ex.Message }); }
    }
}
