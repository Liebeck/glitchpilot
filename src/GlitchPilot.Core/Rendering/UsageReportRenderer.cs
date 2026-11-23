using System.Globalization;
using System.Net;
using System.Text;
using GlitchPilot.Core.Models;

namespace GlitchPilot.Core.Rendering;

/// <summary>Renders the "Usage &amp; cost" block that replaces <see cref="TriageReportRenderer.UsagePlaceholder"/>.</summary>
public static class UsageReportRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string InsertInto(string htmlBody, RunRecord run, string projectSlug)
    {
        var project = run.Projects.FirstOrDefault(p => p.Slug.Equals(projectSlug, StringComparison.OrdinalIgnoreCase));
        return htmlBody.Replace(TriageReportRenderer.UsagePlaceholder, Render(run, project));
    }

    /// <summary>Usage section for when the cost could not be determined at all.</summary>
    public static string Unavailable(string reason) => $"""
        <tr><td style="padding:0 36px 24px;">
          <h2 style="font-size:16px;color:#0f172a;margin:0 0 12px;">Usage &amp; cost</h2>
          <div style="font-size:12px;color:#475569;margin:6px 0;line-height:1.5;">
            <strong style="color:#0f172a;">Copilot cost:</strong> n/a <span style="color:#dc2626;">&#9888; {Esc(reason)}</span>
          </div>
        </td></tr>
        """;

    /// <summary>The Copilot usage section, or nothing if the run was not reported with billing data (no storage).</summary>
    public static string Render(RunRecord run, ProjectRunUsage? project)
    {
        if (run.Billing is null)
            return run.UsageError is { } reason ? Unavailable(reason) : "";

        var sb = new StringBuilder();
        sb.Append("""
            <tr><td style="padding:0 36px 24px;">
              <h2 style="font-size:16px;color:#0f172a;margin:0 0 12px;">Usage &amp; cost</h2>
            """);

        // ── Copilot cloud agent per issue (measured) ──
        var issues = project?.Issues ?? [];
        if (issues.Count > 0)
        {
            sb.Append("""
                <table width="100%" cellpadding="0" cellspacing="0" style="font-size:12px;color:#475569;border-collapse:collapse;margin:8px 0;">
                  <tr style="color:#94a3b8;text-align:left;">
                    <th style="padding:4px 6px;font-weight:600;">Issue</th>
                    <th style="padding:4px 6px;font-weight:600;">Copilot</th>
                    <th style="padding:4px 6px;font-weight:600;text-align:right;">Sessions</th>
                    <th style="padding:4px 6px;font-weight:600;text-align:right;">Minutes</th>
                    <th style="padding:4px 6px;font-weight:600;text-align:right;">≈ Cost</th>
                  </tr>
                """);
            foreach (var i in issues)
            {
                var c = i.Copilot;
                var pr = c?.PullRequests.FirstOrDefault();
                var status = c is null ? "—"
                    : pr is null ? Esc(c.Status)
                    : $"<a href=\"{Esc(pr.Url)}\" style=\"color:#3b82f6;text-decoration:none;\">PR #{pr.Number}</a> · {Esc(c.Status)}";
                if (c?.Error is not null) status += " &#9888;";
                var cost = c?.EstimatedGrossUsd is { } usd
                    ? $"${usd.ToString("0.00", Inv)}" + (c.EstimatedAiCredits is { } cr ? $" ({cr.ToString("0.#", Inv)} cr)" : "")
                    : "n/a";
                sb.Append($"""
                      <tr style="border-top:1px solid #e2e8f0;">
                        <td style="padding:4px 6px;"><a href="{Esc(i.Url)}" style="color:#3b82f6;text-decoration:none;">#{i.Number}</a></td>
                        <td style="padding:4px 6px;">{status}</td>
                        <td style="padding:4px 6px;text-align:right;">{c?.Sessions.Count ?? 0}</td>
                        <td style="padding:4px 6px;text-align:right;">{(c?.TotalSessionMinutes ?? 0).ToString("0.0", Inv)}</td>
                        <td style="padding:4px 6px;text-align:right;">{cost}</td>
                      </tr>
                    """);
            }
            sb.Append("</table>");
            sb.Append(Note(run.CostBasis == "run"
                ? "Minutes are measured from the Copilot agent's workflow runs. GitHub bills AI credits per day, not per session — the cost column splits the cloud-agent credits used during this run (day total now minus day total at the start) by session minutes. Other agent sessions running at the same time are included."
                : "Minutes are measured from the Copilot agent's workflow runs. GitHub bills AI credits per day, not per session — the cost column splits the day's cloud-agent credits by session minutes (upper bound if other agent sessions ran that day; no usable snapshot from the start of the run)."));
        }

        if (run.UsageError is { } error)
            sb.Append(Section("Copilot cost", $"n/a <span style=\"color:#dc2626;\">&#9888; {Esc(error)}</span>"));

        // ── GitHub billing (real numbers) ──
        if (run.BillingDuringRun is { } during)
            sb.Append(Section($"GitHub Copilot during this run ({run.StartedAt.UtcDateTime:HH:mm}–{during.FetchedAt.UtcDateTime:HH:mm} UTC)", BillingLine(during)));
        if (run.Billing is { } today)
            sb.Append(Section($"GitHub Copilot {today.Day:yyyy-MM-dd} (so far)", BillingLine(today)));
        if (run.BillingPreviousDay is { } prev)
            sb.Append(Section($"GitHub Copilot {prev.Day:yyyy-MM-dd} (final)", BillingLine(prev)));

        sb.Append("""
              <p style="font-size:11px;color:#94a3b8;margin:8px 0 0;">Full numbers are stored as JSON in the GlitchPilot usage container.</p>
            </td></tr>
            """);
        return sb.ToString();
    }

    private static string BillingLine(CopilotBillingDay day)
    {
        if (day.Error is not null) return $"n/a <span style=\"color:#dc2626;\">&#9888; Billing API: {Esc(day.Error)}</span>";
        if (day.Items.Count == 0) return "no usage reported (yet)";

        var line = $"Copilot ${day.CopilotGrossUsd.ToString("0.00", Inv)} used · ${day.CopilotNetUsd.ToString("0.00", Inv)} billed";
        if (day.AgentGrossUsd > 0)
            line += $" · cloud agent ${day.AgentGrossUsd.ToString("0.00", Inv)} ({day.AgentGrossQuantity.ToString("0.#", Inv)} {Esc(day.Items.First(i => i.IsAgent).UnitType)})";

        var models = day.Items.Where(i => i.IsCopilot && i.GrossAmount > 0)
            .GroupBy(i => i.Model ?? i.Sku)
            .OrderByDescending(g => g.Sum(i => i.GrossAmount))
            .Take(4)
            .Select(g => $"{Esc(g.Key)} ${g.Sum(i => i.GrossAmount).ToString("0.00", Inv)}");
        var breakdown = string.Join(", ", models);
        return breakdown.Length == 0 ? line : $"{line}<br><span style=\"color:#94a3b8;\">{breakdown}</span>";
    }

    private static string Section(string label, string value) => $"""
        <div style="font-size:12px;color:#475569;margin:6px 0;line-height:1.5;">
          <strong style="color:#0f172a;">{Esc(label)}:</strong> {value}
        </div>
        """;

    private static string Note(string text) =>
        $"<p style=\"font-size:11px;color:#94a3b8;margin:4px 0 8px;\">{Esc(text)}</p>";

    private static string Esc(string text) => WebUtility.HtmlEncode(text);
}
