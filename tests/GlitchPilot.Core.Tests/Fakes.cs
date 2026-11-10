using GlitchPilot.Core.Models;
using GlitchPilot.Core.Services;

namespace GlitchPilot.Core.Tests;

internal static class Make
{
    public static TriageOutcome Outcome(string id, int occurrences = 1, string priority = "high",
        bool actionable = true, string title = "NullReferenceException") => new()
    {
        Candidate = new TriageCandidate
        {
            ErrorId = id,
            ErrorTitle = title,
            Occurrences = occurrences,
            GlitchTipLink = $"https://glitchtip.example/issues/{id}",
            GitHubOwner = "org",
            GitHubRepo = "repo",
            ProjectSlug = "app",
        },
        Verdict = new Verdict
        {
            RequiresAction = actionable,
            Priority = priority,
            ErrorType = "bug",
            IssueTitle = $"{title} in Foo",
        },
    };
}

internal static class Tracked
{
    public static ExistingWorkItem Issue(int number) => new()
    {
        Number = number,
        Title = $"Issue {number}",
        Url = $"https://github.com/org/repo/issues/{number}",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
    };
}

internal sealed class FakeChat : IChatCompleter
{
    private readonly Queue<string> _responses;
    public List<(string System, string User)> Calls { get; } = [];

    public FakeChat(params string[] responses) => _responses = new Queue<string>(responses);

    public Task<string> CompleteAsync(string systemInstruction, string userPrompt, CancellationToken ct = default)
    {
        Calls.Add((systemInstruction, userPrompt));
        if (_responses.Count == 0) throw new InvalidOperationException("no response configured");
        return Task.FromResult(_responses.Dequeue());
    }
}

internal sealed class FakeGlitchTip(List<TriageCandidate> candidates) : IGlitchTipClient
{
    public Task<List<ProjectSummary>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<ProjectSummary> { new() { Id = "1", Slug = "app", Name = "app" } });

    public Task<List<TriageCandidate>> CollectCandidatesAsync(string projectSlug, string targetEnvironment,
        DateTimeOffset? since = null, CancellationToken ct = default) => Task.FromResult(candidates);

    public Task<TriageCandidate?> GetIssueAsync(string issueId, CancellationToken ct = default) =>
        Task.FromResult<TriageCandidate?>(null);
}

internal sealed class FakeClassifier : IErrorClassifier
{
    public Task<Verdict> EvaluateAsync(TriageCandidate candidate, CancellationToken ct = default) =>
        Task.FromResult(new Verdict
        {
            RequiresAction = !candidate.ErrorTitle.Contains("noise"),
            Priority = "high",
            ErrorType = "bug",
            IssueTitle = candidate.ErrorTitle,
        });
}

internal sealed class FakeFiler : IIssueFiler
{
    private int _next = 100;
    public List<TriageOutcome> Filed { get; } = [];
    public List<(TriageOutcome Outcome, KnownDuplicate Known)> Annotated { get; } = [];
    public Dictionary<string, ExistingWorkItem> TrackedByLabel { get; } = [];
    public List<ExistingWorkItem> OpenWork { get; } = [];
    public List<(int Number, List<string> Ids)> LabelsAdded { get; } = [];
    public int ListOpenWorkCalls { get; private set; }
    public int TrackedLookups { get; private set; }

    /// <summary>Exception thrown by the tracked lookup (simulates GitHub outage / timeout).</summary>
    public Exception? LookupFailure { get; set; }

    /// <summary>Exception thrown when annotating a recurrence.</summary>
    public Exception? AnnotateFailure { get; set; }

    /// <summary>When true, CreateAsync returns null (filing failed).</summary>
    public bool CreateFails { get; set; }

    public Task<int?> FileAsync(TriageOutcome outcome, CancellationToken ct = default) =>
        throw new NotSupportedException("TriagePipeline must use GetTrackedErrorsAsync + CreateAsync");

    public Task<int?> CreateAsync(TriageOutcome outcome, CancellationToken ct = default)
    {
        Filed.Add(outcome);
        return Task.FromResult<int?>(CreateFails ? null : _next++);
    }

    public List<List<string>> TrackedLookupIds { get; } = [];

    public Task<IReadOnlyDictionary<string, ExistingWorkItem>> GetTrackedErrorsAsync(
        string owner, string repo, IEnumerable<string> errorIds, CancellationToken ct = default)
    {
        TrackedLookups++;
        var ids = errorIds.ToList();
        TrackedLookupIds.Add(ids);
        if (LookupFailure is not null) throw LookupFailure;
        IReadOnlyDictionary<string, ExistingWorkItem> result = TrackedByLabel
            .Where(kv => ids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(result);
    }

    public Task AddTrackingLabelsAsync(string owner, string repo, int number, IEnumerable<string> errorIds, CancellationToken ct = default)
    {
        LabelsAdded.Add((number, errorIds.ToList()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExistingWorkItem>> ListOpenWorkAsync(string owner, string repo, int maxItems, CancellationToken ct = default)
    {
        ListOpenWorkCalls++;
        return Task.FromResult<IReadOnlyList<ExistingWorkItem>>(OpenWork);
    }

    public Task AnnotateRecurrenceAsync(TriageOutcome outcome, KnownDuplicate known, CancellationToken ct = default)
    {
        if (AnnotateFailure is not null) throw AnnotateFailure;
        Annotated.Add((outcome, known));
        return Task.CompletedTask;
    }
}

internal sealed class FakeMailer : ISmtpMailer
{
    public List<(string Subject, string Body)> Sent { get; } = [];

    public Task DeliverAsync(string subject, string htmlBody, CancellationToken ct = default)
    {
        Sent.Add((subject, htmlBody));
        return Task.CompletedTask;
    }
}
