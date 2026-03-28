namespace GlitchPilot.Core.Models;

/// <summary>AI classification output for an error.</summary>
public class Verdict
{
    public bool RequiresAction { get; set; }
    public string Priority { get; set; } = "low";        // critical, high, medium, low
    public string ErrorType { get; set; } = "unknown";    // bug, infra, config, transient, dependency
    public string Explanation { get; set; } = "";
    public string RecommendedAction { get; set; } = "";

    // GitHub issue fields (richer output for Copilot)
    public string IssueTitle { get; set; } = "";
    public string ProblemDescription { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public List<string> FilesToInvestigate { get; set; } = [];
    public string Recommendation { get; set; } = "";

    // Email notification
    public string EmailSummary { get; set; } = "";
}
