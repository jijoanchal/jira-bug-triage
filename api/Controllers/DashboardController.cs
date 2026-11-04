using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;

namespace Triage.Api.Controllers;

[ApiController, Route("api/dashboard"), Authorize]
public class DashboardController(AppDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var runs = await db.Runs.OrderBy(r => r.UploadedAt).ToListAsync();
        var bugs = await db.Bugs.Select(b => new { b.RunId, b.Status, b.JiraState }).ToListAsync();

        var history = runs.Select(r =>
        {
            var rb = bugs.Where(b => b.RunId == r.Id).ToList();
            var active = rb.Where(b => b.Status == BugStatus.Draft).ToList();
            return new
            {
                r.Id, r.Name, r.UploadedAt, r.Status, r.TotalTests, r.Passed, r.Failed, r.Skipped, r.Flaky,
                Bugs = active.Count,
                Merged = rb.Count(b => b.Status == BugStatus.Merged),
                Discarded = rb.Count(b => b.Status == BugStatus.Discarded),
                Created = active.Count(b => b.JiraState == JiraState.Created),
                Linked = active.Count(b => b.JiraState == JiraState.Linked),
                Partial = active.Count(b => b.JiraState == JiraState.PartiallyCreated),
                JiraFailed = active.Count(b => b.JiraState == JiraState.Failed),
                Pending = active.Count(b => b.JiraState == JiraState.NotSent)
            };
        }).ToList();

        var open = bugs.Where(b => b.Status == BugStatus.Draft).ToList();
        return Ok(new
        {
            totals = new
            {
                runs = runs.Count,
                failedTests = runs.Sum(r => r.Failed),
                bugs = open.Count,
                merged = bugs.Count(b => b.Status == BugStatus.Merged),
                created = open.Count(b => b.JiraState == JiraState.Created),
                linked = open.Count(b => b.JiraState == JiraState.Linked),
                partial = open.Count(b => b.JiraState == JiraState.PartiallyCreated),
                jiraFailed = open.Count(b => b.JiraState == JiraState.Failed),
                pending = open.Count(b => b.JiraState == JiraState.NotSent)
            },
            history
        });
    }
}
