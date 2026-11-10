using System.Text;
using System.Text.RegularExpressions;

namespace GlitchPilot.Core.Services;

/// <summary>Helpers to condense GitHub issue / PR bodies for the history duplicate scan.</summary>
internal static partial class WorkItemText
{
    private const int MaxExcerpt = 1200;

    /// <summary>
    /// Extracts the parts of a GlitchPilot issue body that identify the problem:
    /// "Problem", "Files to Investigate" and the top of the stack trace.
    /// Falls back to the beginning of the body for other issues / PRs.
    /// </summary>
    public static string Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var text = body.Replace("\r\n", "\n");

        var sb = new StringBuilder();
        var problem = Section(text, "## Problem");
        if (problem is not null) sb.AppendLine($"problem: {problem}");

        var files = Section(text, "## Files to Investigate");
        if (files is not null) sb.AppendLine($"files: {string.Join(", ", Lines(files).Select(l => l.TrimStart('-', ' ').Trim('`')))}");

        var stack = StackRegex().Match(text);
        if (stack.Success) sb.AppendLine($"stack (top):\n{string.Join('\n', Lines(stack.Groups[1].Value).Take(8))}");

        var result = sb.Length > 0 ? sb.ToString() : text;
        return result.Length <= MaxExcerpt ? result.Trim() : result[..MaxExcerpt].Trim() + "…";
    }

    /// <summary>
    /// Issue numbers a PR body refers to. Closing keywords ("Fixes #12") win; otherwise any
    /// "#N" that is one of the known GlitchPilot issues counts.
    /// </summary>
    public static List<int> ReferencedIssues(string? body, IEnumerable<int> knownIssues)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        var known = knownIssues.ToHashSet();

        var closing = ClosingRefRegex().Matches(body)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(known.Contains)
            .Distinct()
            .ToList();
        if (closing.Count > 0) return closing;

        return AnyRefRegex().Matches(body)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(known.Contains)
            .Distinct()
            .ToList();
    }

    public static bool MentionsGlitchTip(string? title, string? body) =>
        (title?.Contains("GlitchTip", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (body?.Contains("GlitchTip", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? Section(string text, string heading)
    {
        var start = text.IndexOf(heading + "\n", StringComparison.Ordinal);
        if (start < 0) return null;
        start += heading.Length + 1;
        var end = text.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = (end < 0 ? text[start..] : text[start..end]).Trim();
        return section.Length == 0 ? null : section;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"### Stack Trace\s*\n```[^\n]*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex StackRegex();

    [GeneratedRegex(@"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingRefRegex();

    [GeneratedRegex(@"(?<![\w/])#(\d+)\b")]
    private static partial Regex AnyRefRegex();
}
