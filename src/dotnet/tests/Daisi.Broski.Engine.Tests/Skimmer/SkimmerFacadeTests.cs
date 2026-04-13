using System.Net;
using System.Text;
using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Skimmer;
using Xunit;
// Same alias as the production code — Daisi.Broski.Skimmer.Skimmer
// (the class) collides with the Daisi.Broski.Skimmer namespace.
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;
using EngineBroski = Daisi.Broski.Engine.Broski;

namespace Daisi.Broski.Engine.Tests.Skimmer;

/// <summary>
/// End-to-end tests for the high-level facades
/// (<c>Broski.LoadAsync</c>, <c>Skimmer.SkimAsync</c>) covering the
/// <c>ScriptingEnabled</c> toggle. Uses an embedded
/// <see cref="HttpListener"/> as the test server so we can serve
/// fixture HTML + JS without depending on the network or any
/// external site.
/// </summary>
public class SkimmerFacadeTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly Dictionary<string, (string body, string contentType)> _routes = new();
    private readonly Task _listenerLoop;
    private readonly CancellationTokenSource _cts = new();

    public SkimmerFacadeTests()
    {
        // Bind to a random port. Two attempts in case of a port
        // race against another test in the same suite.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int port = Random.Shared.Next(40000, 60000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                _baseUrl = $"http://127.0.0.1:{port}";
                _listenerLoop = Task.Run(LoopAsync);
                return;
            }
            catch (HttpListenerException)
            {
                _listener.Close();
            }
        }
        throw new InvalidOperationException("Could not bind a test HttpListener.");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            var path = ctx.Request.Url!.AbsolutePath;
            if (_routes.TryGetValue(path, out var route))
            {
                ctx.Response.ContentType = route.contentType;
                var bytes = Encoding.UTF8.GetBytes(route.body);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private void Serve(string path, string body, string contentType = "text/html; charset=utf-8")
        => _routes[path] = (body, contentType);

    // ========================================================
    // Skimmer.SkimAsync
    // ========================================================

    [Fact]
    public async Task SkimAsync_with_ScriptingEnabled_runs_scripts()
    {
        Serve("/", """
            <!doctype html><html><body>
              <article><p id="x">before</p></article>
              <script>document.getElementById('x').textContent = 'after a long enough story';</script>
            </body></html>
            """);

        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/"),
            new SkimmerOptions { ScriptingEnabled = true });

        Assert.Contains("after", article.PlainText);
        Assert.DoesNotContain("before", article.PlainText);
    }

    [Fact]
    public async Task SkimAsync_with_ScriptingDisabled_skips_scripts()
    {
        Serve("/", """
            <!doctype html><html><body>
              <article><p id="x">before but the original SSR text needs to be long enough to score above the threshold for content extraction.</p></article>
              <script>document.getElementById('x').textContent = 'after';</script>
            </body></html>
            """);

        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/"),
            new SkimmerOptions { ScriptingEnabled = false });

        Assert.Contains("before", article.PlainText);
        Assert.DoesNotContain("after", article.PlainText);
    }

    [Fact]
    public async Task SkimAsync_default_options_runs_scripts()
    {
        // Default options should match ScriptingEnabled=true.
        Serve("/", """
            <!doctype html><html><body>
              <article><p id="x">static content placeholder text</p></article>
              <script>document.getElementById('x').textContent = 'dynamic content rendered by JS for the test fixture body';</script>
            </body></html>
            """);

        var article = await SkimmerApi.SkimAsync(new Uri(_baseUrl + "/"));

        Assert.Contains("dynamic content", article.PlainText);
    }

    [Fact]
    public async Task SkimAsync_returns_extracted_metadata()
    {
        Serve("/article", """
            <!doctype html><html lang="en"><head>
              <title>Test Article</title>
              <meta name="author" content="Jane Doe">
              <meta property="article:published_time" content="2026-04-13T12:00:00Z">
            </head><body><article>
              <p>Body content. Body content. Body content. Body content. Body content.</p>
            </article></body></html>
            """);

        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/article"),
            new SkimmerOptions { ScriptingEnabled = false });

        Assert.Equal("Test Article", article.Title);
        Assert.Equal("Jane Doe", article.Byline);
        Assert.Equal("2026-04-13T12:00:00Z", article.PublishedAt);
        Assert.Equal("en", article.Lang);
    }

    // ========================================================
    // Broski.LoadAsync
    // ========================================================

    [Fact]
    public async Task BroskiLoadAsync_with_scripts_executes_them()
    {
        Serve("/", """
            <!doctype html><html><body>
              <p id="x">unset</p>
              <script>document.getElementById('x').textContent = 'set-by-script';</script>
            </body></html>
            """);

        var page = await EngineBroski.LoadAsync(
            new Uri(_baseUrl + "/"),
            new BroskiOptions { ScriptingEnabled = true });

        var p = page.Document.QuerySelector("#x");
        Assert.Equal("set-by-script", p?.TextContent);
    }

    [Fact]
    public async Task BroskiLoadAsync_without_scripts_returns_static_dom()
    {
        Serve("/", """
            <!doctype html><html><body>
              <p id="x">unset</p>
              <script>document.getElementById('x').textContent = 'set-by-script';</script>
            </body></html>
            """);

        var page = await EngineBroski.LoadAsync(
            new Uri(_baseUrl + "/"),
            new BroskiOptions { ScriptingEnabled = false });

        var p = page.Document.QuerySelector("#x");
        Assert.Equal("unset", p?.TextContent);
    }

    [Fact]
    public async Task BroskiLoadAsync_runs_external_scripts()
    {
        Serve("/", $$"""
            <!doctype html><html><body>
              <p id="x">unset</p>
              <script src="{{_baseUrl}}/extra.js"></script>
            </body></html>
            """);
        Serve("/extra.js", "document.getElementById('x').textContent = 'from-external';",
              "application/javascript");

        var page = await EngineBroski.LoadAsync(
            new Uri(_baseUrl + "/"),
            new BroskiOptions { ScriptingEnabled = true });

        var p = page.Document.QuerySelector("#x");
        Assert.Equal("from-external", p?.TextContent);
    }

    [Fact]
    public async Task BroskiLoadAsync_continues_after_one_script_throws()
    {
        Serve("/", """
            <!doctype html><html><body>
              <p id="a">a</p>
              <p id="b">b</p>
              <script>throw new Error('boom');</script>
              <script>document.getElementById('b').textContent = 'survived';</script>
            </body></html>
            """);

        var page = await EngineBroski.LoadAsync(
            new Uri(_baseUrl + "/"),
            new BroskiOptions { ScriptingEnabled = true });

        Assert.Equal("survived", page.Document.QuerySelector("#b")?.TextContent);
    }

    // ========================================================
    // PageScripts (lower-level)
    // ========================================================

    [Fact]
    public async Task PageScripts_RunAll_reports_counts()
    {
        Serve("/", """
            <!doctype html><html><body>
              <script>var x = 1;</script>
              <script type="application/json">{"a":1}</script>
              <script>var y = 2;</script>
            </body></html>
            """);

        using var loader = new PageLoader();
        var page = await loader.LoadAsync(new Uri(_baseUrl + "/"));
        var result = PageScripts.RunAll(page);

        Assert.Equal(3, result.TotalScripts);
        Assert.Equal(1, result.SkippedByType);
        Assert.Equal(2, result.Ran);
        Assert.Equal(0, result.Errored);
    }
}
