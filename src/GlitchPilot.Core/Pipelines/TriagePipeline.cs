using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Pipelines;

public sealed class TriagePipeline
{
    private readonly IGlitchTipClient _client;
    private readonly IErrorClassifier _classifier;
    private readonly IIssueFiler _issueFiler;
    private readonly ISmtpMailer _mailer;
    private readonly ILogger<TriagePipeline> _log;

    public TriagePipeline(
        IGlitchTipClient client,
        IErrorClassifier classifier,
        IIssueFiler issueFiler,
        ISmtpMailer mailer,
        ILogger<TriagePipeline> log)
    {
        _client = client;
        _classifier = classifier;
        _issueFiler = issueFiler;
        _mailer = mailer;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var lookbackHours = EnvironmentConfig.LookbackHours;
        _log.LogInformation("Triage started — lookback {H}h", lookbackHours);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-lookbackHours);
        var projects = await _client.ListProjectsAsync(ct);
        var monitoredProjects = JsonSerializer.Deserialize<List<MonitoredProject>>(
            EnvironmentConfig.ProjectsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        var outcomes = new List<TriageOutcome>();

        foreach (var monitored in monitoredProjects)
        {
            var match = projects.FirstOrDefault(p =>
                p.Slug.Equals(monitored.Slug, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals(monitored.Slug, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _log.LogWarning("Project {Slug} not found — skipping", monitored.Slug);
                continue;
            }

            var candidates = await _client.CollectCandidatesAsync(
                match.Slug, monitored.TargetEnvironment, cutoff, ct);

            _log.LogInformation("{Slug}/{Env}: {N} candidate(s)",
                monitored.Slug, monitored.TargetEnvironment, candidates.Count);

            foreach (var candidate in candidates)
            {
                candidate.ProjectSlug = match.Slug;
                candidate.GitHubOwner = monitored.GitHubOwner;
                candidate.GitHubRepo = monitored.GitHubRepo;
                var verdict = await _classifier.EvaluateAsync(candidate, ct);

                _log.LogInformation("  #{Id} [{Priority}/{Type}] {Title}",
                    candidate.ErrorId, verdict.Priority, verdict.ErrorType,
                    !string.IsNullOrWhiteSpace(verdict.IssueTitle) ? verdict.IssueTitle : candidate.ErrorTitle);

                var outcome = new TriageOutcome
                {
                    Candidate = candidate,
                    Verdict = verdict,
                };

                // File GitHub issue for actionable errors
                if (verdict.RequiresAction)
                {
                    try
                    {
                        var issueNumber = await _issueFiler.FileAsync(outcome, ct);
                        if (issueNumber.HasValue)
                        {
                            outcome.GitHubIssueNumber = issueNumber;
                            outcome.GitHubIssueUrl = $"https://github.com/{candidate.GitHubOwner}/{candidate.GitHubRepo}/issues/{issueNumber}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to file issue for error {Id}", candidate.ErrorId);
                    }
                }

                outcomes.Add(outcome);
            }
        }

        await SendNotificationAsync(outcomes, lookbackHours, ct);

        var actionable = outcomes.Count(o => o.Verdict.RequiresAction);
        _log.LogInformation("Triage done — {Total} scanned, {Actionable} actionable",
            outcomes.Count, actionable);
    }

    private async Task SendNotificationAsync(
        List<TriageOutcome> outcomes, int lookbackHours, CancellationToken ct)
    {
        var byProject = outcomes
            .GroupBy(o => o.Candidate.ProjectSlug)
            .ToList();

        foreach (var group in byProject)
        {
            var slug = group.Key;
            var items = group.ToList();
            var actionable = items.Count(o => o.Verdict.RequiresAction);

            var subject = actionable > 0
                ? $"GlitchPilot: {actionable} {Plural(actionable, "issue")} {(actionable == 1 ? "needs" : "need")} attention [{slug}]"
                : $"GlitchPilot: {items.Count} {Plural(items.Count, "issue")} scanned, all clear [{slug}]";

            var environment = items.FirstOrDefault()?.Candidate.Environment ?? "—";
            var body = TriageReportRenderer.RenderSummary(slug, items,
                environment, EnvironmentConfig.MinOccurrences, lookbackHours);

            try
            {
                await _mailer.DeliverAsync(subject, body, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send email for project {Slug}", slug);
            }
        }

        // Send all-clear for monitored projects that had zero candidates
        var projectsWithOutcomes = byProject.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitoredProjects = JsonSerializer.Deserialize<List<MonitoredProject>>(
            EnvironmentConfig.ProjectsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        foreach (var mp in monitoredProjects)
        {
            if (projectsWithOutcomes.Contains(mp.Slug)) continue;

            try
            {
                await _mailer.DeliverAsync(
                    $"GlitchPilot: All clear [{mp.Slug}]",
                    TriageReportRenderer.RenderQuietNight(mp.Slug, 0, lookbackHours),
                    ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send email for project {Slug}", mp.Slug);
            }
        }
    }

    private static string Plural(int count, string word) =>
        count == 1 ? word : word + "s";
}
