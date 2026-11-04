using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;
using Triage.Api.Services;

namespace Triage.Api.Controllers;

public static class Dto
{
    public static object Bug(Data.Bug b) => new
    {
        b.Id, b.RunId, b.Summary, b.Description, b.Labels, b.Severity, b.Status, b.MergedIntoId, b.AiNote,
        b.JiraState, b.JiraKey, b.JiraUrl, b.JiraError, b.JiraProjectKey, b.JiraAffectsVersion, b.JiraFixVersion, b.StoryKey, b.StoryLinked, b.JiraAttempts, b.JiraLastAttemptAt, b.CreatedAt, b.UpdatedAt,
        Failures = b.Failures.Select(f => new
        {
            f.Id, f.Title, f.File, f.Line, f.Project, f.Status, f.Retries, f.DurationMs, f.ErrorMessage, f.Stack, f.Snippet, f.StoryKey,
            Attachments = f.Attachments.Select(a => new { a.Id, a.FileName, a.ContentType, a.Size, Available = a.StoragePath != null, a.JiraUploaded, a.JiraError })
        })
    };

    public static object Run(TestRun r) => new
    {
        r.Id, r.Name, r.FileName, r.UploadedAt, r.UploadedByName, r.Status, r.Error,
        r.TotalTests, r.Passed, r.Failed, r.Skipped, r.Flaky, r.DurationMs
    };
}

[ApiController, Route("api/runs"), Authorize]
public class RunsController(AppDb db, ArtifactStore store, TriageService triage, IntegrationConfig cfg) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var runs = await db.Runs.OrderByDescending(r => r.UploadedAt).ToListAsync();
        var bugs = await db.Bugs.Select(b => new { b.RunId, b.Status, b.JiraState }).ToListAsync();
        return Ok(runs.Select(r =>
        {
            var rb = bugs.Where(b => b.RunId == r.Id).ToList();
            var active = rb.Where(b => b.Status == BugStatus.Draft).ToList();
            return new
            {
                Run = Dto.Run(r),
                Bugs = active.Count,
                Merged = rb.Count(b => b.Status == BugStatus.Merged),
                Discarded = rb.Count(b => b.Status == BugStatus.Discarded),
                Created = active.Count(b => b.JiraState is JiraState.Created or JiraState.PartiallyCreated),
                Linked = active.Count(b => b.JiraState == JiraState.Linked),
                JiraFailed = active.Count(b => b.JiraState == JiraState.Failed || b.JiraState == JiraState.PartiallyCreated),
                Pending = active.Count(b => b.JiraState == JiraState.NotSent)
            };
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var run = await db.Runs.FindAsync(id);
        if (run == null) return NotFound();
        var bugs = await db.Bugs.AsSplitQuery().Include(b => b.Failures).ThenInclude(f => f.Attachments)
            .Where(b => b.RunId == id).OrderBy(b => b.Id).ToListAsync();
        return Ok(new { run = Dto.Run(run), bugs = bugs.Select(Dto.Bug) });
    }

    [HttpPost, DisableRequestSizeLimit, RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload([FromForm] string? name)
    {
        var form = await Request.ReadFormAsync();
        var report = form.Files.FirstOrDefault(f => f.Name == "report");
        if (report == null) return BadRequest(new { message = "Attach the Playwright JSON report as form field 'report'." });
        var stepsFile = form.Files.FirstOrDefault(f => f.Name == "steps");

        ParsedRun parsed;
        try
        {
            await using var s = report.OpenReadStream();
            if (report.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                parsed = ZephyrCsvParser.ToRun(ZephyrCsvParser.Parse(s));
            else
            {
                parsed = PlaywrightParser.Parse(s);
                if (stepsFile != null)
                {
                    // Enrich each failure with the matching Zephyr test case (objective, precondition, steps).
                    await using var cs = stepsFile.OpenReadStream();
                    var cases = ZephyrCsvParser.Parse(cs).Cases.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
                    parsed = parsed with
                    {
                        Failures = parsed.Failures.Select(f =>
                            ZephyrCsvParser.KeyOf(f.Title) is { } key && cases.TryGetValue(key, out var c)
                                ? f with { Snippet = c.Describe(), StoryKey = f.StoryKey ?? c.StoryKey } : f).ToList()
                    };
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        { return BadRequest(new { message = ex.Message }); }

        ArtifactStore.Pool pool;
        try { pool = await store.BuildPoolAsync(form.Files.Where(f => f.Name == "artifacts")); }
        catch (InvalidDataException ex) { return BadRequest(new { message = "Could not read the artifacts zip: " + ex.Message }); }

        var run = new TestRun
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Run {DateTime.Now:yyyy-MM-dd HH:mm}" : name.Trim(),
            FileName = report.FileName,
            UploadedById = CurrentUser.Id(User), UploadedByName = CurrentUser.Name(User),
            TotalTests = parsed.Total, Passed = parsed.Passed, Failed = parsed.Failed, Skipped = parsed.Skipped, Flaky = parsed.Flaky,
            DurationMs = parsed.DurationMs
        };
        foreach (var pf in parsed.Failures)
        {
            run.Failures.Add(new TestFailure
            {
                Title = pf.Title, File = pf.File, Line = pf.Line, Project = pf.Project, Status = pf.Status, Retries = pf.Retries,
                DurationMs = pf.DurationMs, ErrorMessage = pf.ErrorMessage, Stack = pf.Stack, Snippet = pf.Snippet, StoryKey = pf.StoryKey
            });
        }
        db.Runs.Add(run);
        await db.SaveChangesAsync(); // ids are needed for the storage paths

        var mime = new FileExtensionContentTypeProvider();
        for (var i = 0; i < parsed.Failures.Count; i++)
        {
            var failure = run.Failures[i];
            foreach (var pa in parsed.Failures[i].Attachments)
            {
                var data = pa.Body ?? (pa.Path != null ? store.Resolve(pa.Path, pool) : null);
                var ext = Path.GetExtension(pa.Path ?? "");
                var fileName = pa.Name.Contains('.') || ext.Length == 0 ? pa.Name : pa.Name + ext;
                var ct = pa.ContentType;
                if (pa.Path != null && ct == "application/octet-stream" && mime.TryGetContentType(pa.Path, out var guessed)) ct = guessed;
                var att = new FailureAttachment { FailureId = failure.Id, FileName = fileName, ContentType = ct };
                if (data != null)
                {
                    att.StoragePath = await store.SaveAsync(run.Id, failure.Id, $"{failure.Attachments.Count + 1}-{fileName}", data);
                    att.Size = data.Length;
                }
                failure.Attachments.Add(att);
            }
        }
        await db.SaveChangesAsync();

        var missing = run.Failures.SelectMany(f => f.Attachments).Count(a => a.StoragePath == null);
        return Ok(new { run = Dto.Run(run), missingArtifacts = missing });
    }

    [HttpPost("{id:int}/triage")]
    public async Task<IActionResult> Triage(int id, CancellationToken ct)
    {
        if (!cfg.AnthropicConfigured) return StatusCode(503, new { message = "ANTHROPIC_API_KEY is not set on the server." });
        try { var n = await triage.TriageRunAsync(id, ct); return Ok(new { triaged = n }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
}

[ApiController, Route("api/attachments"), Authorize]
public class AttachmentsController(AppDb db, ArtifactStore store) : ControllerBase
{
    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> File(int id)
    {
        var a = await db.Attachments.FindAsync(id);
        if (a?.StoragePath == null) return NotFound();
        var abs = store.Abs(a.StoragePath);
        return System.IO.File.Exists(abs) ? PhysicalFile(abs, a.ContentType, a.FileName) : NotFound();
    }
}
