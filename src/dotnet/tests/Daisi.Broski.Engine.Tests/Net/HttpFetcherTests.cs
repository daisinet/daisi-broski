using System.Net;
using System.Text;
using Daisi.Broski.Engine.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Net;

public class HttpFetcherTests
{
    [Fact]
    public async Task FetchAsync_returns_body_and_content_type_on_200()
    {
        const string html = "<!doctype html><title>hi</title>";

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher();
        var result = await fetcher.FetchAsync(server.BaseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(html, Encoding.UTF8.GetString(result.Body));
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Equal(server.BaseUrl, result.FinalUrl);
        Assert.Single(result.RedirectChain);
    }

    [Fact]
    public async Task FetchAsync_follows_redirect_chain_and_reports_every_hop()
    {
        using var server = new LocalHttpServer(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/")
                LocalHttpServer.Redirect(ctx, new Uri(ctx.Request.Url!, "/step-1"));
            else if (path == "/step-1")
                LocalHttpServer.Redirect(ctx, new Uri(ctx.Request.Url!, "/final"));
            else
                LocalHttpServer.WriteText(ctx, "arrived", "text/plain");
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher();
        var result = await fetcher.FetchAsync(server.BaseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("arrived", Encoding.UTF8.GetString(result.Body));
        Assert.Equal(new Uri(server.BaseUrl, "/final"), result.FinalUrl);
        Assert.Equal(3, result.RedirectChain.Count);
        Assert.Equal(server.BaseUrl, result.RedirectChain[0]);
        Assert.Equal(new Uri(server.BaseUrl, "/step-1"), result.RedirectChain[1]);
        Assert.Equal(new Uri(server.BaseUrl, "/final"), result.RedirectChain[2]);
    }

    [Fact]
    public async Task FetchAsync_throws_when_redirect_cap_is_exceeded()
    {
        // Bounce forever. Cap should kick in at 3.
        using var server = new LocalHttpServer(ctx =>
        {
            var n = int.Parse(ctx.Request.Url!.AbsolutePath.TrimStart('/').Length == 0
                ? "0"
                : ctx.Request.Url!.AbsolutePath.TrimStart('/'));
            LocalHttpServer.Redirect(ctx, new Uri(ctx.Request.Url!, "/" + (n + 1)));
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher(new HttpFetcherOptions { MaxRedirects = 3 });

        var ex = await Assert.ThrowsAsync<HttpFetcherException>(() =>
            fetcher.FetchAsync(server.BaseUrl, TestContext.Current.CancellationToken));

        Assert.Contains("redirect cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_enforces_max_response_bytes_on_oversize_content_length()
    {
        // Server advertises a 10-byte body; cap is 5.
        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, "0123456789", "application/octet-stream");
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher(new HttpFetcherOptions { MaxResponseBytes = 5 });

        await Assert.ThrowsAsync<HttpFetcherException>(() =>
            fetcher.FetchAsync(server.BaseUrl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchAsync_round_trips_a_cookie_across_two_calls()
    {
        // First request sets a cookie. Second request should include it.
        using var server = new LocalHttpServer(ctx =>
        {
            var cookieHeader = ctx.Request.Headers["Cookie"];
            if (ctx.Request.Url!.AbsolutePath == "/set")
            {
                ctx.Response.Headers["Set-Cookie"] = "session=abc123; Path=/";
                LocalHttpServer.WriteText(ctx, "set", "text/plain");
            }
            else
            {
                LocalHttpServer.WriteText(ctx, cookieHeader ?? "<none>", "text/plain");
            }
            return Task.CompletedTask;
        });

        using var fetcher = new HttpFetcher();
        var ct = TestContext.Current.CancellationToken;
        await fetcher.FetchAsync(new Uri(server.BaseUrl, "/set"), ct);
        var check = await fetcher.FetchAsync(new Uri(server.BaseUrl, "/check"), ct);

        Assert.Equal("session=abc123", Encoding.UTF8.GetString(check.Body));
    }
}
