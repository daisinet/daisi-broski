using System.Runtime.Versioning;
using Daisi.Broski;
using Daisi.Broski.Engine.Tests.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Host;

/// <summary>
/// End-to-end tests that spawn the real <c>Daisi.Broski.Sandbox.exe</c>
/// child process, send IPC requests to it through a pair of anonymous
/// pipes, and assert on the responses.
///
/// Windows-only. The sandbox .exe is built as a
/// <c>ReferenceOutputAssembly="false"</c> project reference in the test
/// project so MSBuild produces it before the test runs.
///
/// Pages being navigated to come from <see cref="LocalHttpServer"/> —
/// no public-internet dependency.
/// </summary>
[SupportedOSPlatform("windows")]
public class BrowserSessionIntegrationTests
{
    [Fact]
    public async Task Navigate_and_query_round_trip_through_a_real_sandbox_child()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const string html = """
            <!DOCTYPE html>
            <html>
              <head><meta charset="utf-8"><title>sandbox page</title></head>
              <body>
                <ul>
                  <li class="storylink"><a href="/a">First</a></li>
                  <li class="storylink"><a href="/b">Second</a></li>
                  <li><a href="/c">Other</a></li>
                </ul>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        await using var session = BrowserSession.Create();

        var nav = await session.NavigateAsync(
            server.BaseUrl, ct: TestContext.Current.CancellationToken);

        Assert.Equal(200, nav.Status);
        Assert.Equal(server.BaseUrl.AbsoluteUri, nav.FinalUrl);
        Assert.Equal("sandbox page", nav.Title);
        Assert.Equal("utf-8", nav.Encoding);

        var matches = await session.QuerySelectorAllAsync(
            ".storylink a", TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal("a", matches[0].TagName);
        Assert.Equal("First", matches[0].TextContent);
        Assert.Equal("/a", matches[0].Attributes["href"]);
        Assert.Equal("Second", matches[1].TextContent);
        Assert.Equal("/b", matches[1].Attributes["href"]);
    }

    [Fact]
    public async Task Navigate_to_invalid_url_returns_a_sandbox_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        await using var session = BrowserSession.Create();

        var ex = await Assert.ThrowsAsync<SandboxException>(() =>
            session.NavigateAsync(
                new Uri("http://127.0.0.1:1/"), // nothing listening
                ct: TestContext.Current.CancellationToken));

        // Don't assert on exact error text — transport errors vary by
        // platform. Just make sure the host surfaces it as a SandboxException.
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task QueryAll_before_navigate_returns_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        await using var session = BrowserSession.Create();

        var ex = await Assert.ThrowsAsync<SandboxException>(() =>
            session.QuerySelectorAllAsync("a", TestContext.Current.CancellationToken));

        Assert.Contains("no_page", ex.Message);
    }

    [Fact]
    public async Task Dispose_is_clean_after_navigate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, "<html><body>hi</body></html>");
            return Task.CompletedTask;
        });

        var session = BrowserSession.Create();
        await session.NavigateAsync(server.BaseUrl, ct: TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        // Second dispose must not throw.
        await session.DisposeAsync();
    }

    // ============================================================
    // Phase-4 completion: Run + Skim across the sandbox boundary.
    // ============================================================

    [Fact]
    public async Task Run_executes_scripts_in_the_sandbox_child()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const string html = """
            <!DOCTYPE html>
            <html>
              <head><title>pre-script</title></head>
              <body>
                <p id="x">before</p>
                <script>document.getElementById('x').textContent = 'after-from-script';</script>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        await using var session = BrowserSession.Create();

        // scriptingEnabled: script runs, #x.textContent = 'after-from-script'.
        var resp = await session.RunAsync(
            server.BaseUrl,
            scriptingEnabled: true,
            select: "#x",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(200, resp.Status);
        Assert.Single(resp.Matches);
        Assert.Equal("after-from-script", resp.Matches[0].TextContent);
    }

    [Fact]
    public async Task Run_with_scripting_disabled_returns_static_dom()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const string html = """
            <!DOCTYPE html>
            <html>
              <body>
                <p id="x">before</p>
                <script>document.getElementById('x').textContent = 'after';</script>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        await using var session = BrowserSession.Create();

        var resp = await session.RunAsync(
            server.BaseUrl,
            scriptingEnabled: false,
            select: "#x",
            ct: TestContext.Current.CancellationToken);

        Assert.Single(resp.Matches);
        Assert.Equal("before", resp.Matches[0].TextContent);
    }

    [Fact]
    public async Task Skim_returns_serialized_article_from_sandbox()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const string html = """
            <!DOCTYPE html>
            <html lang="en">
              <head>
                <title>Sandbox Article</title>
                <meta name="author" content="Jane Doe">
              </head>
              <body>
                <article>
                  <h1>Sandbox Article</h1>
                  <p>This article lives in the sandbox for cross-process skim testing.
                     The paragraph needs to be long enough to clear the extractor's
                     scoring threshold so the article block wins the content-root contest.</p>
                  <p>A second paragraph gives the scoring pass extra evidence to pick
                     the article over any other candidate in the document.</p>
                </article>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        await using var session = BrowserSession.Create();

        var resp = await session.SkimAsync(
            server.BaseUrl,
            scriptingEnabled: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Sandbox Article", resp.Title);
        Assert.Equal("Jane Doe", resp.Byline);
        Assert.Equal("en", resp.Lang);
        Assert.Contains("article", resp.PlainText);
        Assert.NotNull(resp.ContentHtml);
        Assert.Contains("Sandbox Article", resp.ContentHtml!);
    }

    [Fact]
    public async Task Skim_returns_empty_article_for_content_free_page()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        // Pages that fail the extractor's threshold (trivial content)
        // should still return successfully — just with empty fields.
        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, "<html><body>hi</body></html>");
            return Task.CompletedTask;
        });

        await using var session = BrowserSession.Create();

        var resp = await session.SkimAsync(
            server.BaseUrl,
            scriptingEnabled: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resp);
        // PlainText may be empty; that's fine.
    }
}
