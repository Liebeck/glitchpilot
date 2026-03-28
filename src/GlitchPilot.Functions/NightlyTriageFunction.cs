using GlitchPilot.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Functions;

public class NightlyTriageFunction
{
    private readonly TriagePipeline _pipeline;
    private readonly ILogger<NightlyTriageFunction> _log;

    public NightlyTriageFunction(TriagePipeline pipeline, ILogger<NightlyTriageFunction> log)
    {
        _pipeline = pipeline;
        _log = log;
    }

    [Function("NightlyTriage")]
    public async Task Run([TimerTrigger("%TRIAGE_SCHEDULE%")] TimerInfo timer)
    {
        _log.LogInformation("NightlyTriage triggered at {Time}", DateTimeOffset.Now);
        await _pipeline.RunAsync();
        _log.LogInformation("NightlyTriage completed");
    }
}
