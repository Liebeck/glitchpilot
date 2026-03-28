namespace GlitchPilot.Core.Configuration;

/// <summary>
/// Reads all configuration from environment variables.
/// Call Require() for mandatory values, Read() for optional ones.
/// </summary>
public static class EnvironmentConfig
{
    // ── GlitchTip ──────────────────────────────────────────
    public static string GlitchTipBaseUrl   => Require("GLITCHTIP_URL");
    public static string GlitchTipApiToken  => Require("GLITCHTIP_TOKEN");
    public static string GlitchTipOrg       => Require("GLITCHTIP_ORG");

    // ── Azure OpenAI ───────────────────────────────────────
    public static string OpenAiEndpoint     => Require("OPENAI_ENDPOINT");
    public static string OpenAiApiKey       => Require("OPENAI_KEY");
    public static string OpenAiModel        => Read("OPENAI_MODEL") ?? "gpt-4o";

    // ── GitHub ─────────────────────────────────────────────
    public static string GitHubToken        => Require("GITHUB_TOKEN");
    public static List<string> GitHubLabels => ReadList("GITHUB_LABELS", "glitchpilot");
    public static string? GitHubAgentLabel  => Read("GITHUB_AGENT_LABEL");
    public static string? GitHubAssignTo    => Read("GITHUB_ASSIGN_TO");
    public static string? CopilotModel      => Read("GITHUB_COPILOT_MODEL");

    // ── SMTP / Mail ────────────────────────────────────────
    public static string SmtpHost           => Require("SMTP_HOST");
    public static int    SmtpPort           => int.Parse(Read("SMTP_PORT") ?? "587");
    public static string SmtpUsername       => Require("SMTP_USER");
    public static string SmtpPassword       => Require("SMTP_PASS");
    public static string MailFrom           => Require("MAIL_FROM");
    public static List<string> MailRecipients =>
        ReadList("MAIL_RECIPIENTS");

    // ── Projects (JSON array) ──────────────────────────────
    public static string ProjectsJson       => Require("PROJECTS");

    // ── Filters ────────────────────────────────────────────
    public static int MinOccurrences        => int.Parse(Read("MIN_OCCURRENCES") ?? "1");
    public static List<string> ExcludeTitlePatterns => ReadList("EXCLUDE_TITLES");
    public static HashSet<string> ExcludeIds => ReadList("EXCLUDE_IDS").ToHashSet();

    // ── General ────────────────────────────────────────────
    public static int LookbackHours         => int.Parse(Read("LOOKBACK_HOURS") ?? "24");

    // ── Helpers ────────────────────────────────────────────

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name);

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");

    private static List<string> ReadList(string name, string? fallback = null)
    {
        var raw = Read(name) ?? fallback ?? "";
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
