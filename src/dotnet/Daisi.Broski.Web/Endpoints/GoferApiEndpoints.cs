using System.Diagnostics;
using Daisi.Broski.Gofer;
using Daisi.Broski.Gofer.Search;
using Daisi.Broski.Web.Data;
using Daisi.Broski.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Daisi.Broski.Web.Endpoints;

/// <summary>
/// Minimal-API endpoints for Gofer — the parallel crawler and
/// multi-source search. Three routes under <c>/api/v1/gofer</c>:
///
/// <list type="bullet">
/// <item><c>POST /search</c> — query + sources → hit list (no crawl).</item>
/// <item><c>POST /crawl</c> — seed URLs → crawled articles (no search).</item>
/// <item><c>POST /research</c> — query → hits → crawled articles. The headline
///   endpoint for LLM research: one call, search + crawl, markdown back.</item>
/// </list>
///
/// All three require an API key (same scheme as <c>/skim</c>) and
/// log one usage entry per call regardless of how many pages the
/// call processed — billing is per-request, not per-page.
/// </summary>
public static class GoferApiEndpoints
{
    public static void MapGoferApi(RouteGroupBuilder group)
    {
        // Every endpoint has both a POST (JSON body) and a GET
        // (query-string params) form so callers can pick whichever
        // fits their tooling. Array params on the GET form use
        // repeated keys — e.g. ?seeds=a&seeds=b&selectors=.article.
        group.MapPost("/search", HandleSearchPost)
            .WithName("GoferSearchPost").DisableAntiforgery()
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);
        group.MapGet("/search", HandleSearchGet)
            .WithName("GoferSearchGet")
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);

        group.MapPost("/crawl", HandleCrawlPost)
            .WithName("GoferCrawlPost").DisableAntiforgery()
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);
        group.MapGet("/crawl", HandleCrawlGet)
            .WithName("GoferCrawlGet")
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);

        group.MapPost("/research", HandleResearchPost)
            .WithName("GoferResearchPost").DisableAntiforgery()
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);
        group.MapGet("/research", HandleResearchGet)
            .WithName("GoferResearchGet")
            .RequireAuthorization(ApiKeyAuthHandler.SchemeName);
    }

    // ============================================================
    // /search — hits only
    // ============================================================

    private static Task<IResult> HandleSearchPost(
        GoferSearchRequest request,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleSearch(request, http, usage, ct);

    private static Task<IResult> HandleSearchGet(
        [FromQuery] string? query,
        [FromQuery] SearchSource? sources,
        [FromQuery(Name = "perProviderLimit")] int? perProviderLimit,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleSearch(
            new GoferSearchRequest(query ?? "", sources, perProviderLimit),
            http, usage, ct);

    private static async Task<IResult> HandleSearch(
        GoferSearchRequest request,
        HttpContext http,
        UsageLogService usage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required" });

        var sources = request.Sources ?? SearchSource.Scholarly | SearchSource.Community;
        if (sources == SearchSource.None)
            return Results.BadRequest(new { error = "at least one source must be selected" });

        var sw = Stopwatch.StartNew();
        IReadOnlyList<SearchResult> hits;
        int status = StatusCodes.Status200OK;
        try
        {
            var pipeline = SearchPipeline.FromSources(sources);
            hits = await pipeline.SearchAsync(
                request.Query,
                perProviderLimit: Math.Clamp(request.PerProviderLimit ?? 10, 1, 50),
                ct);
        }
        catch (OperationCanceledException)
        {
            status = StatusCodes.Status499ClientClosedRequest;
            return Results.StatusCode(status);
        }
        catch (Exception ex)
        {
            status = StatusCodes.Status502BadGateway;
            LogUsage(http, usage, status, sw.ElapsedMilliseconds, bytes: 0, host: "gofer/search");
            return Results.Problem(
                title: "search failed",
                detail: ex.Message,
                statusCode: status);
        }
        sw.Stop();

        var payload = new { query = request.Query, sources = sources.ToString(), count = hits.Count, hits };
        LogUsage(http, usage, status, sw.ElapsedMilliseconds,
            bytes: EstimateBytes(hits.Count, avgPerItem: 512),
            host: "gofer/search");
        return Results.Json(payload);
    }

    // ============================================================
    // /crawl — seeds only, no search
    // ============================================================

    private static Task<IResult> HandleCrawlPost(
        GoferCrawlRequest request,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleCrawl(request, http, usage, ct);

    private static Task<IResult> HandleCrawlGet(
        [FromQuery] string[]? seeds,
        [FromQuery(Name = "maxDepth")] int? maxDepth,
        [FromQuery(Name = "maxPages")] int? maxPages,
        [FromQuery(Name = "degreeOfParallelism")] int? dop,
        [FromQuery(Name = "stayOnHost")] bool? stayOnHost,
        [FromQuery(Name = "followLinks")] bool? followLinks,
        [FromQuery] string[]? selectors,
        // Repeated ?header=Name:Value pairs. Colon splits
        // name from value; further colons become part of value.
        [FromQuery(Name = "header")] string[]? headerStrings,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleCrawl(
            new GoferCrawlRequest(
                Seeds: seeds ?? [],
                MaxDepth: maxDepth,
                MaxPages: maxPages,
                DegreeOfParallelism: dop,
                StayOnHost: stayOnHost,
                FollowLinks: followLinks,
                Selectors: selectors,
                Headers: ParseHeaderStrings(headerStrings)),
            http, usage, ct);

    private static async Task<IResult> HandleCrawl(
        GoferCrawlRequest request,
        HttpContext http,
        UsageLogService usage,
        CancellationToken ct)
    {
        if (request.Seeds is null || request.Seeds.Length == 0)
            return Results.BadRequest(new { error = "seeds[] is required" });

        var seeds = new List<Uri>(request.Seeds.Length);
        foreach (var raw in request.Seeds)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)
                || (u.Scheme != "http" && u.Scheme != "https"))
            {
                return Results.BadRequest(new { error = $"invalid seed url: {raw}" });
            }
            seeds.Add(u);
        }

        var options = BuildCrawlOptions(
            request.MaxDepth, request.MaxPages, request.DegreeOfParallelism,
            request.StayOnHost, request.FollowLinks, request.Selectors, request.Headers);

        var sw = Stopwatch.StartNew();
        var results = new List<GoferResult>();
        int status = StatusCodes.Status200OK;
        try
        {
            await using var gofer = new GoferCrawler(options);
            gofer.PageScraped += (_, e) =>
            {
                lock (results) results.Add(e.Result);
            };
            await gofer.RunAsync(seeds, ct);
        }
        catch (OperationCanceledException)
        {
            status = StatusCodes.Status499ClientClosedRequest;
            return Results.StatusCode(status);
        }
        catch (Exception ex)
        {
            status = StatusCodes.Status502BadGateway;
            LogUsage(http, usage, status, sw.ElapsedMilliseconds, bytes: 0, host: "gofer/crawl");
            return Results.Problem(
                title: "crawl failed",
                detail: ex.Message,
                statusCode: status);
        }
        sw.Stop();

        LogUsage(http, usage, status, sw.ElapsedMilliseconds,
            bytes: results.Sum(r => r.Bytes),
            host: "gofer/crawl");
        return Results.Json(new
        {
            seeds = request.Seeds,
            count = results.Count,
            results,
        });
    }

    // ============================================================
    // /research — the full search + crawl pipeline
    // ============================================================

    private static Task<IResult> HandleResearchPost(
        GoferResearchRequest request,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleResearch(request, http, usage, ct);

    private static Task<IResult> HandleResearchGet(
        [FromQuery] string? query,
        [FromQuery] SearchSource? sources,
        [FromQuery(Name = "perProviderLimit")] int? perProviderLimit,
        [FromQuery(Name = "maxCrawled")] int? maxCrawled,
        [FromQuery(Name = "maxPages")] int? maxPages,
        [FromQuery(Name = "degreeOfParallelism")] int? dop,
        [FromQuery] string[]? selectors,
        [FromQuery(Name = "header")] string[]? headerStrings,
        HttpContext http, UsageLogService usage, CancellationToken ct)
        => HandleResearch(
            new GoferResearchRequest(
                Query: query ?? "",
                Sources: sources,
                PerProviderLimit: perProviderLimit,
                MaxCrawled: maxCrawled,
                MaxPages: maxPages,
                DegreeOfParallelism: dop,
                Selectors: selectors,
                Headers: ParseHeaderStrings(headerStrings)),
            http, usage, ct);

    private static async Task<IResult> HandleResearch(
        GoferResearchRequest request,
        HttpContext http,
        UsageLogService usage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required" });

        var sources = request.Sources ?? SearchSource.Scholarly | SearchSource.Community;
        if (sources == SearchSource.None)
            return Results.BadRequest(new { error = "at least one source must be selected" });

        var options = BuildCrawlOptions(
            maxDepth: 0,
            request.MaxPages,
            request.DegreeOfParallelism,
            stayOnHost: false,
            followLinks: false,
            request.Selectors,
            request.Headers);

        var sw = Stopwatch.StartNew();
        IReadOnlyList<SearchAndCrawlResult> pairs;
        int status = StatusCodes.Status200OK;
        try
        {
            var pipeline = SearchPipeline.FromSources(sources);
            pairs = await pipeline.SearchAndCrawlAsync(
                request.Query,
                options,
                perProviderLimit: Math.Clamp(request.PerProviderLimit ?? 10, 1, 50),
                maxCrawled: Math.Clamp(request.MaxCrawled ?? 20, 1, 200),
                ct);
        }
        catch (OperationCanceledException)
        {
            status = StatusCodes.Status499ClientClosedRequest;
            return Results.StatusCode(status);
        }
        catch (Exception ex)
        {
            status = StatusCodes.Status502BadGateway;
            LogUsage(http, usage, status, sw.ElapsedMilliseconds, bytes: 0, host: "gofer/research");
            return Results.Problem(
                title: "research failed",
                detail: ex.Message,
                statusCode: status);
        }
        sw.Stop();

        LogUsage(http, usage, status, sw.ElapsedMilliseconds,
            bytes: pairs.Sum(p => p.Crawl.Bytes),
            host: "gofer/research");
        return Results.Json(new
        {
            query = request.Query,
            sources = sources.ToString(),
            count = pairs.Count,
            results = pairs,
        });
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static GoferOptions BuildCrawlOptions(
        int? maxDepth, int? maxPages, int? dop,
        bool? stayOnHost, bool? followLinks,
        string[]? selectors,
        Dictionary<string, string>? headers)
    {
        var opts = new GoferOptions
        {
            // Conservative server-side caps so a single API call
            // can't fan out indefinitely. Caller-supplied values
            // are clamped to the API's ceiling.
            MaxDepth = Math.Clamp(maxDepth ?? 0, 0, 5),
            MaxPages = Math.Clamp(maxPages ?? 50, 1, 500),
            DegreeOfParallelism = Math.Clamp(dop ?? 8, 1, 32),
            StayOnHost = stayOnHost ?? false,
            FollowLinks = followLinks ?? false,
            PerHostDelay = TimeSpan.FromMilliseconds(100),
            RequestTimeout = TimeSpan.FromSeconds(20),
        };
        if (selectors is { Length: > 0 })
        {
            foreach (var s in selectors) opts.Selectors.Add(s);
        }
        if (headers is { Count: > 0 })
        {
            // Caller-supplied headers replace the provider default
            // User-Agent / Accept if they collide. Lets API users
            // identify themselves or pass an Authorization bearer
            // for pages that require one.
            foreach (var kv in headers) opts.Headers[kv.Key] = kv.Value;
        }
        return opts;
    }

    private static void LogUsage(
        HttpContext http, UsageLogService usage,
        int status, long durationMs, long bytes, string host)
    {
        var keyId = http.Items["apiKeyId"] as string ?? "";
        var accountId = http.Items["accountId"] as string ?? "";
        var userId = http.Items["userId"] as string ?? "";
        if (string.IsNullOrEmpty(keyId)) return;
        _ = Task.Run(() => usage.RecordAsync(new ApiKeyUsage
        {
            KeyId = keyId,
            AccountId = accountId,
            UserId = userId,
            UrlHost = host,
            Status = status,
            DurationMs = (int)durationMs,
            ResponseBytes = bytes,
        }));
    }

    /// <summary>Rough response-size estimate for usage accounting.
    /// Counts only serialized-hit bytes; the Json envelope adds a
    /// fixed ~100-byte wrapper we don't bother tracking.</summary>
    private static long EstimateBytes(int items, int avgPerItem) => items * (long)avgPerItem;

    /// <summary>Parse repeated <c>?header=Name:Value</c> query
    /// params into a header dictionary. The first colon splits
    /// name from value; further colons remain in the value
    /// (useful for URL-shaped header values that contain colons).</summary>
    private static Dictionary<string, string>? ParseHeaderStrings(string[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw)
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0) continue;
            var name = pair[..colon].Trim();
            var value = pair[(colon + 1)..].TrimStart();
            if (!string.IsNullOrEmpty(name))
                dict[name] = value;
        }
        return dict.Count > 0 ? dict : null;
    }

    // ============================================================
    // Request DTOs
    // ============================================================

    public sealed record GoferSearchRequest(
        string Query,
        SearchSource? Sources = null,
        int? PerProviderLimit = null);

    public sealed record GoferCrawlRequest(
        string[] Seeds,
        int? MaxDepth = null,
        int? MaxPages = null,
        int? DegreeOfParallelism = null,
        bool? StayOnHost = null,
        bool? FollowLinks = null,
        string[]? Selectors = null,
        Dictionary<string, string>? Headers = null);

    public sealed record GoferResearchRequest(
        string Query,
        SearchSource? Sources = null,
        int? PerProviderLimit = null,
        int? MaxCrawled = null,
        int? MaxPages = null,
        int? DegreeOfParallelism = null,
        string[]? Selectors = null,
        Dictionary<string, string>? Headers = null);
}
