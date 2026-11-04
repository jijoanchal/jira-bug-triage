namespace Triage.Api.Data;

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public static class BugStatus
{
    public const string Draft = "Draft";
    public const string Merged = "Merged";
    public const string Discarded = "Discarded";
}

public static class JiraState
{
    public const string NotSent = "NotSent";
    public const string Created = "Created";
    public const string PartiallyCreated = "PartiallyCreated"; // issue exists, some attachments failed
    public const string Failed = "Failed";
    public const string Linked = "Linked"; // already tracked by an open Jira issue; no new issue was created
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.User;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>One uploaded Playwright result file = one iteration.</summary>
public class TestRun
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public Guid UploadedById { get; set; }
    public string UploadedByName { get; set; } = "";
    /// <summary>Uploaded, Triaging, Triaged, TriageFailed</summary>
    public string Status { get; set; } = "Uploaded";
    public string? Error { get; set; }
    public int TotalTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Flaky { get; set; }
    public double DurationMs { get; set; }
    public List<TestFailure> Failures { get; set; } = new();
    public List<Bug> Bugs { get; set; } = new();
}

public class TestFailure
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public TestRun? Run { get; set; }
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Project { get; set; } = "";
    public string Status { get; set; } = "";
    public int Retries { get; set; }
    public double DurationMs { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? Stack { get; set; }
    public string? Snippet { get; set; }
    /// <summary>Jira story key(s) the report says this test covers (comma-separated); null when the report names none.</summary>
    public string? StoryKey { get; set; }
    /// <summary>Bug this failure currently belongs to (changes on merge).</summary>
    public int? BugId { get; set; }
    public Bug? Bug { get; set; }
    public List<FailureAttachment> Attachments { get; set; } = new();
}

public class FailureAttachment
{
    public int Id { get; set; }
    public int FailureId { get; set; }
    public TestFailure? Failure { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    /// <summary>Relative to the data directory. Null when the file referenced by the JSON could not be found.</summary>
    public string? StoragePath { get; set; }
    public long Size { get; set; }
    public bool JiraUploaded { get; set; }
    public string? JiraError { get; set; }
}

public class Bug
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public TestRun? Run { get; set; }
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Labels { get; set; } = new();
    public string Severity { get; set; } = "Medium";
    public string Status { get; set; } = BugStatus.Draft;
    public int? MergedIntoId { get; set; }
    /// <summary>Set when AI triage failed and the draft was built from the raw failure.</summary>
    public string? AiNote { get; set; }
    public string JiraState { get; set; } = Data.JiraState.NotSent;
    public string? JiraKey { get; set; }
    public string? JiraUrl { get; set; }
    public string? JiraError { get; set; }
    /// <summary>What the bug was last submitted to Jira with; editable and reused on retry or after reopening.</summary>
    public string? JiraProjectKey { get; set; }
    public string? JiraAffectsVersion { get; set; }
    public string? JiraFixVersion { get; set; }
    /// <summary>Story (comma-separated keys) the Jira issue is linked to; taken from the report, editable, and asked for when missing.</summary>
    public string? StoryKey { get; set; }
    /// <summary>True once the Jira issue has been linked to every key in <see cref="StoryKey"/>.</summary>
    public bool StoryLinked { get; set; }
    public int JiraAttempts { get; set; }
    public DateTime? JiraLastAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TestFailure> Failures { get; set; } = new();
    public List<JiraAttempt> Attempts { get; set; } = new();
}

public class JiraAttempt
{
    public int Id { get; set; }
    public int BugId { get; set; }
    public Bug? Bug { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? JiraKey { get; set; }
    public string By { get; set; } = "";
}

/// <summary>One call to the Anthropic API (successful or not), with its token counts and estimated cost.</summary>
public class AiUsage
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "system";
    /// <summary>triage, retriage or merge.</summary>
    public string Operation { get; set; } = "";
    public int? RunId { get; set; }
    public int? BugId { get; set; }
    public int? FailureId { get; set; }
    public string Model { get; set; } = "";
    /// <summary>ok, bad-output (tokens were spent but the reply was unusable), error, or blocked (budget).</summary>
    public string Status { get; set; } = "ok";
    public int? HttpStatus { get; set; }
    /// <summary>HTTP attempts made, including retried 429/5xx responses (which carry no usage).</summary>
    public int Attempts { get; set; } = 1;
    public long InputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double CostUsd { get; set; }
    /// <summary>False when the model has no price configured, so CostUsd is 0 rather than a real figure.</summary>
    public bool Priced { get; set; } = true;
    public long DurationMs { get; set; }
    public string? RequestId { get; set; }
    public string? Error { get; set; }
}

/// <summary>A monthly spend limit: Scope is "global" or a user id.</summary>
public class AiBudget
{
    public int Id { get; set; }
    public string Scope { get; set; } = "";
    public double LimitUsd { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
}
