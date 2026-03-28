using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

public interface IErrorClassifier
{
    Task<Verdict> EvaluateAsync(TriageCandidate candidate, CancellationToken ct = default);
}
