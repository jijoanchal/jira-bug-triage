using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;

namespace Triage.Api.Services;

/// <summary>What an AI call is for, so its usage can be traced back to a run, bug or failure.</summary>
public record AiCallInfo(string Operation, int? RunId = null, int? BugId = null, int? FailureId = null);

public class BudgetExceededException(string message) : InvalidOperationException(message);

/// <summary>A model's list prices in USD per million tokens (cost estimates only). Configured under Ai:Models.</summary>
public class ModelPrice
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CacheReadPerMTok { get; set; }

    /// <summary>Cache writes are billed at 1.25x input (5-minute cache).</summary>
    public double Cost(long input, long cacheWrite, long cacheRead, long output) =>
        (double)((input * InputPerMTok + cacheWrite * InputPerMTok * 1.25m + cacheRead * CacheReadPerMTok + output * OutputPerMTok) / 1_000_000m);
}

/// <summary>Governance settings from the "Ai" configuration section.</summary>
public class AiOptions
{
    public List<ModelPrice> Models { get; } = new();
    /// <summary>Org-wide monthly limit in USD; null = none. An admin can also set it in the UI, which takes priority.</summary>
    public double? MonthlyBudgetUsd { get; }
    /// <summary>When true, AI calls are refused once a limit is reached. When false, limits only warn.</summary>
    public bool EnforceBudget { get; }
    public double WarningPercent { get; } = 80;

    public AiOptions(IConfiguration config)
    {
        var s = config.GetSection("Ai");
        s.GetSection("Models").Bind(Models);
        MonthlyBudgetUsd = s.GetValue<double?>("MonthlyBudgetUsd");
        EnforceBudget = s.GetValue("EnforceBudget", false);
        WarningPercent = s.GetValue("WarningPercent", 80d);
    }

    public ModelPrice? Price(string model) => Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Writes the usage log in its own scope, so a failed or cancelled request still leaves a record.</summary>
public class UsageRecorder(IServiceScopeFactory scopes, ILogger<UsageRecorder> log)
{
    public async Task RecordAsync(AiUsage u)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.AiUsages.Add(u);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never fail the user's action because the log could not be written.
            log.LogError(ex, "Could not record AI usage for {Operation} ({Model})", u.Operation, u.Model);
        }
    }
}

public record BudgetStatus(string Month, double? LimitUsd, double SpentUsd, double? Percent, string Level, string? LimitSource);

/// <summary>Monthly spend limits (per user and global), and the check that runs before each AI call.</summary>
public class BudgetService(IServiceScopeFactory scopes, AiOptions options)
{
    public static string MonthOf(DateTime utc) => utc.ToString("yyyy-MM");
    public static (DateTime From, DateTime To) Range(string month)
    {
        var from = DateTime.SpecifyKind(DateTime.ParseExact(month, "yyyy-MM", null), DateTimeKind.Utc);
        return (from, from.AddMonths(1));
    }

    public static string Level(double spent, double? limit, double warnPercent) =>
        limit is not > 0 ? "none" : spent >= limit ? "over" : spent / limit.Value * 100 >= warnPercent ? "warning" : "ok";

    public async Task<BudgetStatus> StatusAsync(AppDb db, Guid? userId, CancellationToken ct = default)
    {
        var month = MonthOf(DateTime.UtcNow);
        var (from, to) = Range(month);
        var month_ = db.AiUsages.Where(x => x.TimestampUtc >= from && x.TimestampUtc < to);
        var budgets = await db.AiBudgets.AsNoTracking().ToListAsync(ct);
        var globalLimit = budgets.FirstOrDefault(b => b.Scope == "global")?.LimitUsd ?? options.MonthlyBudgetUsd;
        var userLimit = userId is { } id ? budgets.FirstOrDefault(b => b.Scope == id.ToString())?.LimitUsd : null;

        var globalSpent = await month_.SumAsync(x => (double?)x.CostUsd, ct) ?? 0;
        var userSpent = userId is { } uid ? await month_.Where(x => x.UserId == uid).SumAsync(x => (double?)x.CostUsd, ct) ?? 0 : 0;

        // Report whichever limit is closest to being hit, so the user sees the one that will actually stop them.
        var candidates = new List<(double Limit, double Spent, string Source)>();
        if (userLimit is > 0) candidates.Add((userLimit.Value, userSpent, "user"));
        if (globalLimit is > 0) candidates.Add((globalLimit.Value, globalSpent, "global"));
        if (candidates.Count == 0) return new BudgetStatus(month, null, userId == null ? globalSpent : userSpent, null, "none", null);
        var worst = candidates.OrderByDescending(c => c.Spent / c.Limit).First();
        var pct = worst.Spent / worst.Limit * 100;
        return new BudgetStatus(month, worst.Limit, worst.Spent, pct, Level(worst.Spent, worst.Limit, options.WarningPercent), worst.Source);
    }

    /// <summary>Throws when enforcement is on and the user's or the global monthly limit has been reached.</summary>
    public async Task EnforceAsync(Guid? userId, CancellationToken ct)
    {
        if (!options.EnforceBudget) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var s = await StatusAsync(db, userId, ct);
        if (s.Level == "over")
            throw new BudgetExceededException(
                $"The {(s.LimitSource == "user" ? "monthly AI budget for your account" : "organisation's monthly AI budget")} " +
                $"(${s.LimitUsd:0.00}) has been reached for {s.Month} (UTC). Ask an admin to raise the limit.");
    }
}

/// <summary>Creates the usage/budget tables on databases created before they existed (EnsureCreated skips existing databases).</summary>
public static class AiSchema
{
    public static void Ensure(AppDb db, string provider)
    {
        var sql = provider.ToLowerInvariant() switch
        {
            "sqlserver" => SqlServer,
            "postgres" or "postgresql" => Postgres,
            _ => Sqlite,
        };
        foreach (var stmt in sql.Split("--GO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            db.Database.ExecuteSqlRaw(stmt);
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS AiUsage (
          Id INTEGER PRIMARY KEY AUTOINCREMENT, TimestampUtc TEXT NOT NULL, UserId TEXT NULL, UserName TEXT NOT NULL,
          Operation TEXT NOT NULL, RunId INTEGER NULL, BugId INTEGER NULL, FailureId INTEGER NULL, Model TEXT NOT NULL,
          Status TEXT NOT NULL, HttpStatus INTEGER NULL, Attempts INTEGER NOT NULL, InputTokens INTEGER NOT NULL,
          CacheWriteTokens INTEGER NOT NULL, CacheReadTokens INTEGER NOT NULL, OutputTokens INTEGER NOT NULL,
          TotalTokens INTEGER NOT NULL, CostUsd REAL NOT NULL, Priced INTEGER NOT NULL, DurationMs INTEGER NOT NULL,
          RequestId TEXT NULL, Error TEXT NULL)
        --GO
        CREATE INDEX IF NOT EXISTS IX_AiUsage_TimestampUtc ON AiUsage (TimestampUtc)
        --GO
        CREATE TABLE IF NOT EXISTS AiBudget (
          Id INTEGER PRIMARY KEY AUTOINCREMENT, Scope TEXT NOT NULL, LimitUsd REAL NOT NULL, UpdatedAt TEXT NOT NULL, UpdatedBy TEXT NOT NULL)
        --GO
        CREATE UNIQUE INDEX IF NOT EXISTS IX_AiBudget_Scope ON AiBudget (Scope)
        """;

    // Not exercised by the default SQLite setup; kept in step with the EF model for SQL Server / PostgreSQL deployments.
    private const string SqlServer = """
        IF OBJECT_ID(N'AiUsage', N'U') IS NULL CREATE TABLE AiUsage (
          Id INT IDENTITY(1,1) PRIMARY KEY, TimestampUtc DATETIME2 NOT NULL, UserId UNIQUEIDENTIFIER NULL, UserName NVARCHAR(MAX) NOT NULL,
          Operation NVARCHAR(MAX) NOT NULL, RunId INT NULL, BugId INT NULL, FailureId INT NULL, Model NVARCHAR(MAX) NOT NULL,
          Status NVARCHAR(MAX) NOT NULL, HttpStatus INT NULL, Attempts INT NOT NULL, InputTokens BIGINT NOT NULL,
          CacheWriteTokens BIGINT NOT NULL, CacheReadTokens BIGINT NOT NULL, OutputTokens BIGINT NOT NULL,
          TotalTokens BIGINT NOT NULL, CostUsd FLOAT NOT NULL, Priced BIT NOT NULL, DurationMs BIGINT NOT NULL,
          RequestId NVARCHAR(MAX) NULL, Error NVARCHAR(MAX) NULL)
        --GO
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiUsage_TimestampUtc') CREATE INDEX IX_AiUsage_TimestampUtc ON AiUsage (TimestampUtc)
        --GO
        IF OBJECT_ID(N'AiBudget', N'U') IS NULL CREATE TABLE AiBudget (
          Id INT IDENTITY(1,1) PRIMARY KEY, Scope NVARCHAR(450) NOT NULL, LimitUsd FLOAT NOT NULL, UpdatedAt DATETIME2 NOT NULL, UpdatedBy NVARCHAR(MAX) NOT NULL)
        --GO
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiBudget_Scope') CREATE UNIQUE INDEX IX_AiBudget_Scope ON AiBudget (Scope)
        """;

    private const string Postgres = """
        CREATE TABLE IF NOT EXISTS "AiUsage" (
          "Id" INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "TimestampUtc" TIMESTAMP NOT NULL, "UserId" UUID NULL, "UserName" TEXT NOT NULL,
          "Operation" TEXT NOT NULL, "RunId" INTEGER NULL, "BugId" INTEGER NULL, "FailureId" INTEGER NULL, "Model" TEXT NOT NULL,
          "Status" TEXT NOT NULL, "HttpStatus" INTEGER NULL, "Attempts" INTEGER NOT NULL, "InputTokens" BIGINT NOT NULL,
          "CacheWriteTokens" BIGINT NOT NULL, "CacheReadTokens" BIGINT NOT NULL, "OutputTokens" BIGINT NOT NULL,
          "TotalTokens" BIGINT NOT NULL, "CostUsd" DOUBLE PRECISION NOT NULL, "Priced" BOOLEAN NOT NULL, "DurationMs" BIGINT NOT NULL,
          "RequestId" TEXT NULL, "Error" TEXT NULL)
        --GO
        CREATE INDEX IF NOT EXISTS "IX_AiUsage_TimestampUtc" ON "AiUsage" ("TimestampUtc")
        --GO
        CREATE TABLE IF NOT EXISTS "AiBudget" (
          "Id" INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "Scope" TEXT NOT NULL, "LimitUsd" DOUBLE PRECISION NOT NULL,
          "UpdatedAt" TIMESTAMP NOT NULL, "UpdatedBy" TEXT NOT NULL)
        --GO
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiBudget_Scope" ON "AiBudget" ("Scope")
        """;
}
