using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Triage.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TestRun> Runs => Set<TestRun>();
    public DbSet<TestFailure> Failures => Set<TestFailure>();
    public DbSet<FailureAttachment> Attachments => Set<FailureAttachment>();
    public DbSet<Bug> Bugs => Set<Bug>();
    public DbSet<JiraAttempt> JiraAttempts => Set<JiraAttempt>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<AiBudget> AiBudgets => Set<AiBudget>();

    // All timestamps are UTC; mark them as such when read back so JSON carries a "Z" suffix on every provider.
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    private class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<AiUsage>().ToTable("AiUsage").HasIndex(u => u.TimestampUtc);
        b.Entity<AiBudget>().ToTable("AiBudget").HasIndex(x => x.Scope).IsUnique();

        // Labels are stored as a JSON string so the schema stays portable across SQLite / SQL Server / PostgreSQL.
        var comparer = new ValueComparer<List<string>>(
            (x, y) => x != null && y != null && x.SequenceEqual(y),
            x => x.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            x => x.ToList());
        b.Entity<Bug>().Property(x => x.Labels)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(comparer);

        b.Entity<TestRun>().HasMany(r => r.Failures).WithOne(f => f.Run).HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestRun>().HasMany(r => r.Bugs).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Bug>().HasMany(x => x.Failures).WithOne(f => f.Bug).HasForeignKey(f => f.BugId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Bug>().HasMany(x => x.Attempts).WithOne(a => a.Bug).HasForeignKey(a => a.BugId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestFailure>().HasMany(f => f.Attachments).WithOne(a => a.Failure).HasForeignKey(a => a.FailureId).OnDelete(DeleteBehavior.Cascade);
    }
}
