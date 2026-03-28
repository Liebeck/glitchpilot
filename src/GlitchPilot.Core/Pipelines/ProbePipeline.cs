using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Pipelines;

public sealed class ProbePipeline
{
    private readonly IGlitchTipClient _client;
    private readonly IErrorClassifier _classifier;
    private readonly IIssueFiler _filer;
    private readonly ISmtpMailer _mailer;
    private readonly ILogger<ProbePipeline> _log;

    public ProbePipeline(
        IGlitchTipClient client,
        IErrorClassifier classifier,
        IIssueFiler filer,
        ISmtpMailer mailer,
        ILogger<ProbePipeline> log)
    {
        _client = client;
        _classifier = classifier;
        _filer = filer;
        _mailer = mailer;
        _log = log;
    }

    public async Task RunAsync(string issueId, bool fileIssue = false, bool sendEmail = false, CancellationToken ct = default)
    {
        _log.LogInformation("=== Probe started for issue {IssueId} ===", issueId);

        // Step 1: Fetch from GlitchTip
        _log.LogInformation("[1/3] Fetching issue {IssueId} from GlitchTip...", issueId);

        var candidate = await _client.GetIssueAsync(issueId, ct);

        if (candidate is null)
        {
            _log.LogError("Issue {IssueId} not found or could not be enriched", issueId);
            return;
        }

        _log.LogInformation("[1/3] Issue fetched successfully:");
        _log.LogInformation("  Title:        {Title}", candidate.ErrorTitle);
        _log.LogInformation("  Source:       {Source}", candidate.SourceLocation ?? "(none)");
        _log.LogInformation("  Function:     {Function}", candidate.FunctionName ?? "(none)");
        _log.LogInformation("  Severity:     {Severity}", candidate.SeverityLevel);
        _log.LogInformation("  Occurrences:  {Count}", candidate.Occurrences);
        _log.LogInformation("  First seen:   {First}", candidate.DetectedAt ?? "(unknown)");
        _log.LogInformation("  Last seen:    {Last}", candidate.LastOccurredAt ?? "(unknown)");
        _log.LogInformation("  Environment:  {Env}", candidate.Environment ?? "(unknown)");
        _log.LogInformation("  Endpoint:     {Endpoint}", candidate.HttpEndpoint ?? "(none)");
        _log.LogInformation("  Handled:      {Handled}", candidate.ExceptionHandled?.ToString() ?? "(unknown)");
        _log.LogInformation("  Release:      {Release}", candidate.AppRelease ?? "(unknown)");
        _log.LogInformation("  Server OS:    {Os}", candidate.ServerOs ?? "(unknown)");
        _log.LogInformation("  User ID:      {UserId}", candidate.UserId ?? "(none)");
        _log.LogInformation("  Link:         {Link}", candidate.GlitchTipLink);

        if (candidate.StackTrace is not null)
        {
            _log.LogInformation("  Stack trace:");
            foreach (var line in candidate.StackTrace.Split('\n'))
                _log.LogInformation("    {Line}", line);
        }

        if (candidate.Breadcrumbs.Count > 0)
        {
            _log.LogInformation("  Breadcrumbs ({Count}):", candidate.Breadcrumbs.Count);
            foreach (var b in candidate.Breadcrumbs)
                _log.LogInformation("    [{Timestamp}] {Category}: {Message}",
                    b.Timestamp ?? "?", b.Category ?? "?", b.Message ?? "(no message)");
        }

        if (candidate.RequestHeaders is not null)
        {
            _log.LogInformation("  Request headers:");
            foreach (var line in candidate.RequestHeaders.Split('\n'))
                _log.LogInformation("    {Header}", line);
        }

        if (candidate.Tags.Count > 0)
        {
            _log.LogInformation("  Tags ({Count}):", candidate.Tags.Count);
            foreach (var (key, value) in candidate.Tags)
                _log.LogInformation("    {Key}: {Value}", key, value);
        }

        // Step 2: Classify with OpenAI
        _log.LogInformation("[2/3] Sending to OpenAI for classification...");

        var verdict = await _classifier.EvaluateAsync(candidate, ct);

        _log.LogInformation("[2/3] Classification result:");
        _log.LogInformation("  Requires action: {Action}", verdict.RequiresAction);
        _log.LogInformation("  Priority:        {Priority}", verdict.Priority);
        _log.LogInformation("  Type:            {Type}", verdict.ErrorType);
        _log.LogInformation("  Explanation:     {Explanation}", verdict.Explanation);
        _log.LogInformation("  Issue title:     {Title}", verdict.IssueTitle);
        _log.LogInformation("  Problem:         {Desc}", verdict.ProblemDescription);

        if (verdict.AcceptanceCriteria.Count > 0)
        {
            _log.LogInformation("  Acceptance criteria ({Count}):", verdict.AcceptanceCriteria.Count);
            foreach (var c in verdict.AcceptanceCriteria)
                _log.LogInformation("    - {Criterion}", c);
        }

        if (verdict.FilesToInvestigate.Count > 0)
        {
            _log.LogInformation("  Files to investigate ({Count}):", verdict.FilesToInvestigate.Count);
            foreach (var f in verdict.FilesToInvestigate)
                _log.LogInformation("    - {File}", f);
        }

        _log.LogInformation("  Recommendation:  {Rec}", verdict.Recommendation);
        _log.LogInformation("  Email summary:   {Summary}", verdict.EmailSummary);

        // Step 3: File GitHub issue (optional)
        var outcome = new TriageOutcome { Candidate = candidate, Verdict = verdict };

        if (fileIssue)
        {
            _log.LogInformation("[3/4] Filing GitHub issue...");
            var issueNumber = await _filer.FileAsync(outcome, ct);

            if (issueNumber.HasValue)
            {
                outcome.GitHubIssueNumber = issueNumber;
                outcome.GitHubIssueUrl = $"https://github.com/{candidate.GitHubOwner}/{candidate.GitHubRepo}/issues/{issueNumber}";
                _log.LogInformation("[3/4] Filed GitHub issue #{Num}", issueNumber.Value);
            }
            else
                _log.LogWarning("[3/4] Failed to file GitHub issue");
        }
        else
        {
            _log.LogInformation("[3/4] Skipped filing (use --file to create a GitHub issue)");
        }

        // Step 4: Send email (optional)
        if (sendEmail)
        {
            _log.LogInformation("[4/4] Sending email...");
            var slug = candidate.ProjectSlug;
            if (string.IsNullOrWhiteSpace(slug)) slug = "probe";

            var outcomes = new List<TriageOutcome> { outcome };
            var environment = candidate.Environment ?? "—";
            var body = TriageReportRenderer.RenderSummary(slug, outcomes,
                environment, 1, EnvironmentConfig.LookbackHours);

            var actionable = verdict.RequiresAction ? 1 : 0;
            var subject = actionable > 0
                ? $"GlitchPilot: 1 issue needs attention [{slug}]"
                : $"GlitchPilot: 1 issue scanned, all clear [{slug}]";

            try
            {
                await _mailer.DeliverAsync(subject, body, ct);
                _log.LogInformation("[4/4] Email sent");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[4/4] Failed to send email");
            }
        }
        else
        {
            _log.LogInformation("[4/4] Skipped email (use --email to send)");
        }

        _log.LogInformation("=== Probe complete ===");
    }
}
