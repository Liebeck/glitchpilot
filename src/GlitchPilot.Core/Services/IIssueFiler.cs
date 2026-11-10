using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

public interface IIssueFiler
{
    /// <summary>
    /// Files a GitHub issue for the outcome (and all errors merged into it) and assigns Copilot.
    /// Returns the existing issue number if one is already tracked via a glitchtip:{id} label,
    /// and null if filing failed or the duplicate lookup could not be performed.
    /// </summary>
    Task<int?> FileAsync(TriageOutcome outcome, CancellationToken ct = default);

    /// <summary>Creates the issue and assigns Copilot without a duplicate check. Returns null on failure.</summary>
    Task<int?> CreateAsync(TriageOutcome outcome, CancellationToken ct = default);

    /// <summary>
    /// Maps the given GlitchTip error IDs to the open issue / PR carrying their glitchtip:{id} label
    /// (IDs without one are absent). Throws if GitHub cannot be queried — a failed lookup must not be
    /// treated as "not tracked".
    /// </summary>
    Task<IReadOnlyDictionary<string, ExistingWorkItem>> GetTrackedErrorsAsync(
        string owner, string repo, IEnumerable<string> errorIds, CancellationToken ct = default);

    /// <summary>Adds glitchtip:{id} labels for the given errors to an existing issue / PR (best effort).</summary>
    Task AddTrackingLabelsAsync(string owner, string repo, int number, IEnumerable<string> errorIds, CancellationToken ct = default);

    /// <summary>Lists open GlitchPilot issues and related open pull requests (at most maxItems) for the history scan.</summary>
    Task<IReadOnlyList<ExistingWorkItem>> ListOpenWorkAsync(string owner, string repo, int maxItems, CancellationToken ct = default);

    /// <summary>Adds glitchtip:{id} labels and a recurrence comment to an already open issue / PR.</summary>
    Task AnnotateRecurrenceAsync(TriageOutcome outcome, KnownDuplicate known, CancellationToken ct = default);
}
