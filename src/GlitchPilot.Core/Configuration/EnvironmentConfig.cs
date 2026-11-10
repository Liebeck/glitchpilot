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

    // ── Duplicate handling ─────────────────────────────────
    /// <summary>Merge errors of the same run that share a root cause (AI-based). Default: on.</summary>
    public static bool MergeDuplicatesEnabled => ReadBool("MERGE_DUPLICATES_ENABLED", true);
    /// <summary>Compare new errors with open GitHub issues / PRs before filing (AI-based). Default: off.</summary>
    public static bool DuplicateScanEnabled  => ReadBool("DUPLICATE_SCAN_ENABLED", false);
    /// <summary>Maximum number of open issues per repository considered by the history scan.</summary>
    public static int DuplicateScanMaxIssues => ReadPositiveInt("DUPLICATE_SCAN_MAX_ISSUES", 50);

    // ── General ────────────────────────────────────────────
    public static int LookbackHours         => int.Parse(Read("LOOKBACK_HOURS") ?? "24");

    // ── Helpers ────────────────────────────────────────────

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name);

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");

    private static int ReadPositiveInt(string name, int fallback)
    {
        var raw = Read(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw.Trim(), out var value) && value > 0
            ? value
            : throw new InvalidOperationException(
                $"Environment variable '{name}' has invalid value '{raw}'. Use a positive number.");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = Read(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"Environment variable '{name}' has invalid value '{raw}'. Use true/false."),
        };
    }

    private static List<string> ReadList(string name, string? fallback = null)
    {
        var raw = Read(name) ?? fallback ?? "";
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
