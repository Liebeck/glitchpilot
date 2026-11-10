namespace GlitchPilot.Core.Models;

/// <summary>An open GitHub issue or pull request used for the history duplicate scan.</summary>
public class ExistingWorkItem
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsPullRequest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Condensed body (problem statement, files, top stack frames) for the AI comparison.</summary>
    public string Excerpt { get; set; } = "";

    /// <summary>Open pull requests that reference this issue (e.g. Copilot "Fixes #N").</summary>
    public List<LinkedPullRequest> PullRequests { get; set; } = [];
}

public class LinkedPullRequest
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Draft { get; set; }
}
