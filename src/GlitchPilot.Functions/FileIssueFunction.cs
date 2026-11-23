using System.Collections.Specialized;
using System.Net;
using System.Web;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Rendering;
using GlitchPilot.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Functions;

/// <summary>
/// Target of the "Create GitHub issue anyway" button in the triage email.
/// GET shows a confirmation page (safe for mail scanners that prefetch links); POST files the issue.
/// Anonymous on purpose — no GitHub or Azure login is needed; the HMAC-signed, expiring link is the authorization.
/// </summary>
public class FileIssueFunction
{
    private readonly ManualFilingService _service;
    private readonly ILogger<FileIssueFunction> _log;

    public FileIssueFunction(ManualFilingService service, ILogger<FileIssueFunction> log)
    {
        _service = service;
        _log = log;
    }

    [Function("FileIssue")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "file-issue")] HttpRequestData req,
        CancellationToken ct)
    {
        ManualFilingSettings? settings;
        try
        {
            settings = EnvironmentConfig.ManualFiling;
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "Manual filing is misconfigured");
            return await HtmlAsync(req, HttpStatusCode.InternalServerError,
                ManualFilingPages.RenderError("This endpoint is not configured correctly."));
        }

        if (settings is null)
            return await HtmlAsync(req, HttpStatusCode.NotFound,
                ManualFilingPages.RenderError("Creating issues from the email is not enabled."));

        var isPost = req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        var fields = isPost
            ? HttpUtility.ParseQueryString(await new StreamReader(req.Body).ReadToEndAsync(ct))
            : HttpUtility.ParseQueryString(req.Url.Query);

        var slug = fields[IssueLinks.ProjectParam];
        var errorId = fields[IssueLinks.ErrorParam];
        var failure = IssueLinks.Verify(settings.SigningKey, slug, errorId,
            fields[IssueLinks.ExpiresParam], fields[IssueLinks.SignatureParam], DateTimeOffset.UtcNow);
        if (failure is not null)
        {
            _log.LogWarning("Rejected file-issue request: {Reason}", failure);
            return await HtmlAsync(req, HttpStatusCode.Forbidden, ManualFilingPages.RenderError(failure));
        }

        if (!isPost)
        {
            var preview = await _service.PreviewAsync(slug!, errorId!, ct);
            return preview.Candidate is null
                ? await HtmlAsync(req, HttpStatusCode.NotFound, ManualFilingPages.RenderError(preview.Message ?? "Not found."))
                : await HtmlAsync(req, HttpStatusCode.OK, ManualFilingPages.RenderConfirm(preview, LinkFields(fields)));
        }

        var result = await _service.FileAsync(slug!, errorId!, fields["notes"], ct);
        var status = result.Status switch
        {
            ManualFilingStatus.Created or ManualFilingStatus.AlreadyTracked => HttpStatusCode.OK,
            ManualFilingStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.BadGateway,
        };
        return await HtmlAsync(req, status, ManualFilingPages.RenderResult(result));
    }

    private static Dictionary<string, string> LinkFields(NameValueCollection fields) =>
        new[] { IssueLinks.ProjectParam, IssueLinks.ErrorParam, IssueLinks.ExpiresParam, IssueLinks.SignatureParam }
            .ToDictionary(k => k, k => fields[k] ?? "");

    private static async Task<HttpResponseData> HtmlAsync(HttpRequestData req, HttpStatusCode status, string html)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        // The signed link is in the URL — do not leak it to GitHub / GlitchTip via the Referer header
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("X-Robots-Tag", "noindex");
        response.Headers.Add("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'");
        await response.WriteStringAsync(html);
        return response;
    }
}
