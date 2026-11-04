using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;

namespace Triage.Api.Services;

/// <summary>Adds columns introduced after a database was first created (EnsureCreated never alters existing tables).</summary>
public static class SchemaUpgrades
{
    private enum Kind { Text, Flag }

    public static void EnsureBugJiraColumns(AppDb db, string provider)
    {
        foreach (var col in new[] { "JiraProjectKey", "JiraAffectsVersion", "JiraFixVersion" }) AddColumn(db, provider, "Bugs", col, Kind.Text);
    }

    /// <summary>Story link columns: the story key(s) on bugs and failures, and whether the Jira issue has been linked to them.</summary>
    public static void EnsureStoryColumns(AppDb db, string provider)
    {
        AddColumn(db, provider, "Bugs", "StoryKey", Kind.Text);
        AddColumn(db, provider, "Bugs", "StoryLinked", Kind.Flag);
        AddColumn(db, provider, "Failures", "StoryKey", Kind.Text);
    }

    // Table and column names are constants from this file, never user input.
#pragma warning disable EF1002
    private static void AddColumn(AppDb db, string provider, string table, string col, Kind kind)
    {
        switch (provider.ToLowerInvariant())
        {
            case "sqlserver":
                db.Database.ExecuteSqlRaw($"IF COL_LENGTH('{table}', '{col}') IS NULL ALTER TABLE {table} ADD {col} " +
                    (kind == Kind.Flag ? "BIT NOT NULL DEFAULT 0" : "NVARCHAR(MAX) NULL"));
                break;
            case "postgres" or "postgresql":
                db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS \"{col}\" " +
                    (kind == Kind.Flag ? "BOOLEAN NOT NULL DEFAULT FALSE" : "TEXT NULL"));
                break;
            default:
                var exists = db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') WHERE name = {{0}}", col).AsEnumerable().First() > 0;
                if (!exists) db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {col} " + (kind == Kind.Flag ? "INTEGER NOT NULL DEFAULT 0" : "TEXT NULL"));
                break;
        }
    }
#pragma warning restore EF1002
}
