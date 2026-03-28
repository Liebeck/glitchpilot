using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace GlitchPilot.Core.Services;

public sealed class ErrorClassifier : IErrorClassifier
{
    private readonly ChatClient _chat;
    private readonly ILogger<ErrorClassifier> _log;

    private static readonly string s_systemInstruction = """
        You are an automated error-triage system. Given an application error, produce a JSON verdict.
        The verdict will be used to create a GitHub issue that a coding agent (GitHub Copilot) will work on.

        IMPORTANT — The consumer of this verdict is GitHub Copilot coding agent. It can ONLY:
        - Modify source code files in the repository
        - Add/change error handling, retry logic, validation, null checks, fallbacks
        - Add or modify unit/integration tests
        - Update configuration files that are in source control (appsettings.json, etc.)

        It CANNOT:
        - Access infrastructure, servers, databases, or cloud dashboards
        - Review logs, metrics, or monitoring systems
        - Change network settings, firewall rules, or DNS
        - Modify CI/CD pipelines or deployment configurations
        - Perform manual testing or observe production behavior over time

        All recommendations and acceptance criteria MUST be achievable through source code changes alone.

        Output ONLY a single JSON object (no markdown, no explanation, no extra text). Keep the total response under 800 tokens.
        Schema:
        {
          "requiresAction": boolean,
          "priority": "critical" | "high" | "medium" | "low",
          "errorType": "bug" | "infra" | "config" | "transient" | "dependency" | "unknown",
          "explanation": "concise one-liner for email summaries",
          "recommendedAction": "brief action for email summaries",
          "issueTitle": "clear, specific title for a GitHub issue (max 80 chars, no brackets or ID prefix)",
          "problemDescription": "2-4 sentence description of what is broken and why, written as a problem statement for a developer who has never seen this codebase. Mention the user impact, frequency, and severity.",
          "acceptanceCriteria": ["criterion 1", "criterion 2", ...],
          "filesToInvestigate": ["path/to/file.cs:42", ...],
          "recommendation": "detailed paragraph on how to fix this, including specific code changes if possible",
          "emailSummary": "exactly 3 sentences for a notification email: what broke, who is affected, and what should be done"
        }

        Decision rules:
        • requiresAction = true → a developer should investigate. false → noise, self-healing, or expected.
        • priority: critical = outage/data-loss, high = broken feature, medium = degraded, low = cosmetic/rare.
        • errorType: bug = logic flaw, infra = host/network, config = misconfiguration, transient = temporary, dependency = 3rd-party.

        Transient error detection:
        • Database connection errors, timeouts, HTTP 5xx from external services, socket exceptions,
          and DNS resolution failures are almost always transient — classify as "transient".
        • If errorType = "transient" AND occurrences < 5: set requiresAction = false
          (these are self-healing; filing an issue would create noise).
        • If errorType = "transient" AND occurrences >= 5: set requiresAction = true,
          but the recommendation should focus on adding retry/resilience code (Polly,
          connection pool tuning in code, circuit breaker pattern), NOT investigating infrastructure.

        Guidelines for GitHub issue fields:
        • issueTitle: Be specific. "StorageException in GetUserPicture when blob is missing" not "StorageException".
        • acceptanceCriteria: 2-5 criteria that can be verified by reviewing the code changes in a pull request.
          Do NOT suggest creating new tests — focus on the fix itself.
          Good: "The endpoint returns 404 instead of 500 when the blob does not exist."
          Good: "A retry policy with exponential backoff is configured for database connections."
          Good: "The controller catches SqlException and returns 503 with a Retry-After header."
          Bad: "The database connection is stable for 7 days." (not code-verifiable)
          Bad: "No errors appear in production logs." (Copilot can't check logs)
          Bad: "A unit test covers the null-reference scenario." (do not suggest new tests)
        • filesToInvestigate: Only include in-app source files from the stack trace (not framework/library frames). Include line numbers if available.
        • recommendation: Describe specific SOURCE CODE changes. Reference the files from the stack trace.
          Good: "Add a try-catch around the DB call in GameController.cs and return a 503 with a retry-after header. Configure a Polly retry policy in Startup.cs."
          Bad: "Review database server logs and network configuration." (not a code change)
        """;

    public ErrorClassifier(ILogger<ErrorClassifier> log)
    {
        _log = log;
        var aoai = new AzureOpenAIClient(
            new Uri(EnvironmentConfig.OpenAiEndpoint),
            new AzureKeyCredential(EnvironmentConfig.OpenAiApiKey));
        _chat = aoai.GetChatClient(EnvironmentConfig.OpenAiModel);
    }

    public async Task<Verdict> EvaluateAsync(TriageCandidate candidate, CancellationToken ct = default)
    {
        var lines = new List<string>
        {
            $"Title: {candidate.ErrorTitle}",
            $"Source: {candidate.SourceLocation ?? "—"}",
            $"Function: {candidate.FunctionName ?? "—"}",
            $"Severity: {candidate.SeverityLevel}",
            $"Occurrences: {candidate.Occurrences}",
            $"Endpoint: {candidate.HttpEndpoint ?? "—"}",
            $"First seen: {candidate.DetectedAt ?? "—"}",
            $"Last seen: {candidate.LastOccurredAt ?? "—"}",
            $"Exception handled: {candidate.ExceptionHandled?.ToString() ?? "—"}",
            $"Release: {candidate.AppRelease ?? "—"}",
        };

        if (candidate.StackTrace is not null)
        {
            lines.Add("");
            lines.Add("Stack trace:");
            lines.Add(Truncate(candidate.StackTrace, 3000));
        }

        if (candidate.Breadcrumbs.Count > 0)
        {
            lines.Add("");
            lines.Add("Breadcrumbs:");
            foreach (var b in candidate.Breadcrumbs.TakeLast(15))
                lines.Add($"  [{b.Timestamp ?? "?"}] {b.Category ?? "?"}: {b.Message ?? ""}");
        }

        var prompt = string.Join('\n', lines);

        try
        {
            var completion = await _chat.CompleteChatAsync(
                [new SystemChatMessage(s_systemInstruction), new UserChatMessage(prompt)],
                cancellationToken: ct);

            var json = completion.Value.Content[0].Text;

            return JsonSerializer.Deserialize<Verdict>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? FallbackVerdict("Unparseable AI response");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Classification failed for error {ErrorId}", candidate.ErrorId);
            return FallbackVerdict(ex.Message);
        }
    }

    private static Verdict FallbackVerdict(string reason) => new()
    {
        RequiresAction = false,
        Explanation = $"Classification unavailable: {reason}",
        IssueTitle = "",
        ProblemDescription = "",
        Recommendation = "",
        EmailSummary = "",
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n... (truncated)";
}
