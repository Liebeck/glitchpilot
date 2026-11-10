using GlitchPilot.Core.Models;
using GlitchPilot.Core.Pipelines;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlitchPilot.Core.Tests;

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection;

/// <summary>Pipeline tests set process environment variables, so they never run in parallel.</summary>
[Collection("env")]
public class TriagePipelineTests : IDisposable
{
    private static readonly string[] s_vars =
        ["PROJECTS", "MERGE_DUPLICATES_ENABLED", "DUPLICATE_SCAN_ENABLED", "DUPLICATE_SCAN_MAX_ISSUES", "LOOKBACK_HOURS", "MIN_OCCURRENCES"];

    private readonly Dictionary<string, string?> _saved = s_vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public TriagePipelineTests()
    {
        Environment.SetEnvironmentVariable("PROJECTS",
            """[{"slug":"app","targetEnvironment":"Production","gitHubOwner":"org","gitHubRepo":"repo"}]""");
        Environment.SetEnvironmentVariable("MERGE_DUPLICATES_ENABLED", null);
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", null);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    private static List<TriageCandidate> Candidates(params (string Id, string Title, int Count)[] items) =>
        items.Select(i => new TriageCandidate
        {
            ErrorId = i.Id,
            ErrorTitle = i.Title,
            Occurrences = i.Count,
            GlitchTipLink = $"https://glitchtip.example/issues/{i.Id}",
        }).ToList();

    private static (TriagePipeline Pipeline, FakeFiler Filer, FakeMailer Mailer, FakeChat Chat) Build(
        List<TriageCandidate> candidates, params string[] chatResponses)
    {
        var filer = new FakeFiler();
        var mailer = new FakeMailer();
        var chat = new FakeChat(chatResponses);
        var pipeline = new TriagePipeline(
            new FakeGlitchTip(candidates),
            new FakeClassifier(),
            filer,
            new DuplicateDetector(chat, NullLogger<DuplicateDetector>.Instance),
            mailer,
            NullLogger<TriagePipeline>.Instance);
        return (pipeline, filer, mailer, chat);
    }

    [Fact]
    public async Task MergedErrors_FileOnlyOneIssue_AndAreReportedInEmail()
    {
        var (pipeline, filer, mailer, _) = Build(
            Candidates(("11", "NRE in GetProfile (user 1)", 3), ("12", "NRE in GetProfile (user 2)", 9), ("13", "Timeout in Export", 7), ("14", "noise", 1)),
            """{"groups":[{"ids":["11","12"],"reason":"Same null avatar in GetProfile"},{"ids":["13"]}]}""");

        await pipeline.RunAsync();

        Assert.Equal(2, filer.Filed.Count);
        var primary = filer.Filed.Single(o => o.MergedOutcomes.Count > 0);
        Assert.Equal("12", primary.Candidate.ErrorId); // most occurrences wins
        Assert.Equal(["12", "11"], primary.AllErrorIds);
        Assert.Equal("Same null avatar in GetProfile", primary.MergeReason);

        var (subject, body) = Assert.Single(mailer.Sent);
        Assert.Contains("2 issues need attention", subject);
        Assert.Contains("Merged 1 GlitchTip duplicate", body);
        Assert.Contains("Same null avatar in GetProfile", body);
        Assert.Contains("1 merged", body);
    }

    [Fact]
    public async Task MergeDisabled_FilesEveryActionableError_WithoutModelCall()
    {
        Environment.SetEnvironmentVariable("MERGE_DUPLICATES_ENABLED", "false");
        var (pipeline, filer, _, chat) = Build(Candidates(("1", "A", 1), ("2", "B", 1)));

        await pipeline.RunAsync();

        Assert.Equal(2, filer.Filed.Count);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task HistoryScanDisabled_DoesNotLoadOpenWork()
    {
        var (pipeline, filer, _, _) = Build(Candidates(("1", "A", 1)));

        await pipeline.RunAsync();

        Assert.Equal(0, filer.ListOpenWorkCalls);
        Assert.Single(filer.Filed);
    }

    [Fact]
    public async Task HistoryScanEnabled_KnownProblem_IsNotFiledButAnnotatedAndReported()
    {
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", "true");
        var (pipeline, filer, mailer, _) = Build(
            Candidates(("21", "NRE in GetProfile", 4), ("22", "Timeout in Export", 2)),
            """{"groups":[{"ids":["21"]},{"ids":["22"]}]}""",
            """{"matches":[{"id":"21","number":87,"confidence":"high","reason":"Same NRE as open issue #87"}]}""");
        filer.OpenWork.Add(new ExistingWorkItem
        {
            Number = 87,
            Title = "[GlitchTip #3] NRE in GetProfile",
            Url = "https://github.com/org/repo/issues/87",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-95),
            PullRequests = [new LinkedPullRequest { Number = 90, Url = "https://github.com/org/repo/pull/90" }],
        });

        await pipeline.RunAsync();

        var filed = Assert.Single(filer.Filed);
        Assert.Equal("22", filed.Candidate.ErrorId);

        var (outcome, known) = Assert.Single(filer.Annotated);
        Assert.Equal("21", outcome.Candidate.ErrorId);
        Assert.Equal(87, known.Number);

        var (subject, body) = Assert.Single(mailer.Sent);
        Assert.Contains("1 issue needs attention", subject);
        Assert.Contains("Already Known (1)", body);
        Assert.Contains("issues/87", body);
        Assert.Contains("PR #90", body);
        Assert.Contains("3 months", body);
    }

    [Fact]
    public async Task HistoryScan_OnlyKnownProblems_SubjectSaysRecurred()
    {
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", "true");
        var (pipeline, filer, mailer, _) = Build(
            Candidates(("21", "NRE in GetProfile", 4)),
            """{"matches":[{"id":"21","number":87,"confidence":"high","reason":"same"}]}""");
        filer.OpenWork.Add(new ExistingWorkItem { Number = 87, Title = "NRE", Url = "u", CreatedAt = DateTimeOffset.UtcNow });

        await pipeline.RunAsync();

        Assert.Empty(filer.Filed);
        Assert.Contains("1 known issue recurred", Assert.Single(mailer.Sent).Subject);
    }

    [Fact]
    public async Task LabelTrackedErrors_SkipHistoryScan_AndAreReportedAsKnown()
    {
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", "true");
        var (pipeline, filer, mailer, chat) = Build(Candidates(("31", "A", 1)));
        filer.TrackedByLabel["31"] = Tracked.Issue(55);

        await pipeline.RunAsync();

        Assert.Equal(0, filer.ListOpenWorkCalls);
        Assert.Empty(chat.Calls);
        Assert.Empty(filer.Filed);
        Assert.Empty(filer.LabelsAdded); // label already present — no redundant write
        var (subject, body) = Assert.Single(mailer.Sent);
        Assert.Contains("1 known issue recurred", subject);
        Assert.Contains("Already Known (1)", body);
        Assert.Contains("issues/55", body);
        Assert.DoesNotContain("no open pull request", body); // PRs were not looked up
    }

    [Fact]
    public async Task LabelTrackedMergedGroup_AddsLabelsOfNewlyMergedErrors()
    {
        var (pipeline, filer, _, _) = Build(
            Candidates(("41", "A", 5), ("42", "A again", 1)),
            """{"groups":[{"ids":["41","42"],"reason":"same"}]}""");
        filer.TrackedByLabel["41"] = Tracked.Issue(55);

        await pipeline.RunAsync();

        Assert.Empty(filer.Filed);
        var (number, ids) = Assert.Single(filer.LabelsAdded);
        Assert.Equal(55, number);
        Assert.Equal(["42"], ids); // only the label that is missing
        Assert.Equal(1, filer.TrackedLookups);
        Assert.Equal(["41", "42"], Assert.Single(filer.TrackedLookupIds)); // all ids, one lookup
    }

    [Fact]
    public async Task GitHubLookupFailure_FilesNothing_AndEmailExplains()
    {
        var (pipeline, filer, mailer, _) = Build(Candidates(("51", "A", 1)));
        filer.LookupFailure = new HttpRequestException("rate limited");

        await pipeline.RunAsync();

        Assert.Empty(filer.Filed);
        Assert.Contains("not filed to avoid duplicates", Assert.Single(mailer.Sent).Body);
    }

    [Fact]
    public async Task TimeoutWithoutCancellation_IsHandled_AndEmailIsStillSent()
    {
        var (pipeline, filer, mailer, _) = Build(Candidates(("52", "A", 1)));
        filer.LookupFailure = new TaskCanceledException("HttpClient timeout");

        await pipeline.RunAsync();

        Assert.Single(mailer.Sent);
    }

    [Fact]
    public async Task RealCancellation_Propagates()
    {
        var (pipeline, filer, _, _) = Build(Candidates(("53", "A", 1)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        filer.LookupFailure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(cts.Token));
    }

    [Fact]
    public async Task AnnotateFailure_DoesNotAbortRun()
    {
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", "true");
        var (pipeline, filer, mailer, _) = Build(
            Candidates(("61", "A", 1), ("62", "B", 1)),
            """{"groups":[{"ids":["61"]},{"ids":["62"]}]}""",
            """{"matches":[{"id":"61","number":87,"confidence":"high","reason":"same"}]}""");
        filer.OpenWork.Add(new ExistingWorkItem { Number = 87, Title = "A", Url = "u", CreatedAt = DateTimeOffset.UtcNow });
        filer.AnnotateFailure = new TaskCanceledException("timeout");

        await pipeline.RunAsync();

        Assert.Equal("62", Assert.Single(filer.Filed).Candidate.ErrorId);
        Assert.Single(mailer.Sent);
    }

    [Fact]
    public async Task CreateFailure_IsShownInEmail()
    {
        var (pipeline, filer, mailer, _) = Build(Candidates(("71", "A", 1)));
        filer.CreateFails = true;

        await pipeline.RunAsync();

        Assert.Contains("Filing the GitHub issue failed", Assert.Single(mailer.Sent).Body);
    }

    [Fact]
    public async Task InvalidBooleanSetting_FailsFast()
    {
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", "ja");
        var (pipeline, filer, _, _) = Build(Candidates(("81", "A", 1)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync());
        Assert.Contains("DUPLICATE_SCAN_ENABLED", ex.Message);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public void PromotePrimary_PrefersPriorityThenOccurrences()
    {
        var low = Make.Outcome("1", occurrences: 100, priority: "low");
        var high = Make.Outcome("2", occurrences: 2, priority: "high");
        var high2 = Make.Outcome("3", occurrences: 5, priority: "high");

        var primary = TriagePipeline.PromotePrimary(new MergeGroup { Members = [low, high, high2], Reason = "r" });

        Assert.Same(high2, primary);
        Assert.Equal([high, low], primary.MergedOutcomes);
        Assert.Same(high2, low.MergedInto);
        Assert.Equal("r", primary.MergeReason);
    }
}
