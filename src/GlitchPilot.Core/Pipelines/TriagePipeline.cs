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
    private readonly IDuplicateDetector _duplicates;
    private readonly ISmtpMailer _mailer;
    private readonly ILogger<TriagePipeline> _log;

    public TriagePipeline(
        IGlitchTipClient client,
        IErrorClassifier classifier,
        IIssueFiler issueFiler,
        IDuplicateDetector duplicates,
        ISmtpMailer mailer,
        ILogger<TriagePipeline> log)
    {
        _client = client;
        _classifier = classifier;
        _issueFiler = issueFiler;
        _duplicates = duplicates;
        _mailer = mailer;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var lookbackHours = EnvironmentConfig.LookbackHours;
        // Read dedup settings up front so invalid values fail before any work is done
        var dedup = new DedupSettings(
            EnvironmentConfig.MergeDuplicatesEnabled,
            EnvironmentConfig.DuplicateScanEnabled,
            EnvironmentConfig.DuplicateScanMaxIssues);
        _log.LogInformation("Triage started — lookback {H}h, merge duplicates: {Merge}, history scan: {Scan}",
            lookbackHours, dedup.MergeEnabled, dedup.HistoryScanEnabled);

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

            // Phase 1: classify every candidate
            var projectOutcomes = new List<TriageOutcome>();
            foreach (var candidate in candidates)
            {
                candidate.ProjectSlug = match.Slug;
                candidate.GitHubOwner = monitored.GitHubOwner;
                candidate.GitHubRepo = monitored.GitHubRepo;
                var verdict = await _classifier.EvaluateAsync(candidate, ct);

                _log.LogInformation("  #{Id} [{Priority}/{Type}] {Title}",
                    candidate.ErrorId, verdict.Priority, verdict.ErrorType,
                    !string.IsNullOrWhiteSpace(verdict.IssueTitle) ? verdict.IssueTitle : candidate.ErrorTitle);

                projectOutcomes.Add(new TriageOutcome
                {
                    Candidate = candidate,
                    Verdict = verdict,
                });
            }

            // Phase 2: deduplicate and file GitHub issues for actionable errors
            await ResolveAndFileAsync(monitored, projectOutcomes, dedup, ct);

            outcomes.AddRange(projectOutcomes);
        }

        await SendNotificationAsync(outcomes, lookbackHours, ct);

        _log.LogInformation(
            "Triage done — {Total} scanned, {Actionable} actionable, {Merged} merged, {Known} already known",
            outcomes.Count,
            outcomes.Count(IsNewActionable),
            outcomes.Count(o => o.MergedInto is not null),
            outcomes.Count(o => o.DuplicateOf is not null && o.MergedInto is null));
    }

    internal sealed record DedupSettings(bool MergeEnabled, bool HistoryScanEnabled, int HistoryScanMaxItems);

    /// <summary>
    /// Groups actionable errors that share a root cause, skips errors that are already tracked
    /// on GitHub (label or — optionally — AI history scan), and files one issue per remaining group.
    /// </summary>
    private async Task ResolveAndFileAsync(
        MonitoredProject project, List<TriageOutcome> projectOutcomes, DedupSettings dedup, CancellationToken ct)
    {
        var actionable = projectOutcomes.Where(o => o.Verdict.RequiresAction).ToList();
        if (actionable.Count == 0) return;

        var owner = project.GitHubOwner;
        var repo = project.GitHubRepo;

        // 1) Merge errors of this run that GlitchTip split into several groups
        var groups = dedup.MergeEnabled
            ? await _duplicates.GroupAsync(actionable, ct)
            : actionable.Select(o => new MergeGroup { Members = [o] }).ToList();

        var primaries = groups.Select(PromotePrimary).ToList();

        // 2) Which errors are already tracked via glitchtip:{id} labels (one search for all errors of the project).
        //    If the lookup fails we do not file anything: creating duplicates costs a Copilot run each.
        IReadOnlyDictionary<string, ExistingWorkItem> tracked;
        try
        {
            tracked = await _issueFiler.GetTrackedErrorsAsync(
                owner, repo, primaries.SelectMany(p => p.AllErrorIds), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "GitHub lookup failed for {Owner}/{Repo} — not filing issues this run", owner, repo);
            foreach (var primary in primaries)
                primary.GitHubError = "GitHub could not be queried for existing issues — not filed to avoid duplicates.";
            PropagateToMerged(primaries);
            return;
        }

        var untracked = new List<TriageOutcome>();
        foreach (var primary in primaries)
        {
            if (TrackingLabels.Find(tracked, primary.AllErrorIds) is { } item)
            {
                // Already known: an earlier run filed (or matched) an issue / PR for this error
                primary.GitHubIssueNumber = item.Number;
                primary.GitHubIssueUrl = item.Url;
                primary.DuplicateOf = new KnownDuplicate
                {
                    Number = item.Number,
                    Url = item.Url,
                    Title = item.Title,
                    IsPullRequest = item.IsPullRequest,
                    CreatedAt = item.CreatedAt,
                    Reason = "Already tracked via its glitchtip label from an earlier run.",
                };

                // Newly merged errors join the existing issue
                var missing = TrackingLabels.Missing(tracked, item.Number, primary.AllErrorIds);
                if (missing.Count > 0)
                    await _issueFiler.AddTrackingLabelsAsync(owner, repo, item.Number, missing, ct);
            }
            else
            {
                untracked.Add(primary);
            }
        }

        // 3) Optional: compare with open GitHub issues / PRs (e.g. unmerged fix from months ago)
        if (dedup.HistoryScanEnabled && untracked.Count > 0)
        {
            IReadOnlyList<ExistingWorkItem> existing = [];
            try
            {
                existing = await _issueFiler.ListOpenWorkAsync(owner, repo, dedup.HistoryScanMaxItems, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Could not load open GitHub work for {Owner}/{Repo} — skipping history scan",
                    owner, repo);
            }

            var matches = await _duplicates.MatchExistingAsync(untracked, existing, ct);
            foreach (var primary in untracked.ToList())
            {
                if (!matches.TryGetValue(primary.Candidate.ErrorId, out var known)) continue;

                primary.DuplicateOf = known;
                untracked.Remove(primary);
                try
                {
                    await _issueFiler.AnnotateRecurrenceAsync(primary, known, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Failed to annotate #{Num} for error {Id}", known.Number, primary.Candidate.ErrorId);
                }
            }
        }

        // 4) File one issue per remaining group
        foreach (var primary in untracked)
        {
            try
            {
                var issueNumber = await _issueFiler.CreateAsync(primary, ct);
                if (issueNumber.HasValue)
                {
                    primary.GitHubIssueNumber = issueNumber;
                    primary.GitHubIssueUrl = IssueUrl(project, issueNumber.Value);
                }
                else
                {
                    primary.GitHubError = "Filing the GitHub issue failed — see logs.";
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Failed to file issue for error {Id}", primary.Candidate.ErrorId);
                primary.GitHubError = "Filing the GitHub issue failed — see logs.";
            }
        }

        PropagateToMerged(primaries);
    }

    private static void PropagateToMerged(IEnumerable<TriageOutcome> primaries)
    {
        foreach (var primary in primaries)
        {
            foreach (var merged in primary.MergedOutcomes)
            {
                merged.GitHubIssueNumber = primary.GitHubIssueNumber;
                merged.GitHubIssueUrl = primary.GitHubIssueUrl;
                merged.GitHubError = primary.GitHubError;
                merged.DuplicateOf = primary.DuplicateOf;
            }
        }
    }

    private static string IssueUrl(MonitoredProject project, int number) =>
        $"https://github.com/{project.GitHubOwner}/{project.GitHubRepo}/issues/{number}";

    /// <summary>
    /// Picks the representative of a merge group (highest priority, then most occurrences)
    /// and links the other members to it.
    /// </summary>
    internal static TriageOutcome PromotePrimary(MergeGroup group)
    {
        var ordered = group.Members
            .OrderByDescending(o => PriorityRank(o.Verdict.Priority))
            .ThenByDescending(o => o.Candidate.Occurrences)
            .ThenBy(o => o.Candidate.ErrorId, StringComparer.Ordinal)
            .ToList();

        var primary = ordered[0];
        primary.MergedOutcomes = ordered.Skip(1).ToList();
        primary.MergeReason = group.Reason;
        foreach (var m in primary.MergedOutcomes)
            m.MergedInto = primary;
        return primary;
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    /// <summary>Actionable, not merged into another error and not matched to existing GitHub work.</summary>
    internal static bool IsNewActionable(TriageOutcome o) =>
        o.Verdict.RequiresAction && o.MergedInto is null && o.DuplicateOf is null;

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
            var actionable = items.Count(IsNewActionable);
            var known = items.Count(o => o.DuplicateOf is not null && o.MergedInto is null);

            var subject = actionable > 0
                ? $"GlitchPilot: {actionable} {Plural(actionable, "issue")} {(actionable == 1 ? "needs" : "need")} attention [{slug}]"
                : known > 0
                    ? $"GlitchPilot: {known} known {Plural(known, "issue")} recurred [{slug}]"
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
