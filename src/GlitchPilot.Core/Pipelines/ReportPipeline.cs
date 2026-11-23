using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Pipelines;

/// <summary>
/// Sends the triage emails of pending runs once their Copilot sessions finished (at the latest after
/// REPORT_MAX_DELAY_MINUTES), with Copilot / AI-credit usage, and stores the final run JSON.
/// </summary>
public sealed class ReportPipeline
{
    private readonly IRunStore _store;
    private readonly ICopilotUsageClient _copilot;
    private readonly ISmtpMailer _mailer;
    private readonly ILogger<ReportPipeline> _log;
    private readonly Func<DateTimeOffset> _now;

    public ReportPipeline(IRunStore store, ICopilotUsageClient copilot, ISmtpMailer mailer, ILogger<ReportPipeline> log)
        : this(store, copilot, mailer, log, () => DateTimeOffset.UtcNow) { }

    internal ReportPipeline(IRunStore store, ICopilotUsageClient copilot, ISmtpMailer mailer,
        ILogger<ReportPipeline> log, Func<DateTimeOffset> now)
    {
        _store = store;
        _copilot = copilot;
        _mailer = mailer;
        _log = log;
        _now = now;
    }

    public bool StorageEnabled => _store.IsEnabled;

    /// <summary>
    /// Stores the day's billing before any Copilot session of the run starts, so the report can subtract
    /// what was used earlier that day. Only with storage — without it there is no delayed report.
    /// </summary>
    public async Task SnapshotBillingAsync(RunRecord run, CancellationToken ct = default)
    {
        if (!_store.IsEnabled) return;
        try
        {
            run.BillingAtStart = await _copilot.GetBillingDayAsync(DateOnly.FromDateTime(run.StartedAt.UtcDateTime), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Run {RunId}: could not read the billing at start, the report will use the day totals", run.RunId);
        }
    }

    /// <summary>Processes all pending runs. <paramref name="force"/> sends them without waiting for Copilot.</summary>
    public async Task RunAsync(bool force = false, CancellationToken ct = default)
    {
        if (!_store.IsEnabled)
        {
            _log.LogWarning("No usage storage configured (USAGE_STORAGE_CONNECTION) — nothing to report");
            return;
        }

        var pending = await _store.ListPendingAsync(ct);
        _log.LogInformation("{Count} pending run(s)", pending.Count);
        foreach (var run in pending)
        {
            try
            {
                await TryReportAsync(run, force, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Reporting run {RunId} failed — retrying on the next run", run.RunId);
            }
        }
    }

    /// <summary>
    /// Sends the run's emails if its Copilot sessions are done (or the delay is over). Returns true if sent.
    /// The cost tracking is not tested against the live GitHub API yet, so every step of it fails soft:
    /// whatever cannot be determined is reported as "n/a" and the emails still go out.
    /// </summary>
    public async Task<bool> TryReportAsync(RunRecord run, bool force = false, CancellationToken ct = default)
    {
        var issues = run.Projects.SelectMany(p => p.Issues.Select(i => (Project: p, Issue: i))).ToList();
        foreach (var (project, issue) in issues)
            issue.Copilot = await ReadIssueUsageAsync(project, issue.Number, ct);

        // An API error (Error set) is not a finished session: keep waiting until the deadline
        var deadline = run.FiledAt.AddMinutes(EnvironmentConfig.ReportMaxDelayMinutes);
        static bool Done(CopilotIssueUsage c) => c.IsFinished && c.Error is null;
        if (!force && !issues.All(i => Done(i.Issue.Copilot!)) && _now() < deadline)
        {
            _log.LogInformation("Run {RunId}: {Open} of {Total} Copilot session(s) still open — waiting (until {Deadline:HH:mm} UTC at the latest)",
                run.RunId, issues.Count(i => !Done(i.Issue.Copilot!)), issues.Count, deadline);
            return false;
        }

        var day = DateOnly.FromDateTime(run.FiledAt.UtcDateTime);
        run.Billing = await ReadBillingAsync(day, ct);
        run.BillingPreviousDay = await ReadBillingAsync(day.AddDays(-1), ct);
        try
        {
            run.BillingDuringRun = CopilotBillingDay.Delta(run.BillingAtStart, run.Billing);
            EstimateIssueCosts(run);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run {RunId}: could not estimate the Copilot cost — reporting it as n/a", run.RunId);
            run.BillingDuringRun = null;
            run.CostBasis = null;
            run.UsageError = $"cost estimate failed: {ex.Message}";
            foreach (var (_, issue) in issues)
            {
                issue.Copilot!.EstimatedGrossUsd = null;
                issue.Copilot.EstimatedAiCredits = null;
            }
        }
        run.ReportedAt = _now();

        await SendEmailsAsync(run, ct);

        try
        {
            await _store.CompleteAsync(run, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Keep the "sent" flags so the next report run only finishes the record and sends nothing twice
            _log.LogError(ex, "Run {RunId}: emails sent, but the final usage JSON could not be stored — retrying on the next run", run.RunId);
            try { await _store.SavePendingAsync(run, ct); }
            catch (Exception inner) when (!ct.IsCancellationRequested)
            {
                _log.LogError(inner, "Run {RunId}: could not update the pending run either", run.RunId);
            }
        }

        _log.LogInformation("Run {RunId} reported — {Issues} issue(s), {Minutes:0.0} Copilot minute(s), cost basis {Basis}, Copilot day so far {Gross}",
            run.RunId, issues.Count, issues.Sum(i => i.Issue.Copilot!.TotalSessionMinutes), run.CostBasis ?? "n/a",
            run.Billing.Error is null ? $"${run.Billing.CopilotGrossUsd:0.00}" : "n/a");
        return true;
    }

    private async Task<CopilotIssueUsage> ReadIssueUsageAsync(ProjectRunUsage project, int issueNumber, CancellationToken ct)
    {
        try
        {
            return await _copilot.GetIssueUsageAsync(project.GitHubOwner, project.GitHubRepo, issueNumber, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not read Copilot activity for {Owner}/{Repo}#{Num}", project.GitHubOwner, project.GitHubRepo, issueNumber);
            return new CopilotIssueUsage { Status = "error", Error = ex.Message };
        }
    }

    private async Task<CopilotBillingDay> ReadBillingAsync(DateOnly day, CancellationToken ct)
    {
        try
        {
            return await _copilot.GetBillingDayAsync(day, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not read GitHub billing usage for {Day}", day);
            return new CopilotBillingDay { Day = day, FetchedAt = _now(), Error = ex.Message };
        }
    }

    /// <summary>Fills the usage section into every unsent email of the run and sends it (failures are logged).</summary>
    public async Task SendEmailsAsync(RunRecord run, CancellationToken ct = default)
    {
        foreach (var email in run.Emails ?? [])
        {
            if (email.Sent) continue;
            string body;
            try
            {
                body = UsageReportRenderer.InsertInto(email.HtmlBody, run, email.ProjectSlug);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not render the usage section for project {Slug} — sending the email with n/a", email.ProjectSlug);
                body = email.HtmlBody.Replace(TriageReportRenderer.UsagePlaceholder, UsageReportRenderer.Unavailable(ex.Message));
            }

            try
            {
                await _mailer.DeliverAsync(email.Subject, body, ct);
                email.Sent = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Failed to send email for project {Slug}", email.ProjectSlug);
            }
        }
    }

    /// <summary>
    /// Splits the cloud-agent cost used during the run (report snapshot of the day minus start snapshot)
    /// across the run's issues by session minutes; falls back to the whole day if the snapshots are not
    /// comparable. Upper bound: agent sessions not started by GlitchPilot that ran at the same time
    /// (or, with the fallback, earlier that day) are attributed to these issues too.
    /// </summary>
    internal static void EstimateIssueCosts(RunRecord run)
    {
        var day = run.Billing is { Error: null } ok ? ok : null;
        var billing = run.BillingDuringRun ?? day;
        run.CostBasis = run.BillingDuringRun is not null ? "run" : day is not null ? "day" : null;

        var issues = run.Projects.SelectMany(p => p.Issues).Where(i => i.Copilot is not null).ToList();
        var totalMinutes = issues.Sum(i => i.Copilot!.TotalSessionMinutes);
        if (billing is null || totalMinutes <= 0 || billing.AgentGrossUsd <= 0) return;

        foreach (var issue in issues)
        {
            var share = (decimal)(issue.Copilot!.TotalSessionMinutes / totalMinutes);
            issue.Copilot.EstimatedGrossUsd = Math.Round(billing.AgentGrossUsd * share, 4);
            issue.Copilot.EstimatedAiCredits = Math.Round(billing.AgentGrossQuantity * share, 2);
        }
    }
}
