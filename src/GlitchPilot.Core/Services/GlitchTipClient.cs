using System.Net.Http.Json;
using System.Text.Json;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Services;

public sealed class GlitchTipClient : IGlitchTipClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GlitchTipClient> _log;
    private readonly string _org;
    private readonly string _baseUrl;
    private readonly int _minOccurrences;
    private readonly HashSet<string> _excludeIds;
    private readonly List<string> _excludeTitles;

    public GlitchTipClient(HttpClient http, ILogger<GlitchTipClient> log)
    {
        _http = http;
        _log = log;
        _org = EnvironmentConfig.GlitchTipOrg;
        _baseUrl = EnvironmentConfig.GlitchTipBaseUrl.TrimEnd('/');
        _minOccurrences = EnvironmentConfig.MinOccurrences;
        _excludeIds = EnvironmentConfig.ExcludeIds;
        _excludeTitles = EnvironmentConfig.ExcludeTitlePatterns;
    }

    public async Task<List<ProjectSummary>> ListProjectsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ProjectSummary>>(
            $"organizations/{_org}/projects/", ct);
        return result ?? [];
    }

    public async Task<List<TriageCandidate>> CollectCandidatesAsync(
        string projectSlug, string targetEnvironment, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var endpoint = $"projects/{_org}/{projectSlug}/issues/";
        if (since.HasValue)
            endpoint += $"?start={since.Value:yyyy-MM-ddTHH:mm:ss}";

        List<ErrorGroup> groups;
        try
        {
            groups = await _http.GetFromJsonAsync<List<ErrorGroup>>(endpoint, ct) ?? [];
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Could not retrieve errors for {Slug}", projectSlug);
            return [];
        }

        // Filter in a single LINQ pipeline
        var eligible = groups
            .Where(g => g.OccurrenceCount >= _minOccurrences)
            .Where(g => !_excludeIds.Contains(g.Id))
            .Where(g => !_excludeTitles.Any(phrase =>
                (g.Title ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Enrich each eligible error with data from its latest occurrence
        var candidates = new List<TriageCandidate>();
        foreach (var group in eligible)
        {
            var enriched = await EnrichAsync(group, targetEnvironment, ct);
            if (enriched is not null)
                candidates.Add(enriched);
        }

        // Highest occurrence count first
        return candidates.OrderByDescending(c => c.Occurrences).ToList();
    }

    public async Task<TriageCandidate?> GetIssueAsync(string issueId, CancellationToken ct = default)
    {
        var group = await _http.GetFromJsonAsync<ErrorGroup>($"issues/{issueId}/", ct);
        if (group is null) return null;

        return await EnrichAsync(group, targetEnvironment: "", ct);
    }

    /// <summary>Fetches the latest occurrence and merges it with the error group.</summary>
    private async Task<TriageCandidate?> EnrichAsync(
        ErrorGroup group, string targetEnvironment, CancellationToken ct)
    {
        string? source = group.Source;
        string? environment = null;
        string? httpEndpoint = null;
        string? stackTrace = null;
        var breadcrumbs = new List<Breadcrumb>();
        var tags = new Dictionary<string, string>();
        string? userId = null;
        string? requestHeaders = null;
        string? serverOs = null;
        string? appRelease = null;
        bool? exceptionHandled = null;

        try
        {
            var events = await _http.GetFromJsonAsync<List<Occurrence>>(
                $"issues/{group.Id}/events/?limit=1", ct);

            if (events?.FirstOrDefault() is { } latest)
            {
                source = latest.Source ?? source;
                environment = latest.TagValue("environment");
                httpEndpoint = ExtractHttpEndpoint(latest);
                stackTrace = ExtractStackTrace(latest);
                breadcrumbs = ExtractBreadcrumbs(latest);
                requestHeaders = ExtractRequestHeaders(latest);
                exceptionHandled = ExtractExceptionHandled(latest);
                appRelease = latest.TagValue("release");

                // Extract all tags
                if (latest.Tags is not null)
                    foreach (var tag in latest.Tags)
                        if (tag.Content is not null)
                            tags[tag.Name] = tag.Content;

                // Extract user ID
                if (latest.User is { } user && user.TryGetProperty("id", out var uid))
                    userId = uid.GetString();

                // Extract OS from contexts
                if (latest.Contexts is { } ctx
                    && ctx.TryGetProperty("os", out var os)
                    && os.TryGetProperty("raw_description", out var osDesc))
                    serverOs = osDesc.GetString();
            }
        }
        catch
        {
            // Occurrence details are best-effort — proceed without them
        }

        // Skip if environment doesn't match
        if (!string.IsNullOrEmpty(targetEnvironment)
            && !string.IsNullOrEmpty(environment)
            && !environment.Equals(targetEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new TriageCandidate
        {
            ErrorId = group.Id,
            ErrorTitle = group.Title ?? "(untitled)",
            SourceLocation = source,
            FunctionName = group.Meta?.FunctionName,
            HttpEndpoint = httpEndpoint,
            SeverityLevel = group.Severity ?? "error",
            Occurrences = group.OccurrenceCount,
            DetectedAt = group.DetectedAt,
            LastOccurredAt = group.LatestAt,
            Environment = environment ?? targetEnvironment,
            GlitchTipLink = $"{_baseUrl}/{_org}/issues/{group.Id}",
            StackTrace = stackTrace,
            Breadcrumbs = breadcrumbs,
            Tags = tags,
            UserId = userId,
            RequestHeaders = requestHeaders,
            ServerOs = serverOs,
            AppRelease = appRelease,
            ExceptionHandled = exceptionHandled,
        };
    }

    /// <summary>Extracts a readable stack trace from the exception entry.</summary>
    private static string? ExtractStackTrace(Occurrence occurrence)
    {
        var exceptionSection = occurrence.Sections?
            .FirstOrDefault(s => s.Kind == "exception");

        if (exceptionSection?.Payload is not { } data)
            return null;

        if (!data.TryGetProperty("values", out var values))
            return null;

        var lines = new List<string>();
        foreach (var ex in values.EnumerateArray())
        {
            var type = ex.TryGetProperty("type", out var t) ? t.GetString() : "Exception";
            var msg = ex.TryGetProperty("value", out var v) ? v.GetString() : "";
            lines.Add($"{type}: {msg}");

            if (ex.TryGetProperty("stacktrace", out var st)
                && st.TryGetProperty("frames", out var frames))
            {
                foreach (var frame in frames.EnumerateArray())
                {
                    var file = frame.TryGetProperty("filename", out var f) ? f.GetString() : null;
                    var lineNo = frame.TryGetProperty("lineNo", out var ln) ? ln.GetInt32().ToString() : "?";
                    var func = frame.TryGetProperty("function", out var fn) ? fn.GetString() : "?";
                    lines.Add($"  at {func} in {file ?? "?"}:{lineNo}");
                }
            }
        }

        return lines.Count > 0 ? string.Join('\n', lines) : null;
    }

    /// <summary>Extracts breadcrumbs (log entries leading up to the error).</summary>
    private static List<Breadcrumb> ExtractBreadcrumbs(Occurrence occurrence)
    {
        var section = occurrence.Sections?
            .FirstOrDefault(s => s.Kind == "breadcrumbs");

        if (section?.Payload is not { } data
            || !data.TryGetProperty("values", out var values))
            return [];

        var result = new List<Breadcrumb>();
        foreach (var b in values.EnumerateArray())
        {
            result.Add(new Breadcrumb
            {
                Timestamp = b.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null,
                Category = b.TryGetProperty("category", out var cat) ? cat.GetString() : null,
                Level = b.TryGetProperty("level", out var lvl) ? lvl.GetString() : null,
                Message = b.TryGetProperty("message", out var msg) ? msg.GetString() : null,
            });
        }
        return result;
    }

    /// <summary>Extracts request headers as a readable string.</summary>
    private static string? ExtractRequestHeaders(Occurrence occurrence)
    {
        var section = occurrence.Sections?
            .FirstOrDefault(s => s.Kind == "request");

        if (section?.Payload is not { } data
            || !data.TryGetProperty("headers", out var headers))
            return null;

        var lines = new List<string>();
        foreach (var header in headers.EnumerateArray())
        {
            var items = header.EnumerateArray().ToList();
            if (items.Count == 2)
                lines.Add($"{items[0].GetString()}: {items[1].GetString()}");
        }
        return lines.Count > 0 ? string.Join('\n', lines) : null;
    }

    /// <summary>Extracts whether the exception was handled.</summary>
    private static bool? ExtractExceptionHandled(Occurrence occurrence)
    {
        var section = occurrence.Sections?
            .FirstOrDefault(s => s.Kind == "exception");

        if (section?.Payload is not { } data
            || !data.TryGetProperty("values", out var values))
            return null;

        foreach (var ex in values.EnumerateArray())
        {
            if (ex.TryGetProperty("mechanism", out var mech)
                && mech.TryGetProperty("handled", out var handled))
                return handled.GetBoolean();
        }
        return null;
    }

    /// <summary>Extracts "METHOD url" from a request payload section, if present.</summary>
    private static string? ExtractHttpEndpoint(Occurrence occurrence)
    {
        var requestSection = occurrence.Sections?
            .FirstOrDefault(s => s.Kind == "request");

        if (requestSection?.Payload is not { } data)
            return null;

        var method = data.TryGetProperty("method", out var m) ? m.GetString() : null;
        var url = data.TryGetProperty("url", out var u) ? u.GetString() : null;

        return (method, url) switch
        {
            (not null, not null) => $"{method} {url}",
            _ => null,
        };
    }
}
