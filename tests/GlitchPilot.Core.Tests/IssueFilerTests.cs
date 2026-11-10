using GlitchPilot.Core.Models;
using GlitchPilot.Core.Services;

namespace GlitchPilot.Core.Tests;

public class IssueFilerTests
{
    [Fact]
    public void ComposeBody_IncludesDetailsAndFilesOfMergedErrors()
    {
        var primary = Make.Outcome("1");
        primary.Verdict.FilesToInvestigate = ["src/A.cs:10"];
        primary.Candidate.StackTrace = "at A.Run()";

        var merged = Make.Outcome("2", title: "NRE elsewhere");
        merged.Verdict.FilesToInvestigate = ["src/B.cs:20", "src/A.cs:10"];
        merged.Candidate.SourceLocation = "B.Run";
        merged.Candidate.StackTrace = "at B.Run()\nat B.Main()";
        merged.Candidate.ErrorTitle = "NRE for @admin";

        primary.MergedOutcomes = [merged];
        primary.MergeReason = "Same null config";

        var body = IssueFiler.ComposeBody(primary);

        Assert.Contains("## Merged GlitchTip Errors", body);
        Assert.Contains("must cover every call site", body);
        Assert.Contains("Same null config", body);
        Assert.Contains("GlitchTip #2", body);
        Assert.Contains("at B.Run()", body);
        Assert.DoesNotContain("@admin", body, StringComparison.Ordinal);
        Assert.Contains("- `src/B.cs:20`", body);
        Assert.Equal(1, CountOccurrences(body, "- `src/A.cs:10`"));
    }

    [Fact]
    public void TrackingLabels_FindAndMissing()
    {
        var tracked = new Dictionary<string, ExistingWorkItem> { ["2"] = Tracked.Issue(77), ["4"] = Tracked.Issue(80) };

        Assert.Equal(77, TrackingLabels.Find(tracked, ["1", "2"])?.Number);
        Assert.Null(TrackingLabels.Find(tracked, ["3"]));
        Assert.Equal(["1", "4"], TrackingLabels.Missing(tracked, 77, ["1", "2", "4", "2"]));
    }

    [Fact]
    public void SafeMarkdown_NeutralisesMentionsAndLinks()
    {
        var safe = IssueFiler.SafeMarkdown("Invalid handle @admin [click](http://x)\nnext");

        Assert.DoesNotContain("@admin", safe, StringComparison.Ordinal);
        Assert.Contains(@"\[click\]", safe);
        Assert.DoesNotContain("\n", safe);
    }

    [Fact]
    public void SafeMarkdown_NeutralisesIssueRefsHtmlAndCode()
    {
        var safe = IssueFiler.SafeMarkdown("Order #42 failed in <details> `x`");

        Assert.DoesNotContain("#42", safe, StringComparison.Ordinal);
        Assert.Contains("&lt;details&gt;", safe);
        Assert.Contains(@"\`x\`", safe);
    }

    [Fact]
    public void BuildLabelSearchTerms_OrsLabels_AndSplitsBelowQueryLimit()
    {
        var ids = Enumerable.Range(1000, 40).Select(i => i.ToString()).ToList();

        var terms = IssueFiler.BuildLabelSearchTerms(ids, "my-org", "my-repo");

        Assert.True(terms.Count > 1);
        var qualifiers = " repo:my-org/my-repo state:open is:pull-request".Length;
        Assert.All(terms, t => Assert.True(t.Length + qualifiers <= 256, t));
        Assert.StartsWith("label:\"glitchtip:1000\",\"glitchtip:1001\"", terms[0]);
        var all = string.Join(",", terms);
        Assert.All(ids, id => Assert.Contains($"\"glitchtip:{id}\"", all));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
