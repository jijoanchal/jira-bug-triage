using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;
using Triage.Api.Services;

namespace Triage.Api.Controllers;

public record SetBudgetRequest(double? LimitUsd);

/// <summary>
/// AI usage and governance: token and cost reporting per month (UTC), and monthly spend limits. Admins see everyone
/// (or one user); other users only ever see their own usage.
/// </summary>
[ApiController, Route("api/usage"), Authorize]
public class UsageController(AppDb db, BudgetService budgets, AiOptions options) : ControllerBase
{
    private const int MaxRecords = 1000;

    /// <summary>Admins may pick any user (or all); everyone else is fixed to themselves.</summary>
    private Guid? Scope(Guid? user) => User.IsInRole(Roles.Admin) ? user : CurrentUser.Id(User);

    /// <summary>The signed-in user's budget position, for the header chip.</summary>
    [HttpGet("budget")]
    public async Task<IActionResult> MyBudget(CancellationToken ct)
    {
        var s = await budgets.StatusAsync(db, CurrentUser.Id(User), ct);
        return Ok(new
        {
            s.Month, s.LimitUsd, s.SpentUsd, s.Percent, s.Level, s.LimitSource,
            warningPercent = options.WarningPercent, enforced = options.EnforceBudget
        });
    }

    [HttpGet("months")]
    public async Task<IActionResult> Months(Guid? user, CancellationToken ct)
    {
        var scope = Scope(user);
        var q = db.AiUsages.AsNoTracking().Where(x => scope == null || x.UserId == scope);
        var stamps = await q.Select(x => x.TimestampUtc).ToListAsync(ct);
        var months = stamps.Select(BudgetService.MonthOf).Append(BudgetService.MonthOf(DateTime.UtcNow))
            .Distinct().OrderByDescending(m => m).ToList();
        return Ok(months);
    }

    [HttpGet("report")]
    public async Task<IActionResult> Report(string? month, Guid? user, CancellationToken ct)
    {
        month ??= BudgetService.MonthOf(DateTime.UtcNow);
        if (!System.Text.RegularExpressions.Regex.IsMatch(month, @"^\d{4}-(0[1-9]|1[0-2])$")) return BadRequest(new { message = "month must be yyyy-MM." });
        var scope = Scope(user);
        var (from, to) = BudgetService.Range(month);

        var rows = await db.AiUsages.AsNoTracking()
            .Where(x => x.TimestampUtc >= from && x.TimestampUtc < to && (scope == null || x.UserId == scope))
            .OrderByDescending(x => x.TimestampUtc).ToListAsync(ct);

        // Month totals count every call; "calls" are the ones that reached the model (blocked ones never did).
        var reached = rows.Where(r => r.Status != "blocked").ToList();
        var totals = new
        {
            calls = reached.Count,
            ok = rows.Count(r => r.Status == "ok"),
            failed = rows.Count(r => r.Status is "error" or "bad-output"),
            blocked = rows.Count(r => r.Status == "blocked"),
            retries = reached.Sum(r => Math.Max(0, r.Attempts - 1)),
            inputTokens = rows.Sum(r => r.InputTokens),
            cacheWriteTokens = rows.Sum(r => r.CacheWriteTokens),
            cacheReadTokens = rows.Sum(r => r.CacheReadTokens),
            outputTokens = rows.Sum(r => r.OutputTokens),
            totalTokens = rows.Sum(r => r.TotalTokens),
            costUsd = rows.Sum(r => r.CostUsd),
            avgDurationMs = reached.Count == 0 ? 0 : reached.Average(r => (double)r.DurationMs),
            unpricedCalls = reached.Count(r => !r.Priced),
            users = rows.Select(r => r.UserName).Distinct().Count()
        };

        object Group(IEnumerable<AiUsage> src, Func<AiUsage, string> key) => src.GroupBy(key)
            .Select(g => new
            {
                key = g.Key,
                calls = g.Count(r => r.Status != "blocked"),
                failed = g.Count(r => r.Status is "error" or "bad-output"),
                totalTokens = g.Sum(r => r.TotalTokens),
                inputTokens = g.Sum(r => r.InputTokens + r.CacheWriteTokens + r.CacheReadTokens),
                outputTokens = g.Sum(r => r.OutputTokens),
                costUsd = g.Sum(r => r.CostUsd),
                priced = g.All(r => r.Priced || r.Status == "blocked")
            }).OrderByDescending(x => x.costUsd).ThenByDescending(x => x.calls).ToList();

        var allBudgets = await db.AiBudgets.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        // Spend against budget is always this calendar month, whichever month is being viewed.
        var (nowFrom, nowTo) = BudgetService.Range(BudgetService.MonthOf(DateTime.UtcNow));
        var spendNow = (await db.AiUsages.AsNoTracking().Where(x => x.TimestampUtc >= nowFrom && x.TimestampUtc < nowTo && x.UserId != null)
            .GroupBy(x => x.UserId).Select(g => new { Id = g.Key, Spent = g.Sum(x => x.CostUsd) }).ToListAsync(ct))
            .ToDictionary(x => x.Id!.Value, x => x.Spent);

        var byUser = rows.GroupBy(r => new { r.UserId, r.UserName }).Select(g =>
        {
            var limit = g.Key.UserId is { } id ? allBudgets.FirstOrDefault(b => b.Scope == id.ToString())?.LimitUsd : null;
            var spent = g.Key.UserId is { } sid && spendNow.TryGetValue(sid, out var sp) ? sp : 0;
            return new
            {
                userId = g.Key.UserId, userName = g.Key.UserName,
                displayName = g.Key.UserId is { } uid && users.TryGetValue(uid, out var u) ? u.DisplayName : null,
                calls = g.Count(r => r.Status != "blocked"),
                failed = g.Count(r => r.Status is "error" or "bad-output"),
                blocked = g.Count(r => r.Status == "blocked"),
                totalTokens = g.Sum(r => r.TotalTokens), costUsd = g.Sum(r => r.CostUsd),
                lastUsedUtc = g.Max(r => r.TimestampUtc),
                monthlyLimitUsd = limit, spentThisMonthUsd = spent,
                percent = limit is > 0 ? (double?)(spent / limit.Value * 100) : null
            };
        }).OrderByDescending(x => x.costUsd).ToList();

        var byDay = rows.GroupBy(r => r.TimestampUtc.ToString("yyyy-MM-dd")).OrderBy(g => g.Key)
            .Select(g => new { day = g.Key, calls = g.Count(r => r.Status != "blocked"), totalTokens = g.Sum(r => r.TotalTokens), costUsd = g.Sum(r => r.CostUsd) }).ToList();

        return Ok(new
        {
            month, user = scope, totals,
            byUser, byDay,
            userBudgets = User.IsInRole(Roles.Admin)
                ? users.Values.OrderBy(u => u.DisplayName).Select(u =>
                {
                    var lim = allBudgets.FirstOrDefault(b => b.Scope == u.Id.ToString())?.LimitUsd;
                    var sp = spendNow.TryGetValue(u.Id, out var s0) ? s0 : 0;
                    return new { userId = u.Id, u.DisplayName, u.Email, limitUsd = lim, spentUsd = sp, percent = lim is > 0 ? (double?)(sp / lim.Value * 100) : null };
                }).ToList()
                : null,
            byModel = Group(reached, r => r.Model),
            byOperation = Group(rows, r => r.Operation),
            records = rows.Take(MaxRecords).Select(r => new
            {
                r.Id, r.TimestampUtc, r.UserName, r.Operation, r.RunId, r.BugId, r.FailureId, r.Model, r.Status, r.HttpStatus,
                r.Attempts, r.InputTokens, r.CacheWriteTokens, r.CacheReadTokens, r.OutputTokens, r.TotalTokens, r.CostUsd,
                r.Priced, r.DurationMs, r.RequestId, r.Error
            }),
            recordsTruncated = rows.Count > MaxRecords
        });
    }

    /// <summary>Price list and governance settings in force (read-only; set in appsettings under "Ai").</summary>
    [HttpGet("policy")]
    public async Task<IActionResult> Policy(CancellationToken ct)
    {
        var globalLimit = (await db.AiBudgets.AsNoTracking().FirstOrDefaultAsync(b => b.Scope == "global", ct))?.LimitUsd;
        return Ok(new
        {
            enforced = options.EnforceBudget,
            warningPercent = options.WarningPercent,
            globalLimitUsd = globalLimit ?? options.MonthlyBudgetUsd,
            globalLimitSource = globalLimit != null ? "admin" : options.MonthlyBudgetUsd != null ? "config" : null,
            models = options.Models.Select(m => new { m.Id, m.Label, m.InputPerMTok, m.OutputPerMTok, m.CacheReadPerMTok })
        });
    }

    /// <summary>Sets or clears a monthly limit. scope is "global" or a user id. A null or zero limit removes it.</summary>
    [HttpPut("budgets/{scope}"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SetBudget(string scope, SetBudgetRequest r, CancellationToken ct)
    {
        if (scope != "global" && !Guid.TryParse(scope, out _)) return BadRequest(new { message = "scope must be 'global' or a user id." });
        if (scope != "global" && !await db.Users.AnyAsync(u => u.Id == Guid.Parse(scope), ct)) return NotFound(new { message = "User not found." });
        if (r.LimitUsd is < 0 or > 1_000_000) return BadRequest(new { message = "The limit must be between $0 and $1,000,000." });

        scope = scope == "global" ? scope : Guid.Parse(scope).ToString();
        var existing = await db.AiBudgets.FirstOrDefaultAsync(b => b.Scope == scope, ct);
        if (r.LimitUsd is null or 0)
        {
            if (existing != null) db.AiBudgets.Remove(existing);
        }
        else if (existing == null)
            db.AiBudgets.Add(new AiBudget { Scope = scope, LimitUsd = r.LimitUsd.Value, UpdatedBy = CurrentUser.Name(User) });
        else
        {
            existing.LimitUsd = r.LimitUsd.Value; existing.UpdatedAt = DateTime.UtcNow; existing.UpdatedBy = CurrentUser.Name(User);
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
