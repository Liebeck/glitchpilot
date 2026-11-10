using GlitchPilot.Core.Services;

namespace GlitchPilot.Core.Tests;

public class WorkItemTextTests
{
    private const string IssueBody = """
        ## Problem
        GetProfile throws a NullReferenceException when the user has no avatar.

        ## Acceptance Criteria
        - [ ] Returns 404

        ## Files to Investigate
        - `src/Api/ProfileController.cs:42`
        - `src/Api/AvatarService.cs:10`

        ## Technical Context

        ### Stack Trace
        ```
        System.NullReferenceException: Object reference not set
           at Api.ProfileController.GetProfile() in ProfileController.cs:line 42
        ```

        ## Recommendation
        Add a null check.
        """;

    [Fact]
    public void Excerpt_ExtractsProblemFilesAndStack()
    {
        var excerpt = WorkItemText.Excerpt(IssueBody);

        Assert.Contains("problem: GetProfile throws a NullReferenceException", excerpt);
        Assert.Contains("files: src/Api/ProfileController.cs:42, src/Api/AvatarService.cs:10", excerpt);
        Assert.Contains("at Api.ProfileController.GetProfile()", excerpt);
        Assert.DoesNotContain("Add a null check", excerpt);
        Assert.DoesNotContain("Returns 404", excerpt);
    }

    [Fact]
    public void Excerpt_UnstructuredBody_FallsBackToText()
    {
        Assert.Equal("Some PR description", WorkItemText.Excerpt("Some PR description"));
        Assert.Equal("", WorkItemText.Excerpt(null));
    }

    [Fact]
    public void ReferencedIssues_PrefersClosingKeywords()
    {
        var refs = WorkItemText.ReferencedIssues("Fixes #87.\nSee also #12 and #99", [12, 87, 99]);
        Assert.Equal([87], refs);
    }

    [Fact]
    public void ReferencedIssues_FallsBackToKnownMentions()
    {
        var refs = WorkItemText.ReferencedIssues("Related to #12, #500 and org/other#87", [12, 87]);
        Assert.Equal([12], refs);
    }

    [Fact]
    public void ReferencedIssues_IgnoresUnknownIssues()
    {
        Assert.Empty(WorkItemText.ReferencedIssues("Closes #5", [87]));
    }
}
