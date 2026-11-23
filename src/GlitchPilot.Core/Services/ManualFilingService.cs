using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Services;

public enum ManualFilingStatus { Ready, Created, AlreadyTracked, NotFound, Failed }

/// <summary>What the confirmation page shows before anything is filed.</summary>
public sealed record ManualFilingPreview(
    ManualFilingStatus Status, TriageCandidate? Candidate = null, ExistingWorkItem? Existing = null, string? Message = null);

/// <summary>Result of filing an issue from the email button.</summary>
public sealed record ManualFilingResult(
    ManualFilingStatus Status, int? Number = null, string? Url = null, string? Message = null);

/// <summary>
/// Files a GitHub issue for an error GlitchPilot dismissed (e.g. as transient) when a person asks for it
/// via the email button. The repository comes from PROJECTS, never from the link.
/// </summary>
public sealed class ManualFilingService
{
    public const int MaxNotesLength = 4000;

    private readonly IGlitchTipClient _glitchTip;
    private readonly IErrorClassifier _classifier;
    private readonly IIssueFiler _filer;
    private readonly ILogger<ManualFilingService> _log;
    private readonly Func<IReadOnlyList<MonitoredProject>> _projects;

    public ManualFilingService(IGlitchTipClient glitchTip, IErrorClassifier classifier, IIssueFiler filer,
        ILogger<ManualFilingService> log)
        : this(glitchTip, classifier, filer, log, LoadProjects)
    {
    }

    internal ManualFilingService(IGlitchTipClient glitchTip, IErrorClassifier classifier, IIssueFiler filer,
        ILogger<ManualFilingService> log, Func<IReadOnlyList<MonitoredProject>> projects)
    {
        _glitchTip = glitchTip;
        _classifier = classifier;
        _filer = filer;
        _log = log;
        _projects = projects;
    }

    /// <summary>Loads the error for the confirmation page. Side-effect free (mail scanners open links).</summary>
    public async Task<ManualFilingPreview> PreviewAsync(string projectSlug, string errorId, CancellationToken ct = default)
    {
        var project = FindProject(projectSlug);
        if (project is null)
            return new(ManualFilingStatus.NotFound, Message: $"Project '{projectSlug}' is not monitored by GlitchPilot.");

        var candidate = await LoadCandidateAsync(project, errorId, ct);
        if (candidate is null)
            return new(ManualFilingStatus.NotFound, Message: $"GlitchTip error #{errorId} could not be loaded.");

        try
        {
            var tracked = await _filer.GetTrackedErrorsAsync(project.GitHubOwner, project.GitHubRepo, [errorId], ct);
            if (tracked.TryGetValue(errorId, out var existing))
                return new(ManualFilingStatus.AlreadyTracked, candidate, existing);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Not fatal for the preview — FileAsync checks again and refuses to file if GitHub is unreachable
            _log.LogWarning(ex, "GitHub lookup for error {Id} failed during preview", errorId);
        }

        return new(ManualFilingStatus.Ready, candidate);
    }

    public async Task<ManualFilingResult> FileAsync(string projectSlug, string errorId, string? notes,
        CancellationToken ct = default)
    {
        var project = FindProject(projectSlug);
        if (project is null)
            return new(ManualFilingStatus.NotFound, Message: $"Project '{projectSlug}' is not monitored by GlitchPilot.");

        // Duplicate guard first: repeated clicks or a second recipient must not start a second Copilot run
        IReadOnlyDictionary<string, ExistingWorkItem> tracked;
        try
        {
            tracked = await _filer.GetTrackedErrorsAsync(project.GitHubOwner, project.GitHubRepo, [errorId], ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "GitHub lookup for error {Id} failed — not filing to avoid duplicates", errorId);
            return new(ManualFilingStatus.Failed,
                Message: "GitHub could not be queried for existing issues. Nothing was filed — please try again later.");
        }

        if (tracked.TryGetValue(errorId, out var existing))
            return new(ManualFilingStatus.AlreadyTracked, existing.Number, existing.Url);

        var candidate = await LoadCandidateAsync(project, errorId, ct);
        if (candidate is null)
            return new(ManualFilingStatus.NotFound, Message: $"GlitchTip error #{errorId} could not be loaded.");

        // Re-classify for fresh issue text (title, description, files); the person overrides the decision
        var verdict = await _classifier.EvaluateAsync(candidate, ct);
        verdict.RequiresAction = true;

        var outcome = new TriageOutcome
        {
            Candidate = candidate,
            Verdict = verdict,
            FiledManually = true,
            ManualNotes = NormalizeNotes(notes),
        };

        var number = await _filer.CreateAsync(outcome, ct);
        if (number is null)
            return new(ManualFilingStatus.Failed, Message: "Filing the GitHub issue failed — see the function logs.");

        _log.LogInformation("Manually filed #{Num} for GlitchTip error {Id} ({Slug})", number, errorId, projectSlug);
        return new(ManualFilingStatus.Created, number,
            $"https://github.com/{project.GitHubOwner}/{project.GitHubRepo}/issues/{number}");
    }

    internal static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var trimmed = notes.Trim();
        return trimmed.Length > MaxNotesLength ? trimmed[..MaxNotesLength] : trimmed;
    }

    private MonitoredProject? FindProject(string slug) =>
        _projects().FirstOrDefault(p => p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    private async Task<TriageCandidate?> LoadCandidateAsync(MonitoredProject project, string errorId, CancellationToken ct)
    {
        TriageCandidate? candidate;
        try
        {
            candidate = await _glitchTip.GetIssueAsync(errorId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not load GlitchTip error {Id}", errorId);
            return null;
        }

        if (candidate is null) return null;
        candidate.ProjectSlug = project.Slug;
        candidate.GitHubOwner = project.GitHubOwner;
        candidate.GitHubRepo = project.GitHubRepo;
        return candidate;
    }

    private static IReadOnlyList<MonitoredProject> LoadProjects() =>
        JsonSerializer.Deserialize<List<MonitoredProject>>(
            EnvironmentConfig.ProjectsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
}
