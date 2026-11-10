using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;
using Octokit;

namespace GlitchPilot.Core.Services;

public sealed partial class IssueFiler : IIssueFiler
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
        var owner = candidate.GitHubOwner;
        var repo = candidate.GitHubRepo;

        // Duplicate guard — check for an open issue/PR carrying any of the GlitchTip labels
        IReadOnlyDictionary<string, ExistingWorkItem> tracked;
        try
        {
            tracked = await GetTrackedErrorsAsync(owner, repo, outcome.AllErrorIds, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Duplicate lookup failed for error {Id} — not filing to avoid duplicates", candidate.ErrorId);
            return null;
        }

        if (TrackingLabels.Find(tracked, outcome.AllErrorIds) is { } existing)
        {
            _log.LogInformation("Error {Id} already tracked as #{Num}, skipping",
                candidate.ErrorId, existing.Number);
            await AddTrackingLabelsAsync(owner, repo, existing.Number,
                TrackingLabels.Missing(tracked, existing.Number, outcome.AllErrorIds), ct);
            return existing.Number;
        }

        return await CreateAsync(outcome, ct);
    }

    public async Task<int?> CreateAsync(TriageOutcome outcome, CancellationToken ct = default)
    {
        var candidate = outcome.Candidate;
        var verdict = outcome.Verdict;
        var owner = candidate.GitHubOwner;
        var repo = candidate.GitHubRepo;

        var aiTitle = string.IsNullOrWhiteSpace(verdict.IssueTitle)
            ? candidate.ErrorTitle : verdict.IssueTitle;
        var title = FormatTitle(candidate.ErrorId, aiTitle);
        var body = ComposeBody(outcome);

        var request = new NewIssue(title) { Body = body };

        foreach (var lbl in _labels)
            request.Labels.Add(lbl);
        foreach (var lbl in outcome.AllErrorIds.Select(TrackingLabels.For).Distinct())
            request.Labels.Add(lbl);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
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

    public async Task<IReadOnlyDictionary<string, ExistingWorkItem>> GetTrackedErrorsAsync(
        string owner, string repo, IEnumerable<string> errorIds, CancellationToken ct = default)
    {
        // Searches open issues AND pull requests for exactly the glitchtip:{id} labels of this run
        // (comma = OR in GitHub search). Independent of other labels, so manually relabelled issues
        // are still found. Failures propagate — callers must not treat "lookup failed" as "not tracked".
        var tracked = new Dictionary<string, ExistingWorkItem>();
        var wanted = errorIds.Distinct().ToHashSet();

        // The search API rejects queries without is:issue / is:pull-request (HTTP 422) — query both
        var results = new List<(Issue Issue, bool IsPullRequest)>();
        foreach (var term in BuildLabelSearchTerms(wanted, owner, repo))
        {
            foreach (var type in new[] { IssueTypeQualifier.Issue, IssueTypeQualifier.PullRequest })
            {
                ct.ThrowIfCancellationRequested();
                var request = new SearchIssuesRequest(term)
                {
                    Repos = new RepositoryCollection { { owner, repo } },
                    State = ItemState.Open,
                    Type = type,
                    PerPage = 100,
                };
                var found = await _gh.Search.SearchIssues(request);
                results.AddRange(found.Items.Select(i => (i, type == IssueTypeQualifier.PullRequest)));
            }
        }

        foreach (var (issue, isPullRequest) in results.OrderBy(r => r.Issue.Number))
        {
            ExistingWorkItem? item = null;
            foreach (var label in issue.Labels)
            {
                if (!label.Name.StartsWith(TrackingLabels.Prefix, StringComparison.Ordinal)) continue;
                var id = label.Name[TrackingLabels.Prefix.Length..];
                if (!wanted.Contains(id)) continue;
                item ??= new ExistingWorkItem
                {
                    Number = issue.Number,
                    Title = issue.Title,
                    Url = issue.HtmlUrl,
                    IsPullRequest = isPullRequest,
                    CreatedAt = issue.CreatedAt,
                };
                tracked.TryAdd(id, item);
            }
        }

        return tracked;
    }

    /// <summary>
    /// Builds search terms like <c>label:"glitchtip:1","glitchtip:2"</c>, split so each full query
    /// (term + repo/state qualifiers) stays below GitHub's 256 character limit.
    /// </summary>
    internal static List<string> BuildLabelSearchTerms(IEnumerable<string> errorIds, string owner, string repo)
    {
        const int maxQueryLength = 256;
        var qualifiers = $" repo:{owner}/{repo} state:open is:pull-request".Length;
        var terms = new List<string>();
        var current = new List<string>();

        string Term(IEnumerable<string> labels) => "label:" + string.Join(',', labels.Select(l => $"\"{l}\""));

        foreach (var id in errorIds.Distinct())
        {
            var label = TrackingLabels.For(id);
            if (current.Count > 0 && Term(current.Append(label)).Length + qualifiers > maxQueryLength)
            {
                terms.Add(Term(current));
                current.Clear();
            }
            current.Add(label);
        }
        if (current.Count > 0)
            terms.Add(Term(current));
        return terms;
    }

    public Task AddTrackingLabelsAsync(string owner, string repo, int number, IEnumerable<string> errorIds,
        CancellationToken ct = default) =>
        AddLabelsAsync(owner, repo, number, errorIds.Select(TrackingLabels.For).Distinct().ToList(), ct);

    public async Task<IReadOnlyList<ExistingWorkItem>> ListOpenWorkAsync(
        string owner, string repo, int maxItems, CancellationToken ct = default)
    {
        // Open issues filed by GlitchPilot (identified by the first base label)
        var filter = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open,
            SortProperty = IssueSort.Created,
            SortDirection = SortDirection.Descending,
        };
        if (_labels.Count > 0)
            filter.Labels.Add(_labels[0]);

        var pageSize = Math.Clamp(maxItems, 1, 100);
        var issueOptions = new ApiOptions
        {
            PageSize = pageSize,
            PageCount = (int)Math.Ceiling(maxItems / (double)pageSize),
            StartPage = 1,
        };

        var issues = (await _gh.Issue.GetAllForRepository(owner, repo, filter, issueOptions))
            .Where(i => i.PullRequest is null)
            .Take(maxItems)
            .ToList();

        ct.ThrowIfCancellationRequested();

        var prs = await _gh.PullRequest.GetAllForRepository(owner, repo,
            new PullRequestRequest { State = ItemStateFilter.Open },
            new ApiOptions { PageSize = 100, PageCount = 1, StartPage = 1 });

        var items = issues.Select(i => new ExistingWorkItem
        {
            Number = i.Number,
            Title = i.Title,
            Url = i.HtmlUrl,
            CreatedAt = i.CreatedAt,
            Excerpt = WorkItemText.Excerpt(i.Body),
        }).ToList();

        var byNumber = items.ToDictionary(i => i.Number);

        foreach (var pr in prs)
        {
            var linked = new LinkedPullRequest
            {
                Number = pr.Number,
                Url = pr.HtmlUrl,
                Title = pr.Title,
                Draft = pr.Draft,
            };

            var refs = WorkItemText.ReferencedIssues(pr.Body, byNumber.Keys);
            foreach (var n in refs)
                byNumber[n].PullRequests.Add(linked);

            // PRs without an open GlitchPilot issue (e.g. issue already closed) are candidates themselves,
            // as long as the overall limit (prompt size) is not reached
            if (refs.Count == 0 && items.Count < maxItems && WorkItemText.MentionsGlitchTip(pr.Title, pr.Body))
                items.Add(new ExistingWorkItem
                {
                    Number = pr.Number,
                    Title = pr.Title,
                    Url = pr.HtmlUrl,
                    IsPullRequest = true,
                    CreatedAt = pr.CreatedAt,
                    Excerpt = WorkItemText.Excerpt(pr.Body),
                });
        }

        _log.LogInformation("{Owner}/{Repo}: {Issues} open GlitchPilot issue(s), {Prs} open PR(s) for duplicate scan",
            owner, repo, issues.Count, prs.Count);

        return items;
    }

    public async Task AnnotateRecurrenceAsync(TriageOutcome outcome, KnownDuplicate known, CancellationToken ct = default)
    {
        var c = outcome.Candidate;
        var owner = c.GitHubOwner;
        var repo = c.GitHubRepo;

        // Tracking labels (+ base label so the history scan lists the item as GlitchPilot work)
        var labels = outcome.AllErrorIds.Select(TrackingLabels.For).Distinct().ToList();
        if (_labels.Count > 0)
            labels.Add(_labels[0]);
        await AddLabelsAsync(owner, repo, known.Number, labels, ct);

        var all = new[] { outcome }.Concat(outcome.MergedOutcomes).ToList();
        var kind = known.IsPullRequest ? "pull request" : "issue";
        var plural = all.Count == 1 ? "" : "s";
        var sb = new StringBuilder();
        sb.AppendLine("### 🔁 Recurrence detected by GlitchPilot");
        sb.AppendLine();
        sb.AppendLine($"This problem occurred again. GlitchPilot matched the following GlitchTip error{plural} to this {kind} and did **not** start a new Copilot run:");
        sb.AppendLine();
        foreach (var o in all)
            sb.AppendLine($"- [GlitchTip #{o.Candidate.ErrorId}]({o.Candidate.GlitchTipLink}) — {SafeMarkdown(o.Candidate.ErrorTitle)} ({o.Candidate.Occurrences}×, last seen {o.Candidate.LastOccurredAt ?? "—"})");
        if (!string.IsNullOrWhiteSpace(known.Reason))
        {
            sb.AppendLine();
            sb.AppendLine($"> {SafeMarkdown(known.Reason)}");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await _gh.Issue.Comment.Create(owner, repo, known.Number, sb.ToString());
            _log.LogInformation("Commented recurrence of #{Id} on #{Num}", c.ErrorId, known.Number);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Failed to comment recurrence on #{Num}", known.Number);
        }
    }

    private async Task AddLabelsAsync(string owner, string repo, int number, IReadOnlyList<string> labels, CancellationToken ct)
    {
        if (labels.Count == 0) return;
        try
        {
            ct.ThrowIfCancellationRequested();
            await _gh.Issue.Labels.AddToIssue(owner, repo, number, labels.ToArray());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Failed to add labels to #{Num}", number);
        }
    }

    /// <summary>
    /// Neutralises untrusted text (exception messages, AI output) for GitHub Markdown:
    /// no @mentions and #123 cross-references (zero-width space), no injected links, HTML or code spans.
    /// </summary>
    internal static string SafeMarkdown(string text)
    {
        var safe = text
            .Replace("\r", "")
            .Replace("\n", " ")
            .Replace("@", "@\u200B")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("`", "\\`")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
        return IssueRefRegex().Replace(safe, "#\u200B$1");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"#(\d)")]
    private static partial System.Text.RegularExpressions.Regex IssueRefRegex();

    private static string FormatTitle(string errorId, string title)
    {
        var prefix = $"[GlitchTip #{errorId}] ";
        var maxLen = 120 - prefix.Length;
        var trimmed = title.Length > maxLen ? title[..(maxLen - 3)] + "..." : title;
        return prefix + trimmed;
    }

    internal static string ComposeBody(TriageOutcome outcome)
    {
        var c = outcome.Candidate;
        var v = outcome.Verdict;
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

        // Files to Investigate (including those of merged errors — the fix must cover all of them)
        var files = v.FilesToInvestigate
            .Concat(outcome.MergedOutcomes.SelectMany(m => m.Verdict.FilesToInvestigate))
            .Distinct()
            .ToList();
        if (files.Count > 0)
        {
            sb.AppendLine("## Files to Investigate");
            foreach (var file in files)
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

        // Merged GlitchTip errors (same root cause, split by GlitchTip)
        if (outcome.MergedOutcomes.Count > 0)
        {
            sb.AppendLine("## Merged GlitchTip Errors");
            sb.AppendLine("GlitchTip reported the following errors separately; GlitchPilot identified them as the same root cause. " +
                          "**The fix must cover every call site below**, not only the one from the main stack trace.");
            if (!string.IsNullOrWhiteSpace(outcome.MergeReason))
            {
                sb.AppendLine();
                sb.AppendLine($"> {SafeMarkdown(outcome.MergeReason)}");
            }
            sb.AppendLine();
            foreach (var m in outcome.MergedOutcomes)
            {
                var mc = m.Candidate;
                sb.AppendLine($"### [GlitchTip #{mc.ErrorId}]({mc.GlitchTipLink}) — {SafeMarkdown(mc.ErrorTitle)} ({mc.Occurrences}×)");
                sb.AppendLine($"- **Source**: {mc.SourceLocation ?? "—"}");
                if (mc.FunctionName is not null) sb.AppendLine($"- **Function**: {mc.FunctionName}");
                if (mc.HttpEndpoint is not null) sb.AppendLine($"- **Endpoint**: {mc.HttpEndpoint}");
                if (mc.StackTrace is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(string.Join('\n', mc.StackTrace.Split('\n').Take(25)));
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
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
