namespace Triage.Api.Services;

/// <summary>Reads integration secrets from environment variables. Nothing here is ever returned to the client.</summary>
public class IntegrationConfig
{
    public string? AnthropicKey { get; } = Env("ANTHROPIC_API_KEY");
    public string Model { get; } = Env("ANTHROPIC_MODEL") ?? "claude-sonnet-5-5";

    public string? JiraBaseUrl { get; }
    public string? JiraUser { get; } = Env("JIRA_API_USER") ?? Env("JIRA_EMAIL") ?? Env("CONFLUENCE_API_USER");
    public string? JiraToken { get; } = Env("JIRA_API_TOKEN") ?? Env("JIRA_API_KEY") ?? Env("CONFLUENCE_API_TOKEN");
    public string? JiraProjectKey { get; } = Env("JIRA_PROJECT_KEY");
    public string JiraIssueType { get; } = Env("JIRA_ISSUE_TYPE") ?? "Bug";
    /// <summary>Default "Affects Version/s" (comma-separated names) for projects that require it. Can be overridden per request.</summary>
    public string? JiraAffectsVersion { get; } = Env("JIRA_AFFECTS_VERSION");
    /// <summary>Optional JSON object merged into the issue fields, for other required fields, e.g. {"components":[{"name":"Web"}]}.</summary>
    public string? JiraExtraFields { get; } = Env("JIRA_EXTRA_FIELDS");
    /// <summary>Before creating an issue, look for an open one that already tracks the same failure (JIRA_DUPLICATE_CHECK=false turns it off).</summary>
    public bool JiraDuplicateCheck { get; } = !string.Equals(Env("JIRA_DUPLICATE_CHECK"), "false", StringComparison.OrdinalIgnoreCase);
    /// <summary>Status names that count as "still being handled". Matched by name, case-insensitively, so unknown names are harmless.</summary>
    public string[] JiraDuplicateStatuses { get; } = (Env("JIRA_DUPLICATE_STATUSES") ?? "Open,To Do,Planned for Sprint,Selected for Development,In Progress,Dev In Progress")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>Minutes a project's open-bug dossier is reused before Jira is read again (0 = read every time).</summary>
    public int JiraDossierMinutes { get; } = int.TryParse(Env("JIRA_DOSSIER_MINUTES"), out var dm) && dm >= 0 ? dm : 10;
    /// <summary>How alike (0-1) an existing issue's summary must be to the bug's to count as the same one.</summary>
    public double JiraDuplicateThreshold { get; } = double.TryParse(Env("JIRA_DUPLICATE_THRESHOLD"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var t) && t is > 0 and <= 1 ? t : 0.6;
    /// <summary>Jira issue link type used to tie a new bug to its story (default "Relates"; e.g. "Blocks" or a custom "Tests" type).</summary>
    public string JiraStoryLinkType { get; } = Env("JIRA_STORY_LINK_TYPE") ?? "Relates";
    /// <summary>Optional folder the server may read artifact paths from when the artifacts were not uploaded.</summary>
    public string? ArtifactsRoot { get; } = Env("ARTIFACTS_ROOT");

    public IntegrationConfig()
    {
        var url = Env("JIRA_BASE_URL") ?? Env("CONFLUENCE_API_URL");
        if (url != null)
        {
            // Confluence URLs look like https://x.atlassian.net/wiki[/rest/api]; Jira lives at the site root.
            var i = url.IndexOf("/wiki", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) url = url[..i];
            url = url.TrimEnd('/');
        }
        JiraBaseUrl = url;
    }

    public bool AnthropicConfigured => !string.IsNullOrWhiteSpace(AnthropicKey);
    public bool JiraConfigured => !string.IsNullOrWhiteSpace(JiraBaseUrl) && !string.IsNullOrWhiteSpace(JiraUser) && !string.IsNullOrWhiteSpace(JiraToken);

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
