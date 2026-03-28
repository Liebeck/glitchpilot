using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

public interface IIssueFiler
{
    Task<int?> FileAsync(TriageOutcome outcome, CancellationToken ct = default);
}
