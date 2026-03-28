namespace GlitchPilot.Core.Models;

public class MonitoredProject
{
    public string Slug { get; set; } = "";
    public string TargetEnvironment { get; set; } = "";
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
}
