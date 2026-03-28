namespace GlitchPilot.Core.Models;

/// <summary>
/// A fully enriched error ready for classification.
/// Combines data from the error group + its latest occurrence.
/// </summary>
public class TriageCandidate
{
    public string ProjectSlug { get; set; } = "";
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string ErrorId { get; set; } = "";
    public string ErrorTitle { get; set; } = "";
    public string? SourceLocation { get; set; }
    public string? FunctionName { get; set; }
    public string? HttpEndpoint { get; set; }
    public string SeverityLevel { get; set; } = "error";
    public int Occurrences { get; set; }
    public string? DetectedAt { get; set; }
    public string? LastOccurredAt { get; set; }
    public string? Environment { get; set; }
    public string GlitchTipLink { get; set; } = "";
    public string? StackTrace { get; set; }
    public List<Breadcrumb> Breadcrumbs { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = [];
    public string? UserId { get; set; }
    public string? RequestHeaders { get; set; }
    public string? ServerOs { get; set; }
    public string? AppRelease { get; set; }
    public bool? ExceptionHandled { get; set; }
}

public class Breadcrumb
{
    public string? Timestamp { get; set; }
    public string? Category { get; set; }
    public string? Level { get; set; }
    public string? Message { get; set; }
}
