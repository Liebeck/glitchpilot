using System.Text.Json.Serialization;

namespace GlitchPilot.Core.Models;

/// <summary>
/// One triage run, stored in Blob Storage. While the Copilot sessions are still running it is "pending"
/// (and carries the rendered emails); once reported it is completed with Copilot / billing data.
/// </summary>
public sealed class RunRecord
{
    public int SchemaVersion { get; set; } = 2;
    public string RunId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>When all issues of the run were filed (the Copilot sessions start around this time).</summary>
    public DateTimeOffset FiledAt { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public int LookbackHours { get; set; }

    public List<ProjectRunUsage> Projects { get; set; } = [];

    /// <summary>Copilot / AI-credit billing of the UTC day the issues were filed (snapshot at report time — can lag).</summary>
    public CopilotBillingDay? Billing { get; set; }
    /// <summary>Billing of the same UTC day, taken when the run started — before any Copilot session was triggered.</summary>
    public CopilotBillingDay? BillingAtStart { get; set; }
    /// <summary>
    /// <see cref="Billing"/> minus <see cref="BillingAtStart"/>: what was used while this run's Copilot sessions ran.
    /// Null when the two snapshots are not comparable (run crossed midnight UTC, or a billing call failed).
    /// </summary>
    public CopilotBillingDay? BillingDuringRun { get; set; }
    /// <summary>What the per-issue cost estimate is based on: "run" (<see cref="BillingDuringRun"/>) or "day" (<see cref="Billing"/>).</summary>
    public string? CostBasis { get; set; }
    /// <summary>Why the cost could not be determined, if a step of the usage tracking failed.</summary>
    public string? UsageError { get; set; }
    /// <summary>Billing of the previous UTC day (final by now) — to evaluate the previous run with complete numbers.</summary>
    public CopilotBillingDay? BillingPreviousDay { get; set; }

    /// <summary>Rendered triage emails waiting to be sent. Removed from the completed record.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PendingEmail>? Emails { get; set; }
}

public sealed class PendingEmail
{
    public string ProjectSlug { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public bool Sent { get; set; }
}

public sealed class ProjectRunUsage
{
    public string Slug { get; set; } = "";
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public int Scanned { get; set; }
    public int Actionable { get; set; }
    public int AlreadyKnown { get; set; }
    public int Merged { get; set; }
    public int Dismissed { get; set; }
    /// <summary>GitHub issues filed in this run (each one starts a Copilot session).</summary>
    public List<FiledIssueUsage> Issues { get; set; } = [];
}

public sealed class FiledIssueUsage
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> ErrorIds { get; set; } = [];
    public string Priority { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public CopilotIssueUsage? Copilot { get; set; }
}

/// <summary>What the Copilot cloud agent did for one issue (measured via GitHub API).</summary>
public sealed class CopilotIssueUsage
{
    /// <summary>pending (no PR yet), running, completed, or error.</summary>
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public List<CopilotPullRequest> PullRequests { get; set; } = [];
    public List<CopilotSession> Sessions { get; set; } = [];
    public double TotalSessionMinutes => Math.Round(Sessions.Sum(s => s.DurationMinutes ?? 0), 2);

    /// <summary>
    /// Share of the cloud-agent AI-credit cost used during the run (or, if not available, the whole day),
    /// split by session minutes over the GlitchPilot sessions of the run. An estimate: GitHub does not
    /// report cost per session.
    /// </summary>
    public decimal? EstimatedGrossUsd { get; set; }
    public decimal? EstimatedAiCredits { get; set; }

    [JsonIgnore] public bool IsFinished => Status is "completed" or "error";
}

public sealed class CopilotPullRequest
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string State { get; set; } = "";
    public bool Draft { get; set; }
    public bool Merged { get; set; }
    public string HeadBranch { get; set; } = "";
    public string? Author { get; set; }
}

/// <summary>One Copilot agent workflow run (a session; follow-up comments start further sessions).</summary>
public sealed class CopilotSession
{
    public long WorkflowRunId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Conclusion { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public double? DurationMinutes { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>AI-credit / premium-request usage of one UTC day from the GitHub billing API.</summary>
public sealed class CopilotBillingDay
{
    public DateOnly Day { get; set; }
    /// <summary>ai_credit or premium_request — whichever report the account has.</summary>
    public string? Source { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public string? Error { get; set; }
    public List<BillingUsageItem> Items { get; set; } = [];

    public decimal CopilotGrossUsd => Items.Where(i => i.IsCopilot).Sum(i => i.GrossAmount);
    public decimal CopilotNetUsd => Items.Where(i => i.IsCopilot).Sum(i => i.NetAmount);
    public decimal AgentGrossUsd => Items.Where(i => i.IsAgent).Sum(i => i.GrossAmount);
    public decimal AgentNetUsd => Items.Where(i => i.IsAgent).Sum(i => i.NetAmount);
    public decimal AgentGrossQuantity => Items.Where(i => i.IsAgent).Sum(i => i.GrossQuantity);

    /// <summary>
    /// Usage added between two snapshots of the same day (per product / SKU / model / unit; never negative),
    /// or null if they are not comparable: different day or report type, or one of them failed.
    /// </summary>
    public static CopilotBillingDay? Delta(CopilotBillingDay? before, CopilotBillingDay? after)
    {
        if (before is null || after is null || before.Error is not null || after.Error is not null
            || before.Day != after.Day)
            return null;
        // An empty "before" (nothing used yet that day) is comparable whatever report type it came from
        if (before.Items.Count > 0 && before.Source != after.Source) return null;

        static (string, string, string, string) Key(BillingUsageItem i) =>
            (i.Product.ToLowerInvariant(), i.Sku.ToLowerInvariant(), i.Model?.ToLowerInvariant() ?? "", i.UnitType.ToLowerInvariant());

        var earlier = before.Items.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var items = new List<BillingUsageItem>();
        foreach (var group in after.Items.GroupBy(Key))
        {
            var now = group.ToList();
            var was = earlier.GetValueOrDefault(group.Key) ?? [];
            decimal Diff(Func<BillingUsageItem, decimal> f) => Math.Max(0, now.Sum(f) - was.Sum(f));

            var item = new BillingUsageItem
            {
                Product = now[0].Product,
                Sku = now[0].Sku,
                Model = now[0].Model,
                UnitType = now[0].UnitType,
                PricePerUnit = now[0].PricePerUnit,
                GrossQuantity = Diff(i => i.GrossQuantity),
                GrossAmount = Diff(i => i.GrossAmount),
                DiscountQuantity = Diff(i => i.DiscountQuantity),
                DiscountAmount = Diff(i => i.DiscountAmount),
                NetQuantity = Diff(i => i.NetQuantity),
                NetAmount = Diff(i => i.NetAmount),
            };
            if (item.GrossQuantity > 0 || item.GrossAmount > 0 || item.NetAmount > 0)
                items.Add(item);
        }

        return new CopilotBillingDay
        {
            Day = after.Day,
            Source = after.Source,
            FetchedAt = after.FetchedAt,
            Items = items,
        };
    }
}

public sealed class BillingUsageItem
{
    public string Product { get; set; } = "";
    public string Sku { get; set; } = "";
    public string? Model { get; set; }
    public string UnitType { get; set; } = "";
    public decimal PricePerUnit { get; set; }
    public decimal GrossQuantity { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal DiscountQuantity { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal NetQuantity { get; set; }
    public decimal NetAmount { get; set; }

    [JsonIgnore] public bool IsCopilot => Product.Contains("copilot", StringComparison.OrdinalIgnoreCase);
    /// <summary>Cloud / coding agent SKUs (e.g. "cloud agent AI credits").</summary>
    [JsonIgnore] public bool IsAgent => Sku.Contains("agent", StringComparison.OrdinalIgnoreCase);
}
