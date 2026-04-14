using System.Text;
using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests;

/// <summary>
/// Phase 6av — ES-module + importmap support in
/// <see cref="PageScripts"/>. Previously <c>type="module"</c>
/// and <c>type="importmap"</c> scripts were silently
/// skipped; Blazor / Vite / Rollup builds that relied on
/// fingerprinted module URLs via the importmap therefore
/// never bootstrapped. These tests use a fake network
/// (via <see cref="HttpFetcherOptions.Interceptor"/>) so
/// the suite stays offline.
/// </summary>
public class PageScriptsModuleTests
{
    private static LoadedPage ParsePage(string html, Uri url)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        return new LoadedPage
        {
            Document = doc,
            Html = html,
            Body = Encoding.UTF8.GetBytes(html),
            ContentType = "text/html",
            Status = System.Net.HttpStatusCode.OK,
            RequestUrl = url,
            FinalUrl = url,
            RedirectChain = Array.Empty<Uri>(),
            Encoding = Encoding.UTF8,
        };
    }

    [Fact]
    public void Importmap_rewrites_bare_specifier_before_module_fetch()
    {
        // Page has an importmap that remaps "./lib.js" to
        // the fingerprinted version. A later
        // type="module" loads "./lib.js" — we verify the
        // fetcher was asked for the fingerprinted URL.
        var requestedUrls = new List<string>();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
            {
                requestedUrls.Add(req.Url.AbsoluteUri);
                if (req.Url.AbsoluteUri.EndsWith("lib.abc123.js"))
                {
                    return new InterceptedResponse
                    {
                        Status = 200,
                        Body = Encoding.UTF8.GetBytes(
                            "globalThis.__fingerprintedRan = true;"),
                        ContentType = "application/javascript",
                    };
                }
                return new InterceptedResponse { Status = 404, Body = Array.Empty<byte>() };
            },
        });

        var html = """
            <html><head>
            <script type="importmap">
            {"imports": {"./lib.js": "./lib.abc123.js"}}
            </script>
            <script type="module" src="./lib.js"></script>
            </head><body></body></html>
            """;
        var page = ParsePage(html, new Uri("https://test.local/index.html"));
        var result = PageScripts.RunAll(page, fetcher);

        Assert.Equal(0, result.Errored);
        Assert.Contains(requestedUrls, u => u.EndsWith("lib.abc123.js"));
        // The non-fingerprinted URL should NOT have been hit
        // — importmap rewrite wins before the fetch goes out.
        Assert.DoesNotContain(requestedUrls, u => u == "https://test.local/lib.js");
    }

    [Fact]
    public void Module_script_runs_and_sees_imported_bindings()
    {
        // Module A imports a named export from module B via
        // bare specifier "lib" (mapped to lib.js through the
        // importmap). Module A stores the sum on globalThis
        // so the test can verify after the run.
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
            {
                var path = req.Url.AbsolutePath;
                if (path == "/lib.js")
                {
                    return new InterceptedResponse
                    {
                        Status = 200,
                        Body = Encoding.UTF8.GetBytes(
                            "export const x = 2; export const y = 3;"),
                        ContentType = "application/javascript",
                    };
                }
                if (path == "/main.js")
                {
                    return new InterceptedResponse
                    {
                        Status = 200,
                        Body = Encoding.UTF8.GetBytes(
                            "import {x, y} from 'lib'; globalThis.__sum = x + y;"),
                        ContentType = "application/javascript",
                    };
                }
                return new InterceptedResponse { Status = 404, Body = Array.Empty<byte>() };
            },
        });

        var html = """
            <html><head>
            <script type="importmap">{"imports": {"lib": "./lib.js"}}</script>
            <script type="module" src="./main.js"></script>
            </head><body></body></html>
            """;
        var page = ParsePage(html, new Uri("https://test.local/"));
        var engine = new JsEngine();
        engine.AttachDocument(page.Document, page.FinalUrl);
        var result = PageScripts.RunAllAgainst(engine, page.Document, page.FinalUrl, fetcher);

        Assert.Equal(0, result.Errored);
        Assert.Equal(5.0, engine.Globals["__sum"]);
    }

    [Fact]
    public void Importmap_prefix_match_resolves_subtree()
    {
        // Importmap maps "./_framework/" prefix to the site-
        // root /framework-v2/. A module loads
        // "./_framework/runtime.js" — should fetch from
        // /framework-v2/runtime.js.
        var requested = new List<string>();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
            {
                requested.Add(req.Url.AbsolutePath);
                if (req.Url.AbsolutePath == "/framework-v2/runtime.js")
                {
                    return new InterceptedResponse
                    {
                        Status = 200,
                        Body = Encoding.UTF8.GetBytes("globalThis.__ran = 1;"),
                        ContentType = "application/javascript",
                    };
                }
                return new InterceptedResponse { Status = 404, Body = Array.Empty<byte>() };
            },
        });

        var html = """
            <html><head>
            <script type="importmap">
            {"imports": {"./_framework/": "/framework-v2/"}}
            </script>
            <script type="module" src="./_framework/runtime.js"></script>
            </head><body></body></html>
            """;
        var page = ParsePage(html, new Uri("https://test.local/"));
        var engine = new JsEngine();
        engine.AttachDocument(page.Document, page.FinalUrl);
        var result = PageScripts.RunAllAgainst(engine, page.Document, page.FinalUrl, fetcher);

        Assert.Equal(0, result.Errored);
        Assert.Contains("/framework-v2/runtime.js", requested);
        // And the code inside the resolved module ran.
        Assert.Equal(1.0, engine.Globals["__ran"]);
    }

    [Fact]
    public void Dynamic_import_routes_through_importmap()
    {
        // A classic script calls `import('lib')` at runtime
        // — verifies dynamic import() also goes through the
        // same module resolver we installed.
        using var fetcher = new HttpFetcher(new HttpFetcherOptions
        {
            Interceptor = req =>
            {
                if (req.Url.AbsolutePath == "/lib.js")
                {
                    return new InterceptedResponse
                    {
                        Status = 200,
                        Body = Encoding.UTF8.GetBytes("export const magic = 42;"),
                        ContentType = "application/javascript",
                    };
                }
                return new InterceptedResponse { Status = 404, Body = Array.Empty<byte>() };
            },
        });

        var html = """
            <html><head>
            <script type="importmap">{"imports": {"lib": "./lib.js"}}</script>
            <script>
              import('lib').then(m => { globalThis.__magic = m.magic; });
            </script>
            </head><body></body></html>
            """;
        var page = ParsePage(html, new Uri("https://test.local/"));
        var engine = new JsEngine();
        engine.AttachDocument(page.Document, page.FinalUrl);
        PageScripts.RunAllAgainst(engine, page.Document, page.FinalUrl, fetcher);
        engine.DrainEventLoop();

        Assert.Equal(42.0, engine.Globals["__magic"]);
    }
}
