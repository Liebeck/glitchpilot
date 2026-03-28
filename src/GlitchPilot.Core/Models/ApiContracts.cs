using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlitchPilot.Core.Models;

/// <summary>A project inside a GlitchTip organization.</summary>
public class ProjectSummary
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>An error group (called "issue" in the GlitchTip API).</summary>
public class ErrorGroup
{
    [JsonPropertyName("id")]        public string Id { get; set; } = "";
    [JsonPropertyName("title")]     public string? Title { get; set; }
    [JsonPropertyName("culprit")]   public string? Source { get; set; }
    [JsonPropertyName("level")]     public string? Severity { get; set; }
    [JsonPropertyName("count")]     public string? TotalOccurrences { get; set; }
    [JsonPropertyName("firstSeen")] public string? DetectedAt { get; set; }
    [JsonPropertyName("lastSeen")]  public string? LatestAt { get; set; }
    [JsonPropertyName("metadata")]  public ErrorMeta? Meta { get; set; }

    [JsonIgnore]
    public int OccurrenceCount => int.TryParse(TotalOccurrences, out var n) ? n : 0;
}

public class ErrorMeta
{
    [JsonPropertyName("function")] public string? FunctionName { get; set; }
}

/// <summary>A single occurrence (event) of an error group.</summary>
public class Occurrence
{
    [JsonPropertyName("culprit")]  public string? Source { get; set; }
    [JsonPropertyName("entries")]  public List<PayloadSection>? Sections { get; set; }
    [JsonPropertyName("tags")]     public List<EventTag>? Tags { get; set; }
    [JsonPropertyName("user")]     public JsonElement? User { get; set; }
    [JsonPropertyName("contexts")] public JsonElement? Contexts { get; set; }

    /// <summary>Extract the value of a specific tag by key.</summary>
    public string? TagValue(string key) =>
        Tags?.FirstOrDefault(t => t.Name == key)?.Content;
}

public class PayloadSection
{
    [JsonPropertyName("type")] public string? Kind { get; set; }
    [JsonPropertyName("data")] public JsonElement? Payload { get; set; }
}

public class EventTag
{
    [JsonPropertyName("key")]   public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string? Content { get; set; }
}
