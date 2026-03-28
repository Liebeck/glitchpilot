using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Pipelines;

public sealed class VerifyPipeline
{
    private readonly IGlitchTipClient _client;
    private readonly ILogger<VerifyPipeline> _log;

    public VerifyPipeline(IGlitchTipClient client, ILogger<VerifyPipeline> log)
    {
        _client = client;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Verifying GlitchTip connectivity...");

        try
        {
            var projects = await _client.ListProjectsAsync(ct);

            if (projects.Count == 0)
            {
                _log.LogWarning("Connected successfully but no projects found for this organization");
                return;
            }

            _log.LogInformation("Connected — found {Count} project(s):", projects.Count);
            foreach (var project in projects)
                _log.LogInformation("  • {Slug} ({Name})", project.Slug, project.Name);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Failed to connect to GlitchTip — check GLITCHTIP_URL and GLITCHTIP_TOKEN");
        }
    }
}
