namespace GlitchPilot.Core.Models;

/// <summary>The result of processing a single error through the triage pipeline.</summary>
public class TriageOutcome
{
    public TriageCandidate Candidate { get; set; } = null!;
    public Verdict Verdict { get; set; } = null!;
    public int? GitHubIssueNumber { get; set; }
    public string? GitHubIssueUrl { get; set; }

    /// <summary>Why no GitHub issue could be filed (shown in the email), if filing failed.</summary>
    public string? GitHubError { get; set; }

    /// <summary>
    /// Other GlitchTip errors of the same run that share this error's root cause.
    /// Only set on the primary outcome of a merge group.
    /// </summary>
    public List<TriageOutcome> MergedOutcomes { get; set; } = [];

    /// <summary>Set on secondary outcomes: the primary outcome they were merged into.</summary>
    public TriageOutcome? MergedInto { get; set; }

    /// <summary>AI explanation why the errors of a merge group share a root cause.</summary>
    public string? MergeReason { get; set; }

    /// <summary>
    /// Set when this error is already covered by an open GitHub issue or pull request — either via its
    /// glitchtip:{id} label or because the history scan matched it. No new issue is filed and no
    /// Copilot run is started in that case.
    /// </summary>
    public KnownDuplicate? DuplicateOf { get; set; }

    /// <summary>All GlitchTip error IDs represented by this outcome (itself + merged ones).</summary>
    public IEnumerable<string> AllErrorIds =>
        new[] { Candidate.ErrorId }.Concat(MergedOutcomes.Select(m => m.Candidate.ErrorId));
}
