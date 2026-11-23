using System.Web;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Pipelines;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlitchPilot.Core.Tests;

public class IssueLinksTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset s_now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static (string? P, string? E, string? X, string? S) Parse(string link)
    {
        var q = HttpUtility.ParseQueryString(new Uri(link).Query);
        return (q["p"], q["e"], q["x"], q["s"]);
    }

    [Fact]
    public void Build_ThenVerify_Succeeds()
    {
        var link = IssueLinks.Build("https://fn.example/api/file-issue", Key, "my app", "42", s_now.AddDays(14));
        var (p, e, x, s) = Parse(link);

        Assert.StartsWith("https://fn.example/api/file-issue?p=my%20app&e=42&", link);
        Assert.Equal("my app", p);
        Assert.Null(IssueLinks.Verify(Key, p, e, x, s, s_now));
    }

    [Fact]
    public void Verify_RejectsTamperedExpiredOrForeignKey()
    {
        var (p, e, x, s) = Parse(IssueLinks.Build("https://fn.example/f", Key, "app", "42", s_now.AddDays(1)));

        Assert.Equal("The link is invalid.", IssueLinks.Verify(Key, p, "43", x, s, s_now));
        Assert.Equal("The link is invalid.", IssueLinks.Verify(Key, "other", e, x, s, s_now));
        Assert.Equal("The link is invalid.", IssueLinks.Verify(Key, p, e, x + "0", s, s_now));
        Assert.Equal("The link is invalid.", IssueLinks.Verify(Key.Replace('0', '1'), p, e, x, s, s_now));
        Assert.Equal("The link is incomplete.", IssueLinks.Verify(Key, p, e, x, null, s_now));
        Assert.Contains("expired", IssueLinks.Verify(Key, p, e, x, s, s_now.AddDays(2)));
    }
}

public class ManualFilingRenderTests
{
    private static TriageOutcome Dismissed(string id, string type)
    {
        var o = Make.Outcome(id, actionable: false);
        o.Verdict.ErrorType = type;
        return o;
    }

    [Fact]
    public void Summary_ShowsButtonOnlyOnDismissedTransientErrors()
    {
        var outcomes = new List<TriageOutcome>
        {
            Dismissed("1", "transient"),
            Dismissed("2", "config"),
            Make.Outcome("3"), // actionable
        };
        outcomes[2].Verdict.ErrorType = "transient";

        var html = TriageReportRenderer.RenderSummary("app", outcomes,
            fileIssueLink: o => $"https://fn.example/f?e={o.Candidate.ErrorId}&s=a&x=1");

        Assert.Equal(1, CountOccurrences(html, "Create GitHub issue anyway"));
        Assert.Contains("href=\"https://fn.example/f?e=1&amp;s=a&amp;x=1\"", html);
        Assert.DoesNotContain("f?e=2", html);
        Assert.DoesNotContain("f?e=3", html);
    }

    [Fact]
    public void Summary_WithoutLinkBuilder_HasNoButton()
    {
        var html = TriageReportRenderer.RenderSummary("app", [Dismissed("1", "transient")]);
        Assert.DoesNotContain("Create GitHub issue anyway", html);
    }

    [Fact]
    public void ConfirmPage_EscapesAndCarriesSignedFieldsAsHiddenInputs()
    {
        var candidate = new TriageCandidate { ErrorId = "7", ErrorTitle = "<script>x</script>", GlitchTipLink = "https://gt/7" };
        var html = ManualFilingPages.RenderConfirm(new ManualFilingPreview(ManualFilingStatus.Ready, candidate),
            new Dictionary<string, string> { ["p"] = "app", ["e"] = "7", ["x"] = "1", ["s"] = "sig\"" });

        Assert.DoesNotContain("<script>x", html);
        Assert.Contains("<form method=\"post\"", html);
        Assert.Contains("name=\"s\" value=\"sig&quot;\"", html);
        Assert.Contains("name=\"notes\"", html);
    }

    [Fact]
    public void ConfirmPage_AlreadyTracked_HasNoForm()
    {
        var candidate = new TriageCandidate { ErrorId = "7", ErrorTitle = "Timeout", GlitchTipLink = "https://gt/7" };
        var html = ManualFilingPages.RenderConfirm(
            new ManualFilingPreview(ManualFilingStatus.AlreadyTracked, candidate, Tracked.Issue(55)),
            new Dictionary<string, string>());

        Assert.DoesNotContain("<form", html);
        Assert.Contains("#55", html);
    }

    [Fact]
    public void ComposeBody_ManualFiling_IncludesNoteAndTeamNotesWithoutMentions()
    {
        var o = Dismissed("1", "transient");
        o.FiledManually = true;
        o.ManualNotes = "Add a Polly retry.\nAsk @alice for details.";

        var body = IssueFiler.ComposeBody(o);

        Assert.Contains("Filed manually from the GlitchPilot email", body);
        Assert.Contains("## Notes from the Team", body);
        Assert.Contains("Add a Polly retry.\nAsk @​alice", body.Replace("\r", ""));
        Assert.True(body.IndexOf("## Notes from the Team") < body.IndexOf("## Problem"));
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;
}

public class ManualFilingServiceTests
{
    private static readonly IReadOnlyList<MonitoredProject> s_projects =
        [new() { Slug = "app", GitHubOwner = "org", GitHubRepo = "repo" }];

    private static (ManualFilingService Service, FakeFiler Filer) Create(params TriageCandidate[] candidates)
    {
        var filer = new FakeFiler();
        var service = new ManualFilingService(new FakeGlitchTip([.. candidates]), new FakeClassifier(), filer,
            NullLogger<ManualFilingService>.Instance, () => s_projects);
        return (service, filer);
    }

    private static TriageCandidate Candidate(string id) =>
        new() { ErrorId = id, ErrorTitle = "noise: SqlException timeout", Occurrences = 2 };

    [Fact]
    public async Task File_CreatesIssueInConfiguredRepo_WithNotesAndForcedAction()
    {
        var (service, filer) = Create(Candidate("9"));

        var result = await service.FileAsync("APP", "9", "  Use a retry.  ");

        Assert.Equal(ManualFilingStatus.Created, result.Status);
        Assert.Equal("https://github.com/org/repo/issues/100", result.Url);
        var filed = Assert.Single(filer.Filed);
        Assert.True(filed.Verdict.RequiresAction);
        Assert.True(filed.FiledManually);
        Assert.Equal("Use a retry.", filed.ManualNotes);
        Assert.Equal(("org", "repo", "app"), (filed.Candidate.GitHubOwner, filed.Candidate.GitHubRepo, filed.Candidate.ProjectSlug));
    }

    [Fact]
    public async Task File_AlreadyTracked_DoesNotCreateSecondIssue()
    {
        var (service, filer) = Create(Candidate("9"));
        filer.TrackedByLabel["9"] = Tracked.Issue(77);

        var result = await service.FileAsync("app", "9", null);

        Assert.Equal(ManualFilingStatus.AlreadyTracked, result.Status);
        Assert.Equal(77, result.Number);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public async Task File_GitHubLookupFails_FilesNothing()
    {
        var (service, filer) = Create(Candidate("9"));
        filer.LookupFailure = new HttpRequestException("down");

        var result = await service.FileAsync("app", "9", null);

        Assert.Equal(ManualFilingStatus.Failed, result.Status);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public async Task File_UnknownProjectOrError_ReturnsNotFound()
    {
        var (service, filer) = Create(Candidate("9"));

        Assert.Equal(ManualFilingStatus.NotFound, (await service.FileAsync("other", "9", null)).Status);
        Assert.Equal(ManualFilingStatus.NotFound, (await service.FileAsync("app", "10", null)).Status);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public async Task Preview_FilesNothing_AndReportsExistingIssue()
    {
        var (service, filer) = Create(Candidate("9"));

        Assert.Equal(ManualFilingStatus.Ready, (await service.PreviewAsync("app", "9")).Status);
        filer.TrackedByLabel["9"] = Tracked.Issue(77);
        var preview = await service.PreviewAsync("app", "9");

        Assert.Equal(ManualFilingStatus.AlreadyTracked, preview.Status);
        Assert.Equal(77, preview.Existing!.Number);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public void NormalizeNotes_TrimsAndCaps()
    {
        Assert.Null(ManualFilingService.NormalizeNotes("   "));
        Assert.Equal(ManualFilingService.MaxNotesLength, ManualFilingService.NormalizeNotes(new string('a', 5000))!.Length);
    }
}

[Collection("env")]
public class ManualFilingConfigTests : IDisposable
{
    private static readonly string[] s_vars = ["FILE_ISSUE_URL", "LINK_SIGNING_KEY", "LINK_EXPIRY_DAYS"];
    private readonly Dictionary<string, string?> _saved = s_vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public ManualFilingConfigTests()
    {
        foreach (var v in s_vars) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void NotConfigured_ReturnsNull_AndPipelineBuildsNoLink()
    {
        Assert.Null(EnvironmentConfig.ManualFiling);
        Assert.Null(TriagePipeline.BuildFileIssueLink(null));
    }

    [Fact]
    public void UrlWithoutStrongKey_Throws()
    {
        Environment.SetEnvironmentVariable("FILE_ISSUE_URL", "https://fn.example/api/file-issue");
        Assert.Throws<InvalidOperationException>(() => EnvironmentConfig.ManualFiling);

        Environment.SetEnvironmentVariable("LINK_SIGNING_KEY", "short");
        Assert.Throws<InvalidOperationException>(() => EnvironmentConfig.ManualFiling);
    }

    [Fact]
    public void HttpUrl_Throws()
    {
        Environment.SetEnvironmentVariable("FILE_ISSUE_URL", "http://fn.example/api/file-issue");
        Environment.SetEnvironmentVariable("LINK_SIGNING_KEY", new string('k', 32));
        Assert.Throws<InvalidOperationException>(() => EnvironmentConfig.ManualFiling);
    }

    [Fact]
    public void Configured_ProducesVerifiableLinks()
    {
        var key = new string('k', 32);
        Environment.SetEnvironmentVariable("FILE_ISSUE_URL", "https://fn.example/api/file-issue");
        Environment.SetEnvironmentVariable("LINK_SIGNING_KEY", key);
        Environment.SetEnvironmentVariable("LINK_EXPIRY_DAYS", "3");

        var settings = EnvironmentConfig.ManualFiling!;
        var link = TriagePipeline.BuildFileIssueLink(settings)!(Make.Outcome("5"));
        var q = HttpUtility.ParseQueryString(new Uri(link).Query);

        Assert.Equal(3, settings.ExpiryDays);
        Assert.Null(IssueLinks.Verify(key, q["p"], q["e"], q["x"], q["s"], DateTimeOffset.UtcNow.AddDays(2)));
        Assert.NotNull(IssueLinks.Verify(key, q["p"], q["e"], q["x"], q["s"], DateTimeOffset.UtcNow.AddDays(4)));
    }
}
