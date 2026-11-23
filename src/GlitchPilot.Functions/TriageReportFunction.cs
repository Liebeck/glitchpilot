using GlitchPilot.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Functions;

/// <summary>
/// Every 15 minutes: sends the triage emails of pending runs once the Copilot sessions finished (max REPORT_MAX_DELAY_MINUTES),
/// including token / AI-credit usage, and stores the run JSON in Blob Storage.
/// </summary>
public class TriageReportFunction
{
    private readonly ReportPipeline _pipeline;
    private readonly ILogger<TriageReportFunction> _log;

    public TriageReportFunction(ReportPipeline pipeline, ILogger<TriageReportFunction> log)
    {
        _pipeline = pipeline;
        _log = log;
    }

    [Function("TriageReport")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    {
        _log.LogInformation("TriageReport triggered at {Time}", DateTimeOffset.Now);
        await _pipeline.RunAsync();
    }
}
