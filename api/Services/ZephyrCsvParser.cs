using System.Text;
using System.Text.RegularExpressions;

namespace Triage.Api.Services;

public record ZephyrStep(int Number, string Description, string TestData, string Expected, string Actual, string Status);

public record ZephyrCase(string Key, string Title, string Objective, string Precondition, string Status, List<ZephyrStep> Steps, string? StoryKey = null)
{
    /// <summary>Readable test-case write-up (objective, precondition and every step) for the AI prompt.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Objective.Length > 0) sb.AppendLine($"Objective: {Objective}");
        if (Precondition.Length > 0) sb.AppendLine($"Precondition: {Precondition}");
        foreach (var s in Steps)
        {
            sb.AppendLine($"Step {s.Number} [{s.Status}]: {s.Description}");
            if (s.TestData.Length > 0) sb.AppendLine($"  Test data: {s.TestData}");
            if (s.Expected.Length > 0) sb.AppendLine($"  Expected: {s.Expected}");
            if (s.Actual.Length > 0) sb.AppendLine($"  Actual: {s.Actual}");
        }
        return sb.ToString().Trim();
    }

    /// <summary>The first failing step's actual result when it carries a real message, else the first failing step's expectation.</summary>
    public string FailureMessage()
    {
        var failing = Steps.Where(s => s.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
        var real = failing.FirstOrDefault(s => s.Actual.Length > 0 && !s.Actual.Equals("Step executed", StringComparison.OrdinalIgnoreCase));
        if (real != null) return real.Actual;
        var first = failing.FirstOrDefault();
        return first != null
            ? $"Step {first.Number} failed. Expected: {first.Expected}"
            : $"Test case ended with status '{Status}' and no error message.";
    }
}

public record ZephyrCsv(string CycleName, string Environment, string Browser, string ExecutionDate, List<ZephyrCase> Cases);

/// <summary>
/// Parses zephyr-upload.csv: a "Label: value" text export with a cycle header followed by one block per test case
/// (Test Case Key, Title, Objective, Precondition, Overall Status, Test Scripts, then Step n / Test Data / Expected /
/// Actual Result / Status). Unlabelled lines continue the previous field.
/// </summary>
public static partial class ZephyrCsvParser
{
    public static ZephyrCsv Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');

        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<ZephyrCase>();
        Dictionary<string, string>? cur = null;
        var steps = new List<ZephyrStep>();
        StepBuilder? step = null;
        string? field = null;
        Dictionary<string, string> target = header;

        void FlushStep() { if (step != null) steps.Add(step.Build()); step = null; }
        void FlushCase()
        {
            FlushStep();
            if (cur != null && cur.TryGetValue("Test Case Key", out var key))
                cases.Add(new ZephyrCase(key, Get(cur, "Title"), Get(cur, "Objective"), Get(cur, "Precondition"),
                    Get(cur, "Overall Status"), new List<ZephyrStep>(steps), StoryKeys.Join(StoryKeys.Extract(Get(cur, "Story")))));
            cur = null; steps.Clear(); field = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            var stepMatch = StepRegex().Match(line);
            if (stepMatch.Success && cur != null)
            {
                FlushStep();
                step = new StepBuilder(int.Parse(stepMatch.Groups[1].Value)) { Description = stepMatch.Groups[2].Value.Trim() };
                field = "Description";
                continue;
            }

            var m = LabelRegex().Match(line);
            if (m.Success)
            {
                var label = m.Groups[1].Value;
                var value = m.Groups[2].Value.Trim();
                if (label == "Test Case Key") { FlushCase(); cur = new(StringComparer.OrdinalIgnoreCase); target = cur; }
                if (step != null && label is "Test Data" or "Expected" or "Actual Result" or "Status")
                {
                    step.Set(label, value);
                    field = label;
                    continue;
                }
                if (label == "Test Scripts") { FlushStep(); field = null; continue; }
                if (label is "Title" or "Objective" or "Precondition" or "Overall Status" or "Test Case Key"
                    or "Test Cycle Name" or "Test Cycle ID" or "Execution Date" or "Test Environment" or "Browser")
                {
                    target[label] = value;
                    field = label;
                    continue;
                }
                // Story / requirement / linked-issue style fields all end up in "Story"; only Jira keys are kept later.
                if (cur != null && StoryKeys.IsStoryField(label))
                {
                    target["Story"] = (target.GetValueOrDefault("Story") + " " + value).Trim();
                    field = "Story";
                    continue;
                }
            }

            // Continuation of the previous field.
            if (step != null && field != null) step.Append(field, line);
            else if (field != null) target[field] = (target.GetValueOrDefault(field) + " " + line).Trim();
        }
        FlushCase();

        if (cases.Count == 0)
            throw new InvalidDataException("This does not look like a Zephyr upload file (no \"Test Case Key:\" entries found).");

        return new ZephyrCsv(Get(header, "Test Cycle Name"), Get(header, "Test Environment"), Get(header, "Browser"),
            Get(header, "Execution Date"), cases);
    }

    /// <summary>Turns the CSV into a run on its own (used when no JSON results file is supplied).</summary>
    public static ParsedRun ToRun(ZephyrCsv csv)
    {
        var failures = new List<ParsedFailure>();
        int passed = 0, failed = 0, skipped = 0;
        foreach (var c in csv.Cases)
        {
            if (c.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase)) { passed++; continue; }
            if (!c.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            failed++;
            failures.Add(new ParsedFailure($"[{c.Key}] {c.Title}", "", 0, csv.Browser, "failed", 0, 0,
                PlaywrightParser.Clean(c.FailureMessage()), null, c.Describe(), new List<ParsedAttachment>(), c.StoryKey));
        }
        return new ParsedRun(csv.Cases.Count, passed, failed, skipped, 0, 0, failures);
    }

    /// <summary>Extracts the Zephyr key (e.g. GCR-T3335) from a title such as "[GCR-T3335] Scenario: …".</summary>
    public static string? KeyOf(string title) => KeyRegex().Match(title) is { Success: true } m ? m.Groups[1].Value : null;

    private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";

    private sealed class StepBuilder(int number)
    {
        public string Description = "", TestData = "", Expected = "", Actual = "", Status = "";
        public void Set(string label, string v) { switch (label) { case "Test Data": TestData = v; break; case "Expected": Expected = v; break; case "Actual Result": Actual = v; break; case "Status": Status = v; break; } }
        public void Append(string label, string v)
        {
            switch (label)
            {
                case "Description": Description = (Description + " " + v).Trim(); break;
                case "Test Data": TestData = (TestData + " " + v).Trim(); break;
                case "Expected": Expected = (Expected + " " + v).Trim(); break;
                case "Actual Result": Actual = (Actual + " " + v).Trim(); break;
                case "Status": Status = (Status + " " + v).Trim(); break;
            }
        }
        public ZephyrStep Build() => new(number, Description, TestData, Expected, Actual, Status);
    }

    [GeneratedRegex(@"^Step\s+(\d+)\s*:\s*(.*)$")] private static partial Regex StepRegex();
    [GeneratedRegex(@"^([A-Za-z][A-Za-z' ]*?)\s*:\s*(.*)$")] private static partial Regex LabelRegex();
    [GeneratedRegex(@"\[([A-Za-z]+-T\d+)\]")] private static partial Regex KeyRegex();
}
