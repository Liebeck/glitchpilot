using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;
using Octokit;

namespace GlitchPilot.Core.Services;

public sealed class IssueFiler : IIssueFiler
{
    private readonly GitHubClient _gh;
    private readonly HttpClient _http;
    private readonly ILogger<IssueFiler> _log;
    private readonly string _token;
    private readonly List<string> _labels;
    private readonly string? _agentLabel;
    private readonly string? _assignTo;
    private readonly string? _copilotModel;

    public IssueFiler(ILogger<IssueFiler> log)
    {
        _log = log;
        _token = EnvironmentConfig.GitHubToken;
        _labels = EnvironmentConfig.GitHubLabels;
        _agentLabel = EnvironmentConfig.GitHubAgentLabel;
        _assignTo = EnvironmentConfig.GitHubAssignTo;
        _copilotModel = EnvironmentConfig.CopilotModel;

        _gh = new GitHubClient(new Octokit.ProductHeaderValue("GlitchPilot"))
        {
            Credentials = new Credentials(_token),
        };

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlitchPilot", "1.0"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<int?> FileAsync(TriageOutcome outcome, CancellationToken ct = default)
    {
        var candidate = outcome.Candidate;
        var verdict = outcome.Verdict;
        var owner = candidate.GitHubOwner;
        var repo = candidate.GitHubRepo;

        // Duplicate guard — check for existing issue with the same GlitchTip label
        var refLabel = $"glitchtip:{candidate.ErrorId}";
        if (await FindExistingByLabelAsync(owner, repo, refLabel) is { } existingNumber)
        {
            _log.LogInformation("Error {Id} already tracked as #{Num}, skipping",
                candidate.ErrorId, existingNumber);
            return existingNumber;
        }

        var aiTitle = string.IsNullOrWhiteSpace(verdict.IssueTitle)
            ? candidate.ErrorTitle : verdict.IssueTitle;
        var title = FormatTitle(candidate.ErrorId, aiTitle);
        var body = ComposeBody(candidate, verdict);

        var request = new NewIssue(title) { Body = body };

        foreach (var lbl in _labels)
            request.Labels.Add(lbl);
        request.Labels.Add(refLabel);
        request.Labels.Add($"priority:{verdict.Priority}");
        request.Labels.Add($"type:{verdict.ErrorType}");
        if (_agentLabel is not null)
            request.Labels.Add(_agentLabel);
        if (_assignTo is not null)
            request.Assignees.Add(_assignTo);

        try
        {
            var created = await _gh.Issue.Create(owner, repo, request);
            _log.LogInformation("Filed #{Num}: {Title}", created.Number, created.Title);

            await AssignCopilotAsync(owner, repo, created.Number, ct);

            return created.Number;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to file issue for error {Id}", candidate.ErrorId);
            return null;
        }
    }

    private async Task AssignCopilotAsync(string owner, string repo, int issueNumber, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["assignees"] = new[] { "copilot-swe-agent[bot]" },
            };

            if (!string.IsNullOrWhiteSpace(_copilotModel))
            {
                payload["agent_assignment"] = new Dictionary<string, string>
                {
                    ["model"] = _copilotModel,
                };
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/assignees";
            var response = await _http.PostAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
                _log.LogInformation("Assigned Copilot to #{Num} (model: {Model})",
                    issueNumber, _copilotModel ?? "auto");
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Failed to assign Copilot to #{Num}: {Status} {Body}",
                    issueNumber, response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to assign Copilot to #{Num}", issueNumber);
        }
    }

    private async Task<int?> FindExistingByLabelAsync(string owner, string repo, string label)
    {
        try
        {
            var filter = new RepositoryIssueRequest
            {
                State = ItemStateFilter.Open,
            };
            filter.Labels.Add(label);

            var issues = await _gh.Issue.GetAllForRepository(owner, repo, filter);
            return issues.Count > 0 ? issues[0].Number : null;
        }
        catch
        {
            return null; // If lookup fails, allow creation
        }
    }

    private static string FormatTitle(string errorId, string title)
    {
        var prefix = $"[GlitchTip #{errorId}] ";
        var maxLen = 120 - prefix.Length;
        var trimmed = title.Length > maxLen ? title[..(maxLen - 3)] + "..." : title;
        return prefix + trimmed;
    }

    private static string ComposeBody(TriageCandidate c, Verdict v)
    {
        var sb = new System.Text.StringBuilder();

        // Problem
        var problem = string.IsNullOrWhiteSpace(v.ProblemDescription)
            ? v.Explanation : v.ProblemDescription;
        sb.AppendLine("## Problem");
        sb.AppendLine(problem);
        sb.AppendLine();

        // Acceptance Criteria
        if (v.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in v.AcceptanceCriteria)
                sb.AppendLine($"- [ ] {criterion}");
            sb.AppendLine();
        }

        // Files to Investigate
        if (v.FilesToInvestigate.Count > 0)
        {
            sb.AppendLine("## Files to Investigate");
            foreach (var file in v.FilesToInvestigate)
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }

        // Technical Context
        sb.AppendLine("## Technical Context");
        sb.AppendLine();

        // Stack Trace
        if (c.StackTrace is not null)
        {
            sb.AppendLine("### Stack Trace");
            sb.AppendLine("```");
            sb.AppendLine(c.StackTrace);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Breadcrumbs
        if (c.Breadcrumbs.Count > 0)
        {
            sb.AppendLine("### Breadcrumbs");
            sb.AppendLine("| Timestamp | Category | Message |");
            sb.AppendLine("|-----------|----------|---------|");
            foreach (var b in c.Breadcrumbs)
                sb.AppendLine($"| {b.Timestamp ?? "—"} | {b.Category ?? "—"} | {b.Message ?? "—"} |");
            sb.AppendLine();
        }

        // Error Details
        sb.AppendLine("### Error Details");
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| **Priority** | {v.Priority} |");
        sb.AppendLine($"| **Type** | {v.ErrorType} |");
        sb.AppendLine($"| **Severity** | {c.SeverityLevel} |");
        sb.AppendLine($"| **Occurrences** | {c.Occurrences} |");
        sb.AppendLine($"| **First seen** | {c.DetectedAt ?? "—"} |");
        sb.AppendLine($"| **Last seen** | {c.LastOccurredAt ?? "—"} |");
        sb.AppendLine($"| **Environment** | {c.Environment ?? "—"} |");
        if (c.HttpEndpoint is not null)
            sb.AppendLine($"| **Endpoint** | {c.HttpEndpoint} |");
        if (c.AppRelease is not null)
            sb.AppendLine($"| **Release** | {c.AppRelease} |");
        if (c.ExceptionHandled is not null)
            sb.AppendLine($"| **Exception handled** | {c.ExceptionHandled} |");
        if (c.ServerOs is not null)
            sb.AppendLine($"| **Server OS** | {c.ServerOs} |");
        sb.AppendLine();

        // Tags
        if (c.Tags.Count > 0)
        {
            sb.AppendLine("### Tags");
            sb.AppendLine("| Key | Value |");
            sb.AppendLine("|-----|-------|");
            foreach (var (key, value) in c.Tags)
                sb.AppendLine($"| {key} | {value} |");
            sb.AppendLine();
        }

        // Recommendation
        var recommendation = string.IsNullOrWhiteSpace(v.Recommendation)
            ? v.RecommendedAction : v.Recommendation;
        if (!string.IsNullOrWhiteSpace(recommendation))
        {
            sb.AppendLine("## Recommendation");
            sb.AppendLine(recommendation);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"[Open in GlitchTip]({c.GlitchTipLink})");
        sb.AppendLine($"*Filed by GlitchPilot at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC*");

        // Guard against GitHub's 65535 char body limit
        var body = sb.ToString();
        if (body.Length > 60000)
            body = body[..60000] + "\n\n... (truncated)";

        return body;
    }
}
