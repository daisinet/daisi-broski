using System.Diagnostics;
using Daisi.Broski.Skimmer;
using Daisi.Broski.Web.Data;
using Daisi.Broski.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Daisi.Broski.Web.Endpoints;

/// <summary>
/// Minimal-API endpoints for the skim HTTP surface. Grouped
/// under <c>/api/v1</c> so a future v2 can coexist without
/// renaming routes. The skim endpoint requires the ApiKey
/// scheme (X-Api-Key header or <c>Authorization: Bearer db_…</c>);
/// the health probe is open.
/// </summary>
public static class SkimApiEndpoints
{
    public static void MapSkimApi(RouteGroupBuilder group)
    {
        group.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .AllowAnonymous();

        group.MapPost("/skim", HandleSkimPost)
            .WithName("SkimPost")
            .DisableAntiforgery()
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);

        // GET mirror for the POST endpoint — lets users curl / paste
        // a URL directly without building a JSON body. Every field
        // on SkimRequest maps to a query string param of the same
        // name (?url=...&format=md&scripting=true).
        group.MapGet("/skim", HandleSkimGet)
            .WithName("SkimGet")
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);
    }

    private static Task<IResult> HandleSkimPost(
        SkimRequest request,
        HttpContext http,
        SkimService skimmer,
        UsageLogService usage,
        CancellationToken ct)
        => HandleSkim(request, http, skimmer, usage, ct);

    private static Task<IResult> HandleSkimGet(
        [FromQuery] string? url,
        [FromQuery] string? format,
        [FromQuery] bool? scripting,
        HttpContext http,
        SkimService skimmer,
        UsageLogService usage,
        CancellationToken ct)
        => HandleSkim(new SkimRequest(url ?? "", format, scripting), http, skimmer, usage, ct);

    private static async Task<IResult> HandleSkim(
        SkimRequest request,
        HttpContext http,
        SkimService skimmer,
        UsageLogService usage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return Results.BadRequest(new { error = "url is required" });
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url)
            || (url.Scheme != "http" && url.Scheme != "https"))
        {
            return Results.BadRequest(new
            {
                error = "url must be an absolute http(s) URI",
            });
        }

        var sw = Stopwatch.StartNew();
        int status;
        long bytes = 0;
        IResult result;
        try
        {
            var article = await skimmer.SkimAsync(
                url, scripting: request.Scripting ?? true, ct);
            string payload;
            string contentType;
            switch ((request.Format ?? "json").ToLowerInvariant())
            {
                case "md":
                case "markdown":
                    payload = MarkdownFormatter.Format(article);
                    contentType = "text/markdown";
                    break;
                case "html":
                    payload = HtmlFormatter.Format(article);
                    contentType = "text/html";
                    break;
                default:
                    payload = JsonFormatter.Format(article);
                    contentType = "application/json";
                    break;
            }
            bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
            status = StatusCodes.Status200OK;
            result = Results.Text(payload, contentType);
        }
        catch (OperationCanceledException)
        {
            status = StatusCodes.Status499ClientClosedRequest;
            result = Results.StatusCode(status);
        }
        catch (Exception ex)
        {
            status = StatusCodes.Status502BadGateway;
            result = Results.Problem(
                title: "skim failed",
                detail: ex.Message,
                statusCode: status);
        }
        sw.Stop();

        // Record usage after the response is ready — fire-and-
        // forget via Task.Run so the caller sees the response
        // ASAP. UsageLogService swallows DB errors internally.
        var keyId = http.Items["apiKeyId"] as string ?? "";
        var accountId = http.Items["accountId"] as string ?? "";
        var userId = http.Items["userId"] as string ?? "";
        if (!string.IsNullOrEmpty(keyId))
        {
            _ = Task.Run(() => usage.RecordAsync(new ApiKeyUsage
            {
                KeyId = keyId,
                AccountId = accountId,
                UserId = userId,
                UrlHost = url.Host,
                Status = status,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ResponseBytes = bytes,
            }));
        }
        return result;
    }

    public sealed record SkimRequest(
        string Url,
        string? Format = null,
        bool? Scripting = null);
}
