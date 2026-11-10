using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Services;

/// <summary>The glitchtip:{id} labels that link GitHub issues / PRs to GlitchTip errors.</summary>
public static class TrackingLabels
{
    public const string Prefix = "glitchtip:";

    public static string For(string errorId) => Prefix + errorId;

    /// <summary>The open issue / PR tracking any of the given errors, or null.</summary>
    public static ExistingWorkItem? Find(IReadOnlyDictionary<string, ExistingWorkItem> tracked, IEnumerable<string> errorIds)
    {
        foreach (var id in errorIds)
            if (tracked.TryGetValue(id, out var item))
                return item;
        return null;
    }

    /// <summary>Error IDs whose label is not yet on the given issue / PR.</summary>
    public static List<string> Missing(IReadOnlyDictionary<string, ExistingWorkItem> tracked, int number, IEnumerable<string> errorIds) =>
        errorIds
            .Distinct()
            .Where(id => !(tracked.TryGetValue(id, out var item) && item.Number == number))
            .ToList();
}
