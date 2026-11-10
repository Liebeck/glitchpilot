namespace GlitchPilot.Core.Models;

/// <summary>An open GitHub issue / pull request that a new error was matched to.</summary>
public class KnownDuplicate
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsPullRequest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<LinkedPullRequest> PullRequests { get; set; } = [];

    /// <summary>
    /// True when linked pull requests were looked up (history scan). The label check alone
    /// does not query pull requests, so an empty list then means "unknown", not "none".
    /// </summary>
    public bool PullRequestsKnown { get; set; }
    public string Reason { get; set; } = "";
}
