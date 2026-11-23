using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlitchPilot.Core.Services;

/// <summary>
/// Builds and verifies the signed links behind the "Create GitHub issue" button in the email.
/// The link carries project slug, GlitchTip error ID and expiry; an HMAC-SHA256 signature over these
/// values is the only authorization — whoever has an unexpired link from the email may file the issue.
/// </summary>
public static class IssueLinks
{
    public const string ProjectParam = "p";
    public const string ErrorParam = "e";
    public const string ExpiresParam = "x";
    public const string SignatureParam = "s";

    public static string Build(string endpoint, string key, string projectSlug, string errorId, DateTimeOffset expires)
    {
        var x = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = Sign(key, projectSlug, errorId, x);
        var separator = endpoint.Contains('?') ? '&' : '?';
        return $"{endpoint}{separator}{ProjectParam}={Uri.EscapeDataString(projectSlug)}" +
               $"&{ErrorParam}={Uri.EscapeDataString(errorId)}&{ExpiresParam}={x}&{SignatureParam}={sig}";
    }

    /// <summary>Returns null if the link is valid, otherwise a reason suitable for showing to the user.</summary>
    public static string? Verify(string key, string? projectSlug, string? errorId, string? expires, string? signature,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(projectSlug) || string.IsNullOrEmpty(errorId) ||
            string.IsNullOrEmpty(expires) || string.IsNullOrEmpty(signature))
            return "The link is incomplete.";

        var expected = Encoding.ASCII.GetBytes(Sign(key, projectSlug, errorId, expires));
        var actual = Encoding.ASCII.GetBytes(signature);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return "The link is invalid.";

        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) ||
            DateTimeOffset.FromUnixTimeSeconds(unix) < now)
            return "The link has expired. Use the button from a more recent GlitchPilot email.";

        return null;
    }

    private static string Sign(string key, string projectSlug, string errorId, string expires)
    {
        var data = Encoding.UTF8.GetBytes($"glitchpilot-file-issue\n{projectSlug}\n{errorId}\n{expires}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), data);
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
