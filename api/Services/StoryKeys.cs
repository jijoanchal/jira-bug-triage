using System.Text.Json;
using System.Text.RegularExpressions;

namespace Triage.Api.Services;

/// <summary>
/// Finds the Jira story keys (e.g. GCR-123) a test is covering in the uploaded reports. The report formats do not agree on a field name,
/// so fields are recognised by name (story, requirement, epic, issue, linked, coverage, ticket, jira); Zephyr test-case keys such as
/// GCR-T3335 are never mistaken for stories.
/// </summary>
public static partial class StoryKeys
{
    public const string FieldPattern = "story|stories|requirement|epic|issue|linked|coverage|ticket|jira|user ?story";

    public static bool IsStoryField(string name) => FieldRegex().IsMatch(name);

    public static bool IsValid(string key) => KeyRegex().IsMatch(key);

    /// <summary>Distinct keys found in free text, upper-cased, in order of appearance.</summary>
    public static List<string> Extract(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : KeyScan().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    /// <summary>Keys from every story-like property of a JSON object (strings, arrays and nested objects are all searched).</summary>
    public static List<string> FromJson(JsonElement obj)
    {
        var keys = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object) return keys;
        foreach (var p in obj.EnumerateObject())
            if (IsStoryField(p.Name)) Collect(p.Value, keys);

        // Playwright annotations: { type: "story" | "issue" | ..., description: "GCR-123" }
        if (obj.TryGetProperty("annotations", out var anns) && anns.ValueKind == JsonValueKind.Array)
            foreach (var a in anns.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    && IsStoryField(t.GetString() ?? "") && a.TryGetProperty("description", out var d))
                    Collect(d, keys);
        return keys.Distinct().ToList();
    }

    public static string? Join(IEnumerable<string> keys) => keys.Distinct().ToList() is { Count: > 0 } l ? string.Join(", ", l) : null;

    /// <summary>Splits and validates a user-typed list such as "gcr-12, GCR-13"; throws on anything that is not a Jira key.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = input.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant()).Distinct().ToList();
        var bad = parts.Where(p => !IsValid(p)).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Not a Jira issue key: {string.Join(", ", bad)}. Use the form PROJ-123.");
        return string.Join(", ", parts);
    }

    private static void Collect(JsonElement e, List<string> keys)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: keys.AddRange(Extract(e.GetString())); break;
            case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) Collect(i, keys); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Collect(p.Value, keys); break;
        }
    }

    [GeneratedRegex(FieldPattern, RegexOptions.IgnoreCase)] private static partial Regex FieldRegex();
    [GeneratedRegex(@"^[A-Z][A-Z0-9]+-\d+$")] private static partial Regex KeyRegex();
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9]+-\d+\b")] private static partial Regex KeyScan();
}
