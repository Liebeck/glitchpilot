using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Services;

public interface ICopilotUsageClient
{
    /// <summary>Finds the Copilot PR(s) linked to the issue and the agent workflow runs (sessions) on their branches.</summary>
    Task<CopilotIssueUsage> GetIssueUsageAsync(string owner, string repo, int issueNumber, CancellationToken ct = default);

    /// <summary>AI-credit (or legacy premium-request) usage of a UTC day from the GitHub billing API.</summary>
    Task<CopilotBillingDay> GetBillingDayAsync(DateOnly day, CancellationToken ct = default);
}

/// <summary>
/// Reads Copilot cloud agent activity and billing from the GitHub REST API. GitHub reports cost only per
/// day / model / SKU — not per session — so per-issue cost is estimated from the measured session minutes.
/// </summary>
public sealed class CopilotUsageClient : ICopilotUsageClient
{
    private const string BillingApiVersion = "2026-03-10";

    private readonly HttpClient _http;
    private readonly ILogger<CopilotUsageClient> _log;
    private string? _billingUser = EnvironmentConfig.GitHubBillingUser;

    public CopilotUsageClient(ILogger<CopilotUsageClient> log)
    {
        _log = log;
        _http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EnvironmentConfig.GitHubToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlitchPilot", "1.0"));
    }

    // ── Sessions per issue ─────────────────────────────────

    public async Task<CopilotIssueUsage> GetIssueUsageAsync(string owner, string repo, int issueNumber, CancellationToken ct = default)
    {
        var usage = new CopilotIssueUsage();
        try
        {
            using var timeline = await GetJsonAsync($"repos/{owner}/{repo}/issues/{issueNumber}/timeline?per_page=100", ct: ct);
            var prNumbers = LinkedPullRequests(timeline.RootElement, $"{owner}/{repo}");

            foreach (var number in prNumbers)
            {
                using var pr = await GetJsonAsync($"repos/{owner}/{repo}/pulls/{number}", ct: ct);
                var p = pr.RootElement;
                var branch = Str(p, "head", "ref") ?? "";
                usage.PullRequests.Add(new CopilotPullRequest
                {
                    Number = number,
                    Url = Str(p, "html_url") ?? "",
                    State = Str(p, "state") ?? "",
                    Draft = p.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
                    Merged = p.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True,
                    HeadBranch = branch,
                    Author = Str(p, "user", "login"),
                });

                if (branch.Length == 0) continue;
                using var runs = await GetJsonAsync(
                    $"repos/{owner}/{repo}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=100", ct: ct);
                usage.Sessions.AddRange(AgentSessions(runs.RootElement));
            }

            usage.Status = SessionStatus(usage);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not read Copilot activity for {Owner}/{Repo}#{Num}", owner, repo, issueNumber);
            usage.Status = "error";
            usage.Error = ex.Message;
        }
        return usage;
    }

    /// <summary>pending: no PR / session yet · running: a session is not completed · completed: all sessions done.</summary>
    internal static string SessionStatus(CopilotIssueUsage usage) =>
        usage.Sessions.Count == 0 ? "pending"
        : usage.Sessions.Any(s => s.Status != "completed") ? "running"
        : "completed";

    /// <summary>PRs of the same repository that cross-reference the issue; Copilot-authored PRs first.</summary>
    internal static List<int> LinkedPullRequests(JsonElement timeline, string fullRepoName)
    {
        var found = new List<(int Number, bool ByCopilot)>();
        if (timeline.ValueKind != JsonValueKind.Array) return [];

        foreach (var ev in timeline.EnumerateArray())
        {
            if (Str(ev, "event") != "cross-referenced") continue;
            if (!ev.TryGetProperty("source", out var source) || !source.TryGetProperty("issue", out var issue)) continue;
            if (!issue.TryGetProperty("pull_request", out _)) continue;
            var repoName = Str(issue, "repository", "full_name");
            if (repoName is not null && !repoName.Equals(fullRepoName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!issue.TryGetProperty("number", out var n) || !n.TryGetInt32(out var number)) continue;

            var author = Str(issue, "user", "login") ?? "";
            found.Add((number, author.Contains("copilot", StringComparison.OrdinalIgnoreCase)));
        }

        // If Copilot opened a PR, ignore PRs of humans that merely mention the issue
        var relevant = found.Any(f => f.ByCopilot) ? found.Where(f => f.ByCopilot) : found;
        return relevant.Select(f => f.Number).Distinct().ToList();
    }

    /// <summary>Workflow runs of the Copilot agent itself (not the repository's CI that runs on its branch).</summary>
    internal static List<CopilotSession> AgentSessions(JsonElement runsResponse)
    {
        var sessions = new List<CopilotSession>();
        if (!runsResponse.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return sessions;

        foreach (var run in runs.EnumerateArray())
        {
            var name = Str(run, "name") ?? "";
            var path = Str(run, "path") ?? "";
            var isAgent = path.Contains("copilot", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("copilot", StringComparison.OrdinalIgnoreCase);
            if (!isAgent) continue;

            var started = Date(run, "run_started_at") ?? Date(run, "created_at");
            var updated = Date(run, "updated_at");
            var status = Str(run, "status") ?? "";
            sessions.Add(new CopilotSession
            {
                WorkflowRunId = run.TryGetProperty("id", out var id) && id.TryGetInt64(out var v) ? v : 0,
                Name = name,
                Status = status,
                Conclusion = Str(run, "conclusion"),
                StartedAt = started,
                UpdatedAt = updated,
                DurationMinutes = status == "completed" && started is { } s && updated is { } u && u >= s
                    ? Math.Round((u - s).TotalMinutes, 2) : null,
                Url = Str(run, "html_url") ?? "",
            });
        }
        return sessions.OrderBy(s => s.StartedAt).ToList();
    }

    // ── Billing per day ────────────────────────────────────

    public async Task<CopilotBillingDay> GetBillingDayAsync(DateOnly day, CancellationToken ct = default)
    {
        var result = new CopilotBillingDay { Day = day, FetchedAt = DateTimeOffset.UtcNow };
        try
        {
            var user = await BillingUserAsync(ct);
            var query = $"year={day.Year}&month={day.Month}&day={day.Day}";

            // AI credits (usage-based billing since June 2026); legacy plans still report premium requests
            foreach (var source in new[] { "ai_credit", "premium_request" })
            {
                var url = $"users/{Uri.EscapeDataString(user)}/settings/billing/{source}/usage?{query}";
                using var response = await SendAsync(url, BillingApiVersion, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                await EnsureSuccessAsync(response, ct);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var items = ParseBillingItems(doc.RootElement);
                if (items.Count == 0 && source == "ai_credit") { result.Source = source; continue; }

                result.Source = source;
                result.Items = items;
                return result;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not read GitHub billing usage for {Day}", day);
            result.Error = ex.Message;
        }
        return result;
    }

    internal static List<BillingUsageItem> ParseBillingItems(JsonElement root)
    {
        var items = new List<BillingUsageItem>();
        if (!root.TryGetProperty("usageItems", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var i in arr.EnumerateArray())
        {
            items.Add(new BillingUsageItem
            {
                Product = Str(i, "product") ?? "",
                Sku = Str(i, "sku") ?? "",
                Model = Str(i, "model"),
                UnitType = Str(i, "unitType") ?? "",
                PricePerUnit = Dec(i, "pricePerUnit"),
                GrossQuantity = Dec(i, "grossQuantity"),
                GrossAmount = Dec(i, "grossAmount"),
                DiscountQuantity = Dec(i, "discountQuantity"),
                DiscountAmount = Dec(i, "discountAmount"),
                NetQuantity = Dec(i, "netQuantity"),
                NetAmount = Dec(i, "netAmount"),
            });
        }
        return items;
    }

    private async Task<string> BillingUserAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_billingUser)) return _billingUser;
        using var me = await GetJsonAsync("user", ct: ct);
        _billingUser = Str(me.RootElement, "login")
            ?? throw new InvalidOperationException("Could not determine the GitHub user of GITHUB_TOKEN — set GITHUB_BILLING_USER.");
        return _billingUser;
    }

    // ── HTTP / JSON helpers ────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(string url, string apiVersion, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-GitHub-Api-Version", apiVersion);
        return await _http.SendAsync(request, ct);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string apiVersion = "2022-11-28", CancellationToken ct = default)
    {
        using var response = await SendAsync(url, apiVersion, ct);
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 300) body = body[..300];
        throw new HttpRequestException(
            $"GitHub API {(int)response.StatusCode} for {response.RequestMessage?.RequestUri?.PathAndQuery}: {body}");
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static decimal Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
}
