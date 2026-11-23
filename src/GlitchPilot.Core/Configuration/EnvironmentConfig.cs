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

    // ── Manual filing (button in the email) ────────────────
    /// <summary>
    /// Settings for the "Create GitHub issue" button on dismissed transient errors, or null when
    /// FILE_ISSUE_URL is not set (no button). Throws if the URL is set but the signing key is missing or weak.
    /// </summary>
    public static ManualFilingSettings? ManualFiling
    {
        get
        {
            var url = Read("FILE_ISSUE_URL");
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new InvalidOperationException(
                    $"Environment variable 'FILE_ISSUE_URL' has invalid value '{url}'. Use an absolute https URL.");

            var key = Read("LINK_SIGNING_KEY");
            if (string.IsNullOrWhiteSpace(key) || key.Trim().Length < 32)
                throw new InvalidOperationException(
                    "Environment variable 'LINK_SIGNING_KEY' must be set to at least 32 characters when FILE_ISSUE_URL is set.");

            return new ManualFilingSettings(url.Trim(), key.Trim(), ReadPositiveInt("LINK_EXPIRY_DAYS", 14));
        }
    }

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

/// <summary>Endpoint, HMAC key and link lifetime for the manual "Create GitHub issue" button.</summary>
public sealed record ManualFilingSettings(string Endpoint, string SigningKey, int ExpiryDays);
