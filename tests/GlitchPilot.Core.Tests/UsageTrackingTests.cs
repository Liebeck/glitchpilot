using System.Text.Json;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Pipelines;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlitchPilot.Core.Tests;

[Collection("env")]
public class UsageTrackingTests : IDisposable
{
    private static readonly string[] s_vars =
        ["PROJECTS", "MERGE_DUPLICATES_ENABLED", "DUPLICATE_SCAN_ENABLED", "REPORT_MAX_DELAY_MINUTES"];

    private readonly Dictionary<string, string?> _saved = s_vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public UsageTrackingTests()
    {
        Environment.SetEnvironmentVariable("PROJECTS",
            """[{"slug":"app","targetEnvironment":"Production","gitHubOwner":"org","gitHubRepo":"repo"}]""");
        Environment.SetEnvironmentVariable("MERGE_DUPLICATES_ENABLED", "false");
        Environment.SetEnvironmentVariable("DUPLICATE_SCAN_ENABLED", null);
        Environment.SetEnvironmentVariable("REPORT_MAX_DELAY_MINUTES", null);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    private sealed record Setup(TriagePipeline Triage, ReportPipeline Report, FakeRunStore Store,
        FakeCopilot Copilot, FakeMailer Mailer, FakeFiler Filer);

    private static Setup Build(bool storageEnabled, Func<DateTimeOffset>? now = null, params (string Id, string Title)[] errors)
    {
        var candidates = errors.Select(e => new TriageCandidate
        {
            ErrorId = e.Id, ErrorTitle = e.Title, Occurrences = 1,
            GlitchTipLink = $"https://glitchtip.example/issues/{e.Id}",
        }).ToList();

        var store = new FakeRunStore { IsEnabled = storageEnabled };
        var copilot = new FakeCopilot();
        var mailer = new FakeMailer();
        var filer = new FakeFiler();
        var report = new ReportPipeline(store, copilot, mailer, NullLogger<ReportPipeline>.Instance,
            now ?? (() => DateTimeOffset.UtcNow));
        var triage = new TriagePipeline(new FakeGlitchTip(candidates), new FakeClassifier(), filer,
            new DuplicateDetector(new FakeChat(), NullLogger<DuplicateDetector>.Instance),
            store, report, NullLogger<TriagePipeline>.Instance);
        return new Setup(triage, report, store, copilot, mailer, filer);
    }

    // ── Triage without storage: immediate email ────────────

    [Fact]
    public async Task WithoutStorage_EmailIsSentImmediately_WithoutUsageSection()
    {
        var s = Build(storageEnabled: false, now: null, ("1", "NRE"), ("2", "noise"));

        await s.Triage.RunAsync();

        var (_, body) = Assert.Single(s.Mailer.Sent);
        Assert.DoesNotContain(TriageReportRenderer.UsagePlaceholder, body);
        Assert.DoesNotContain("Usage &amp; cost", body);
    }

    // ── Triage with storage: email waits for Copilot ───────

    [Fact]
    public async Task WithStorage_EmailWaitsForCopilot_ThenReportsUsage()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "NRE"), ("2", "Timeout"), ("3", "noise"));

        await s.Triage.RunAsync();

        Assert.Empty(s.Mailer.Sent);
        var run = Assert.Single(s.Store.Pending.Values);
        var project = Assert.Single(run.Projects);
        Assert.Equal([100, 101], project.Issues.Select(i => i.Number));
        Assert.Single(run.Emails!);

        // One session still running → still waiting
        s.Copilot.Issues[100] = FakeCopilot.Completed(200, 30);
        s.Copilot.Issues[101] = new CopilotIssueUsage { Status = "running" };
        await s.Report.RunAsync();
        Assert.Empty(s.Mailer.Sent);

        // All done → email with usage, final record stored
        s.Copilot.Issues[101] = FakeCopilot.Completed(201, 5, 5);
        s.Copilot.Billing = new CopilotBillingDay
        {
            Source = "ai_credit",
            Items =
            [
                new BillingUsageItem { Product = "copilot", Sku = "cloud agent AI credits", UnitType = "credits", GrossQuantity = 400, GrossAmount = 4m, NetAmount = 1m },
                new BillingUsageItem { Product = "copilot", Sku = "chat AI credits", Model = "gpt-5", GrossQuantity = 100, GrossAmount = 1m },
            ],
        };
        await s.Report.RunAsync();

        var (subject, body) = Assert.Single(s.Mailer.Sent);
        Assert.Contains("2 issues need attention", subject);
        Assert.Contains("Usage &amp; cost", body);
        Assert.Contains("PR #200", body);
        Assert.Contains("cloud agent $4.00", body);
        Assert.DoesNotContain(TriageReportRenderer.UsagePlaceholder, body);

        Assert.Empty(s.Store.Pending);
        var done = Assert.Single(s.Store.Completed);
        Assert.NotNull(done.ReportedAt);
        var issues = done.Projects[0].Issues;
        // 30 of 40 minutes → 75 % of the day's agent cost
        Assert.Equal(3m, issues[0].Copilot!.EstimatedGrossUsd);
        Assert.Equal(300m, issues[0].Copilot!.EstimatedAiCredits);
        Assert.Equal(1m, issues[1].Copilot!.EstimatedGrossUsd);
        var day = DateOnly.FromDateTime(done.FiledAt.UtcDateTime);
        Assert.Equal([day, day, day.AddDays(-1)], s.Copilot.BillingDays);
        // Nothing was used before the run → the whole day counts as "during this run"
        Assert.Equal("run", done.CostBasis);
        Assert.Contains("during this run", body);
    }

    [Fact]
    public async Task WithStorage_CostIsBasedOnUsageDuringTheRun()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "NRE"));
        // Earlier that day: 100 agent credits ($1) and some chat
        s.Copilot.Billing = Day(Agent(100, 1m), Chat(50, 0.5m));

        await s.Triage.RunAsync();
        var pending = Assert.Single(s.Store.Pending.Values);
        Assert.Equal(1m, pending.BillingAtStart!.AgentGrossUsd);

        // After the session: 400 agent credits ($4) for the day → 300 credits ($3) during the run
        s.Copilot.Issues[100] = FakeCopilot.Completed(200, 20);
        s.Copilot.Billing = Day(Agent(400, 4m), Chat(50, 0.5m));
        await s.Report.RunAsync();

        var done = Assert.Single(s.Store.Completed);
        Assert.Equal("run", done.CostBasis);
        var during = done.BillingDuringRun!;
        Assert.Equal(3m, during.AgentGrossUsd);
        Assert.Equal(0m, during.CopilotGrossUsd - during.AgentGrossUsd); // chat unchanged → not part of the run
        Assert.Equal(3m, done.Projects[0].Issues[0].Copilot!.EstimatedGrossUsd);
        Assert.Equal(300m, done.Projects[0].Issues[0].Copilot!.EstimatedAiCredits);

        var (_, body) = Assert.Single(s.Mailer.Sent);
        Assert.Contains("GitHub Copilot during this run", body);
        Assert.Contains("cloud agent $3.00", body);
        Assert.Contains("cloud agent $4.00", body); // the day total is still shown
    }

    // ── Cost tracking fails soft ───────────────────────────

    [Fact]
    public async Task CostTrackingFailing_StillSendsEmail_WithNotAvailable()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "NRE"));
        s.Copilot.Throw = true;

        await s.Triage.RunAsync();                 // billing snapshot at the start throws
        var pending = Assert.Single(s.Store.Pending.Values);
        Assert.Null(pending.BillingAtStart);
        Assert.Empty(s.Mailer.Sent);               // an API error is not a finished session → waits

        await s.Report.RunAsync(force: true);      // sessions and billing throw as well

        var (_, body) = Assert.Single(s.Mailer.Sent);
        Assert.Contains("Usage &amp; cost", body);
        Assert.Contains("n/a", body);
        Assert.Contains("GitHub exploded", body);
        var done = Assert.Single(s.Store.Completed);
        Assert.Equal("error", done.Projects[0].Issues[0].Copilot!.Status);
        Assert.Null(done.Projects[0].Issues[0].Copilot!.EstimatedGrossUsd);
        Assert.Null(done.CostBasis);
    }

    [Fact]
    public async Task ApiErrorOnSession_WaitsUntilDeadline_ThenSends()
    {
        var now = DateTimeOffset.UtcNow;
        var s = Build(storageEnabled: true, () => now, ("1", "NRE"));
        await s.Triage.RunAsync();
        s.Copilot.Issues[100] = new CopilotIssueUsage { Status = "error", Error = "403 Forbidden" };

        await s.Report.RunAsync();
        Assert.Empty(s.Mailer.Sent);

        now = now.AddMinutes(121);
        await s.Report.RunAsync();
        var (_, body) = Assert.Single(s.Mailer.Sent);
        Assert.Contains("n/a", body);
    }

    [Fact]
    public async Task StoringFinalRecordFails_EmailIsNotSentTwice()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "NRE"));
        await s.Triage.RunAsync();
        s.Copilot.Issues[100] = FakeCopilot.Completed(200, 10);
        s.Store.FailComplete = true;

        await s.Report.RunAsync();
        await s.Report.RunAsync();

        Assert.Single(s.Mailer.Sent);
        Assert.True(Assert.Single(s.Store.Pending.Values).Emails!.All(e => e.Sent));

        s.Store.FailComplete = false;
        await s.Report.RunAsync();
        Assert.Single(s.Mailer.Sent);
        Assert.Single(s.Store.Completed);
    }

    [Fact]
    public void Unavailable_RendersNotAvailableWithReason()
    {
        var html = UsageReportRenderer.Render(new RunRecord { UsageError = "storage <down>" }, null);

        Assert.Contains("n/a", html);
        Assert.Contains("storage &lt;down&gt;", html);
    }

    [Fact]
    public async Task WithoutStorage_NoBillingIsRead()
    {
        var s = Build(storageEnabled: false, now: null, ("1", "NRE"));

        await s.Triage.RunAsync();

        Assert.Empty(s.Copilot.BillingDays);
    }

    [Fact]
    public void BillingDelta_SubtractsPerSkuAndModel()
    {
        var day = new DateOnly(2026, 11, 23);
        var before = Day(Agent(100, 1m), Chat(50, 0.5m, "gpt-5"));
        var after = Day(Agent(250, 2.5m), Chat(50, 0.5m, "gpt-5"), Chat(10, 0.2m, "claude"));
        before.Day = after.Day = day;

        var delta = CopilotBillingDay.Delta(before, after)!;

        Assert.Equal(day, delta.Day);
        Assert.Equal(2, delta.Items.Count); // unchanged gpt-5 chat is dropped
        Assert.Equal(150m, delta.AgentGrossQuantity);
        Assert.Equal(1.5m, delta.AgentGrossUsd);
        Assert.Equal(0.2m, Assert.Single(delta.Items, i => i.Model == "claude").GrossAmount);
    }

    [Fact]
    public void BillingDelta_NeverNegative()
    {
        // A correction by GitHub between the snapshots must not produce negative cost
        var delta = CopilotBillingDay.Delta(Day(Agent(100, 1m)), Day(Agent(80, 0.8m)))!;

        Assert.Empty(delta.Items);
        Assert.Equal(0m, delta.AgentGrossUsd);
    }

    [Fact]
    public void BillingDelta_NotComparable_ReturnsNull()
    {
        var before = Day(Agent(100, 1m));
        var otherDay = Day(Agent(200, 2m));
        otherDay.Day = before.Day.AddDays(1);
        var failed = new CopilotBillingDay { Day = before.Day, Error = "403" };
        var otherSource = Day(Agent(200, 2m));
        otherSource.Source = "premium_request";

        Assert.Null(CopilotBillingDay.Delta(before, otherDay));   // run crossed midnight UTC
        Assert.Null(CopilotBillingDay.Delta(failed, Day(Agent(200, 2m))));
        Assert.Null(CopilotBillingDay.Delta(before, failed));
        Assert.Null(CopilotBillingDay.Delta(before, otherSource));
        Assert.Null(CopilotBillingDay.Delta(null, before));
        // An empty start snapshot (nothing used yet that day) is fine whatever its report type
        Assert.NotNull(CopilotBillingDay.Delta(new CopilotBillingDay { Day = before.Day }, before));
    }

    [Fact]
    public void EstimateIssueCosts_FallsBackToDayTotal_WithoutUsableStartSnapshot()
    {
        var run = new RunRecord
        {
            Billing = Day(Agent(400, 4m)),
            Projects = [new ProjectRunUsage { Issues = [new FiledIssueUsage { Number = 1, Copilot = FakeCopilot.Completed(2, 10) }] }],
        };

        ReportPipeline.EstimateIssueCosts(run);

        Assert.Equal("day", run.CostBasis);
        Assert.Equal(4m, run.Projects[0].Issues[0].Copilot!.EstimatedGrossUsd);
    }

    private static CopilotBillingDay Day(params BillingUsageItem[] items) =>
        new() { Day = new DateOnly(2026, 11, 23), Source = "ai_credit", Items = items.ToList() };

    private static BillingUsageItem Agent(decimal credits, decimal usd) => new()
    {
        Product = "copilot", Sku = "cloud agent AI credits", UnitType = "credits",
        GrossQuantity = credits, GrossAmount = usd, NetQuantity = credits, NetAmount = usd,
    };

    private static BillingUsageItem Chat(decimal credits, decimal usd, string model = "gpt-5") => new()
    {
        Product = "copilot", Sku = "chat AI credits", Model = model, UnitType = "credits",
        GrossQuantity = credits, GrossAmount = usd, NetQuantity = credits, NetAmount = usd,
    };

    [Fact]
    public async Task WithStorage_SendsAfterMaxDelay_EvenIfCopilotNeverStarted()
    {
        Environment.SetEnvironmentVariable("REPORT_MAX_DELAY_MINUTES", "120");
        var now = DateTimeOffset.UtcNow;
        var s = Build(storageEnabled: true, () => now, ("1", "NRE"));

        await s.Triage.RunAsync();
        Assert.Empty(s.Mailer.Sent);

        now = now.AddMinutes(119);
        await s.Report.RunAsync();
        Assert.Empty(s.Mailer.Sent);

        now = now.AddMinutes(2);
        await s.Report.RunAsync();
        var (_, body) = Assert.Single(s.Mailer.Sent);
        Assert.Contains("pending", body);
        Assert.Single(s.Store.Completed);
    }

    [Fact]
    public async Task WithStorage_NoIssuesFiled_SendsRightAway()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "noise"));

        await s.Triage.RunAsync();

        Assert.Single(s.Mailer.Sent);
        Assert.Single(s.Store.Completed);
    }

    [Fact]
    public async Task ReportForce_SendsWithoutWaiting()
    {
        var s = Build(storageEnabled: true, now: null, ("1", "NRE"));
        await s.Triage.RunAsync();

        await s.Report.RunAsync(force: true);

        Assert.Single(s.Mailer.Sent);
    }

    [Fact]
    public void EstimateIssueCosts_WithoutAgentSku_LeavesEstimateEmpty()
    {
        var run = new RunRecord
        {
            Billing = new CopilotBillingDay { Items = [new BillingUsageItem { Product = "copilot", Sku = "chat", GrossAmount = 2m }] },
            Projects = [new ProjectRunUsage { Issues = [new FiledIssueUsage { Number = 1, Copilot = FakeCopilot.Completed(2, 10) }] }],
        };

        ReportPipeline.EstimateIssueCosts(run);

        Assert.Null(run.Projects[0].Issues[0].Copilot!.EstimatedGrossUsd);
    }

    // ── GitHub API parsing ─────────────────────────────────

    [Fact]
    public void LinkedPullRequests_PrefersCopilotPrs_OfTheSameRepo()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"event":"labeled"},
              {"event":"cross-referenced","source":{"issue":{"number":7,"pull_request":{},"user":{"login":"alice"},"repository":{"full_name":"org/repo"}}}},
              {"event":"cross-referenced","source":{"issue":{"number":8,"pull_request":{},"user":{"login":"Copilot"},"repository":{"full_name":"org/repo"}}}},
              {"event":"cross-referenced","source":{"issue":{"number":9,"pull_request":{},"user":{"login":"Copilot"},"repository":{"full_name":"other/repo"}}}},
              {"event":"cross-referenced","source":{"issue":{"number":10,"user":{"login":"Copilot"},"repository":{"full_name":"org/repo"}}}}
            ]
            """);

        Assert.Equal([8], CopilotUsageClient.LinkedPullRequests(doc.RootElement, "org/repo"));
    }

    [Fact]
    public void AgentSessions_IgnoresRepositoryCi_AndMeasuresMinutes()
    {
        using var doc = JsonDocument.Parse("""
            {"workflow_runs":[
              {"id":1,"name":"Copilot coding agent","path":"dynamic/copilot-swe-agent/copilot","status":"completed","conclusion":"success",
               "run_started_at":"2026-11-23T05:01:00Z","updated_at":"2026-11-23T05:19:30Z","html_url":"https://x/1"},
              {"id":2,"name":"CI","path":".github/workflows/ci.yml","status":"completed","run_started_at":"2026-11-23T05:20:00Z","updated_at":"2026-11-23T05:25:00Z"},
              {"id":3,"name":"Copilot coding agent","path":"dynamic/copilot-swe-agent/copilot","status":"in_progress","run_started_at":"2026-11-23T06:00:00Z","updated_at":"2026-11-23T06:05:00Z"}
            ]}
            """);

        var sessions = CopilotUsageClient.AgentSessions(doc.RootElement);

        Assert.Equal([1L, 3L], sessions.Select(s => s.WorkflowRunId));
        Assert.Equal(18.5, sessions[0].DurationMinutes);
        Assert.Null(sessions[1].DurationMinutes);
        Assert.Equal("running", CopilotUsageClient.SessionStatus(new CopilotIssueUsage { Sessions = sessions }));
    }

    [Fact]
    public void ParseBillingItems_ReadsAiCreditReport()
    {
        using var doc = JsonDocument.Parse("""
            {"timePeriod":{"year":2026,"month":11,"day":23},"user":"me","usageItems":[
              {"product":"Copilot","sku":"Copilot cloud agent AI credits","model":"claude-sonnet","unitType":"credits",
               "pricePerUnit":0.01,"grossQuantity":250,"grossAmount":2.5,"discountQuantity":250,"discountAmount":2.5,"netQuantity":0,"netAmount":0}
            ]}
            """);

        var item = Assert.Single(CopilotUsageClient.ParseBillingItems(doc.RootElement));
        Assert.True(item.IsCopilot);
        Assert.True(item.IsAgent);
        Assert.Equal(2.5m, item.GrossAmount);
        Assert.Equal(0m, item.NetAmount);
        Assert.Equal("claude-sonnet", item.Model);
    }

    [Fact]
    public void RunRecord_RoundTripsAsJson()
    {
        var run = new RunRecord
        {
            RunId = "r1",
            StartedAt = DateTimeOffset.Parse("2026-11-23T05:00:00Z"),
            Billing = new CopilotBillingDay { Day = new DateOnly(2026, 11, 23), Items = [new BillingUsageItem { Product = "copilot", Sku = "agent", GrossAmount = 1.25m }] },
            Projects = [new ProjectRunUsage { Slug = "app", Issues = [new FiledIssueUsage { Number = 5, Copilot = FakeCopilot.Completed(6, 12) }] }],
            Emails = [new PendingEmail { ProjectSlug = "app", Subject = "s", HtmlBody = "<p>" }],
        };

        var json = JsonSerializer.Serialize(run, BlobRunStore.Json);
        var back = JsonSerializer.Deserialize<RunRecord>(json, BlobRunStore.Json)!;

        Assert.Contains("\"agentGrossUsd\": 1.25", json);
        Assert.Contains("\"totalSessionMinutes\": 12", json);
        Assert.Equal(new DateOnly(2026, 11, 23), back.Billing!.Day);
        Assert.Equal(12, back.Projects[0].Issues[0].Copilot!.TotalSessionMinutes);
        Assert.Equal("<p>", back.Emails![0].HtmlBody);
    }
}
