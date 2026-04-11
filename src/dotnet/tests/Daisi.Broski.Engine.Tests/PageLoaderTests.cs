using System.Net;
using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Tests.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests;

public class PageLoaderTests
{
    [Fact]
    public async Task LoadAsync_parses_a_simple_page_end_to_end()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
              <head><meta charset="utf-8"><title>hi</title></head>
              <body>
                <ul>
                  <li class="storylink">First</li>
                  <li class="storylink">Second</li>
                  <li>Third</li>
                </ul>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html, "text/html; charset=utf-8");
            return Task.CompletedTask;
        });

        using var loader = new PageLoader();
        var page = await loader.LoadAsync(server.BaseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Equal(server.BaseUrl, page.FinalUrl);
        Assert.NotNull(page.Document);
        Assert.NotNull(page.Document.Head);
        Assert.NotNull(page.Document.Body);

        var title = page.Document.QuerySelector("title");
        Assert.NotNull(title);
        Assert.Equal("hi", title!.TextContent);

        var stories = page.Document.QuerySelectorAll(".storylink");
        Assert.Equal(2, stories.Count);
        Assert.Equal("First", stories[0].TextContent);
        Assert.Equal("Second", stories[1].TextContent);
    }

    [Fact]
    public async Task LoadAsync_uses_sniffed_encoding_from_meta_charset_when_header_is_silent()
    {
        // Server serves bytes without a charset in Content-Type, but the
        // document has a <meta charset="utf-8">. The sniffer should pick
        // it up and we should decode non-ASCII content correctly.
        const string html = """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>naïve</title></head>
            <body><p>café</p></body></html>
            """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);

        using var server = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html"; // no charset!
            ctx.Response.ContentLength64 = bytes.LongLength;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return Task.CompletedTask;
        });

        using var loader = new PageLoader();
        var page = await loader.LoadAsync(server.BaseUrl, TestContext.Current.CancellationToken);

        var p = page.Document.QuerySelector("p");
        Assert.Equal("café", p!.TextContent);

        var title = page.Document.QuerySelector("title");
        Assert.Equal("naïve", title!.TextContent);
    }

    [Fact]
    public async Task LoadAsync_follows_redirects_to_final_document()
    {
        using var server = new LocalHttpServer(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/")
                LocalHttpServer.Redirect(ctx, new Uri(ctx.Request.Url!, "/final"));
            else
                LocalHttpServer.WriteText(ctx, "<html><body><h1>Final</h1></body></html>");
            return Task.CompletedTask;
        });

        using var loader = new PageLoader();
        var page = await loader.LoadAsync(server.BaseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri(server.BaseUrl, "/final"), page.FinalUrl);
        Assert.Equal(2, page.RedirectChain.Count);
        Assert.Equal("Final", page.Document.QuerySelector("h1")!.TextContent);
    }
}
