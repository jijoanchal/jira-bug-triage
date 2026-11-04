using System.Text.RegularExpressions;

namespace Triage.Api.Services;

/// <summary>Converts the small markup used in bug descriptions to Atlassian Document Format (Jira Cloud REST v3).</summary>
public static partial class AdfConverter
{
    public static object ToAdf(string text)
    {
        var content = new List<object>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        List<object>? list = null;
        string? listType = null;

        void FlushPara()
        {
            if (para.Count == 0) return;
            content.Add(new { type = "paragraph", content = Inline(string.Join(" ", para)) });
            para.Clear();
        }
        void FlushList()
        {
            if (list == null) return;
            content.Add(new { type = listType, content = list });
            list = null; listType = null;
        }
        void AddItem(string type, string t)
        {
            FlushPara();
            if (listType != type) FlushList();
            listType = type; list ??= new List<object>();
            list.Add(new { type = "listItem", content = new object[] { new { type = "paragraph", content = Inline(t) } } });
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara(); FlushList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                var body = string.Join("\n", code);
                content.Add(new { type = "codeBlock", content = new object[] { new { type = "text", text = body.Length == 0 ? " " : body } } });
                continue;
            }
            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                FlushPara(); FlushList();
                content.Add(new { type = "heading", attrs = new { level = Math.Clamp(h.Groups[1].Length, 1, 6) }, content = Inline(h.Groups[2].Value) });
                continue;
            }
            var b = BulletRegex().Match(line);
            if (b.Success) { AddItem("bulletList", b.Groups[1].Value); continue; }
            var n = NumberRegex().Match(line);
            if (n.Success) { AddItem("orderedList", n.Groups[1].Value); continue; }
            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); FlushList(); continue; }
            FlushList();
            para.Add(line.Trim());
        }
        FlushPara(); FlushList();
        if (content.Count == 0) content.Add(new { type = "paragraph", content = Inline("(no description)") });
        return new { type = "doc", version = 1, content };
    }

    private static object[] Inline(string t) => new object[] { new { type = "text", text = t.Length == 0 ? " " : t } };

    [GeneratedRegex(@"^\s*(#{1,6})\s+(.+?)\s*$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^\s*[-*]\s+(.+)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$")] private static partial Regex NumberRegex();
}
