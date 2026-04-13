using System.Net;
using System.Text;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Engine.Tests.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Net;

/// <summary>
/// Phase 5g — host-side request interception. Tests verify
/// the interceptor short-circuits before hitting the wire,
/// returns the synthetic response shape, propagates content
/// type, lets unmatched requests fall through to the real
/// fetcher, and honors the throw-to-fail pattern for
/// blocked requests.
/// </summary>
public class RequestInterceptorTests
{
    [Fact]
    public async Task Interceptor_returning_null_lets_request_proceed_to_network()
    {
        bool serverHit = false;
        using var server = new LocalHttpServer(ctx =>
        {
            serverHit = true;
            LocalHttpServer.WriteText(ctx, "from-network");
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req => null,
        });
        var result = await fetcher.FetchAsync(server.BaseUrl);

        Assert.True(serverHit);
        Assert.Equal("from-network", Encoding.UTF8.GetString(result.Body));
    }

    [Fact]
    public async Task Synthetic_response_short_circuits_the_network_call()
    {
        bool serverHit = false;
        using var server = new LocalHttpServer(ctx =>
        {
            serverHit = true;
            LocalHttpServer.WriteText(ctx, "from-network");
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req => new InterceptedResponse
            {
                Status = 200,
                Body = Encoding.UTF8.GetBytes("from-mock"),
                ContentType = "text/plain",
            },
        });
        var result = await fetcher.FetchAsync(server.BaseUrl);

        Assert.False(serverHit);
        Assert.Equal("from-mock", Encoding.UTF8.GetString(result.Body));
        Assert.Equal(HttpStatusCode.OK, result.Status);
    }

    [Fact]
    public async Task Interceptor_can_return_a_204_for_blocked_url()
    {
        // Pattern for ad / tracker blocking: return a 204 No
        // Content for a URL on a blocklist instead of letting
        // the real request proceed.
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
                req.Url.Host.Contains("ads", StringComparison.Ordinal)
                    ? new InterceptedResponse { Status = 204 }
                    : null,
        });
        var result = await fetcher.FetchAsync(new Uri("http://ads.example.com/pixel"));
        Assert.Equal(204, (int)result.Status);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task Interceptor_throwing_propagates_as_an_exception()
    {
        // "Throw to fail" lets blocked-host policies surface as
        // visible errors in the calling layer instead of silent
        // empty responses.
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req => throw new HttpFetcherException("blocked: " + req.Url),
        });
        var ex = await Assert.ThrowsAsync<HttpFetcherException>(
            () => fetcher.FetchAsync(new Uri("http://blocked.example.com/")));
        Assert.Contains("blocked", ex.Message);
    }

    [Fact]
    public async Task Synthetic_response_carries_explicit_content_type_into_headers()
    {
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req => new InterceptedResponse
            {
                Status = 200,
                Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
                ContentType = "application/json",
            },
        });
        var result = await fetcher.FetchAsync(new Uri("http://mock.example.com/"));
        Assert.Equal("application/json", result.ContentType);
        Assert.True(result.Headers.ContainsKey("content-type"));
        Assert.Equal("application/json", result.Headers["content-type"][0]);
    }

    [Fact]
    public async Task Synthetic_response_inspects_request_url_and_method()
    {
        Uri? observed = null;
        string? observedMethod = null;
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
            {
                observed = req.Url;
                observedMethod = req.Method;
                return new InterceptedResponse { Status = 200 };
            },
        });
        await fetcher.FetchAsync(new Uri("https://example.com/path?q=1"));
        Assert.Equal("https://example.com/path?q=1", observed!.AbsoluteUri);
        Assert.Equal("GET", observedMethod);
    }

    [Fact]
    public async Task Interceptor_via_BroskiOptions_catches_page_load()
    {
        // Library-caller path: pass the interceptor through
        // BroskiOptions and confirm it short-circuits the
        // page navigation. Confirms the wiring from the
        // public API surface.
        var page = await Broski.LoadAsync(
            new Uri("https://blocked.example.com/"),
            new BroskiOptions
            {
                ScriptingEnabled = false,
                Interceptor = req => new InterceptedResponse
                {
                    Status = 200,
                    Body = Encoding.UTF8.GetBytes(
                        "<html><body><h1>mock</h1></body></html>"),
                    ContentType = "text/html",
                },
            });
        Assert.Equal("mock", page.Document.QuerySelector("h1")?.TextContent);
    }

    [Fact]
    public async Task Selective_interception_lets_unmatched_urls_through()
    {
        // The classic test pattern: mock one URL while leaving
        // a co-tenant URL on the same fetcher to hit the real
        // server.
        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, "real");
            return Task.CompletedTask;
        });
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req => req.Url.AbsolutePath == "/mocked"
                ? new InterceptedResponse
                {
                    Body = Encoding.UTF8.GetBytes("mock"),
                    ContentType = "text/plain",
                }
                : null,
        });

        var mocked = await fetcher.FetchAsync(new Uri(server.BaseUrl, "/mocked"));
        Assert.Equal("mock", Encoding.UTF8.GetString(mocked.Body));

        var real = await fetcher.FetchAsync(new Uri(server.BaseUrl, "/real"));
        Assert.Equal("real", Encoding.UTF8.GetString(real.Body));
    }
}
