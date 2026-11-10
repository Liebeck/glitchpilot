using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

public interface IDuplicateDetector
{
    /// <summary>
    /// Groups errors of the same run that share a root cause. Every input outcome appears in
    /// exactly one group. On any failure every outcome is returned as its own group.
    /// </summary>
    Task<List<MergeGroup>> GroupAsync(IReadOnlyList<TriageOutcome> outcomes, CancellationToken ct = default);

    /// <summary>
    /// Matches outcomes against open GitHub issues / PRs. Returns only confident matches,
    /// keyed by the outcome's GlitchTip error ID. On any failure an empty result is returned.
    /// </summary>
    Task<Dictionary<string, KnownDuplicate>> MatchExistingAsync(
        IReadOnlyList<TriageOutcome> outcomes,
        IReadOnlyList<ExistingWorkItem> existing,
        CancellationToken ct = default);
}

public class MergeGroup
{
    public List<TriageOutcome> Members { get; set; } = [];
    public string? Reason { get; set; }
}
