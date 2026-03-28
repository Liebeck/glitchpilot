namespace GlitchPilot.Core.Models;

/// <summary>The result of processing a single error through the triage pipeline.</summary>
public class TriageOutcome
{
    public TriageCandidate Candidate { get; set; } = null!;
    public Verdict Verdict { get; set; } = null!;
    public int? GitHubIssueNumber { get; set; }
    public string? GitHubIssueUrl { get; set; }
}
