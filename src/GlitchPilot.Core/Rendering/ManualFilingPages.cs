using System.Net;
using GlitchPilot.Core.Models;
using GlitchPilot.Core.Services;

namespace GlitchPilot.Core.Rendering;

/// <summary>HTML pages served by the "Create GitHub issue" endpoint (confirmation, result, error).</summary>
public static class ManualFilingPages
{
    /// <summary>
    /// Confirmation page. The signed link values are posted back as hidden fields, so opening the link
    /// (or a mail scanner prefetching it) never files anything.
    /// </summary>
    public static string RenderConfirm(ManualFilingPreview preview, IReadOnlyDictionary<string, string> linkFields)
    {
        var c = preview.Candidate!;
        var hidden = string.Concat(linkFields.Select(f =>
            $"<input type=\"hidden\" name=\"{Esc(f.Key)}\" value=\"{Esc(f.Value)}\">"));

        string body;
        if (preview.Status == ManualFilingStatus.AlreadyTracked && preview.Existing is { } existing)
        {
            var kind = existing.IsPullRequest ? "pull request" : "issue";
            body = $"""
                <p style="color:#475569;">This error is already tracked by an open {kind}:
                  <a href="{Esc(existing.Url)}">#{existing.Number} {Esc(existing.Title)}</a></p>
                <p style="color:#64748b;font-size:13px;">No new issue is needed.</p>
                """;
        }
        else
        {
            body = $"""
                <p style="color:#475569;">GlitchPilot dismissed this error, so no issue was filed. Creating one
                  files a GitHub issue in the project's repository and assigns Copilot to work on it.</p>
                <form method="post" action="">
                  {hidden}
                  <label for="notes" style="display:block;font-weight:600;font-size:14px;margin:16px 0 6px;">
                    How should it be fixed? <span style="font-weight:400;color:#64748b;">(optional, goes into the issue)</span>
                  </label>
                  <textarea id="notes" name="notes" rows="7" maxlength="{ManualFilingService.MaxNotesLength}"
                    style="width:100%;box-sizing:border-box;font:inherit;font-size:14px;padding:10px;border:1px solid #cbd5e1;border-radius:8px;"
                    placeholder="e.g. Wrap the call in a Polly retry with exponential backoff; the API rate-limits bursts."></textarea>
                  <button type="submit"
                    style="margin-top:14px;background:#0f172a;color:#fff;border:0;border-radius:8px;padding:10px 18px;font-size:14px;font-weight:600;cursor:pointer;">
                    Create GitHub issue
                  </button>
                </form>
                """;
        }

        return Page("Create GitHub issue", $"""
            <h1 style="font-size:20px;margin:0 0 6px;">[#{Esc(c.ErrorId)}] {Esc(c.ErrorTitle)}</h1>
            <p style="color:#94a3b8;font-size:13px;margin:0 0 16px;">
              {c.Occurrences}x · {Esc(c.SourceLocation ?? "—")} ·
              <a href="{Esc(c.GlitchTipLink)}">Open in GlitchTip</a>
            </p>
            {body}
            """);
    }

    public static string RenderResult(ManualFilingResult result) => result.Status switch
    {
        ManualFilingStatus.Created => Page("Issue created", $"""
            <h1 style="font-size:20px;color:#16a34a;">&#9989; Issue created</h1>
            <p><a href="{Esc(result.Url ?? "")}">GitHub issue #{result.Number}</a> was filed and Copilot was asked to work on it.</p>
            """),
        ManualFilingStatus.AlreadyTracked => Page("Already tracked", $"""
            <h1 style="font-size:20px;color:#7c3aed;">Already tracked</h1>
            <p>This error already has an open issue or pull request:
              <a href="{Esc(result.Url ?? "")}">#{result.Number}</a>. No new issue was filed.</p>
            """),
        _ => RenderError(result.Message ?? "Something went wrong."),
    };

    public static string RenderError(string message) => Page("GlitchPilot", $"""
        <h1 style="font-size:20px;color:#dc2626;">&#9888; Could not continue</h1>
        <p style="color:#475569;">{Esc(message)}</p>
        """);

    private static string Page(string title, string content) => $"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{Esc(title)} · GlitchPilot</title></head>
        <body style="margin:0;background:#f0f2f5;font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:#0f172a;">
          <div style="max-width:640px;margin:32px auto;padding:0 16px;">
            <div style="background:#0f172a;color:#f8fafc;border-radius:12px 12px 0 0;padding:16px 24px;font-weight:600;">GlitchPilot</div>
            <div style="background:#fff;border-radius:0 0 12px 12px;padding:24px;line-height:1.5;">
              {content}
            </div>
          </div>
        </body></html>
        """;

    private static string Esc(string text) => WebUtility.HtmlEncode(text);
}
