using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

public interface IGlitchTipClient
{
    Task<List<ProjectSummary>> ListProjectsAsync(CancellationToken ct = default);

    Task<List<TriageCandidate>> CollectCandidatesAsync(
        string projectSlug,
        string targetEnvironment,
        DateTimeOffset? since = null,
        CancellationToken ct = default);

    Task<TriageCandidate?> GetIssueAsync(string issueId, CancellationToken ct = default);
}
