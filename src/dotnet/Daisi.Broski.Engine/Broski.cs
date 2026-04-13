using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Net;

namespace Daisi.Broski.Engine;

/// <summary>
/// One-call high-level entry point: fetch a URL, parse the HTML,
/// optionally run the page's scripts, and hand back the resulting
/// <see cref="LoadedPage"/>. Library callers (the Skimmer's
/// <c>Skimmer.SkimAsync</c>, the Surfer's <c>SkimSession</c>, any
/// future tooling) go through here so the "should we run JS?"
/// decision is a single property toggle, not a 100-line script
/// dispatch loop copied between projects.
///
/// <code>
///   var page = await Broski.LoadAsync(
///       new Uri("https://example.com"),
///       new BroskiOptions { ScriptingEnabled = true });
///   var heading = page.Document.QuerySelector("h1")?.TextContent;
/// </code>
/// </summary>
public static class Broski
{
    /// <summary>Fetch <paramref name="url"/>, parse, and (if
    /// <see cref="BroskiOptions.ScriptingEnabled"/>) run every
    /// blocking + deferred script before returning. The returned
    /// <see cref="LoadedPage"/>'s <c>Document</c> reflects the
    /// post-script DOM.</summary>
    public static async Task<LoadedPage> LoadAsync(
        Uri url,
        BroskiOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        options ??= new BroskiOptions();

        // When the caller didn't supply a full HttpFetcherOptions
        // but did supply an interceptor, fold it in. A
        // caller-provided Fetcher already carries its own
        // interceptor (or none) — don't second-guess that.
        var fetcherOptions = options.Fetcher ?? new HttpFetcherOptions
        {
            Interceptor = options.Interceptor,
        };
        // Use one fetcher for the page + scripts so the cookie jar
        // (which sites use to gate "are you logged in?" responses
        // for chunk URLs) is shared.
        using var fetcher = new HttpFetcher(fetcherOptions);
        using var loader = new PageLoader(fetcher);

        var page = await loader.LoadAsync(url, ct).ConfigureAwait(false);

        if (options.ScriptingEnabled)
        {
            // Synchronous script dispatch. The pipeline is fast
            // enough that wrapping this in a Task.Run isn't
            // worth the context-switch overhead in practice;
            // callers that need the work off the calling thread
            // should wrap their LoadAsync invocation themselves.
            PageScripts.RunAll(page, fetcher, options.StoragePath);
        }

        return page;
    }

    /// <summary>Resolve the default storage directory for the
    /// current platform. Returns a per-user, application-local
    /// path: <c>%LOCALAPPDATA%\daisi-broski\storage</c> on
    /// Windows, <c>~/.local/share/daisi-broski/storage</c>
    /// elsewhere. Never returns <c>null</c> — the default exists
    /// so callers who opt into persistence without choosing a
    /// path get a sensible home.</summary>
    public static string DefaultStoragePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
        return Path.Combine(root, "daisi-broski", "storage");
    }
}

/// <summary>Configuration for <see cref="Broski.LoadAsync"/>. All
/// properties have sensible defaults — the typical caller passes
/// <c>null</c> options.</summary>
public sealed class BroskiOptions
{
    /// <summary>When <c>true</c> (default), run the page's
    /// blocking + deferred scripts via <see cref="PageScripts"/>
    /// before returning. When <c>false</c>, the returned document
    /// is the static HTML as parsed — no DOM mutations from JS.
    /// Static-document mode is faster and avoids the failure modes
    /// that come with running arbitrary site JS.</summary>
    public bool ScriptingEnabled { get; init; } = true;

    /// <summary>HTTP fetcher options (User-Agent, redirects, cookie
    /// jar, timeout, max body size). Pass <c>null</c> to accept
    /// the defaults — a Chromium-shaped UA, 20 redirects, fresh
    /// cookie jar, 30-second timeout, 50 MB body cap.</summary>
    public HttpFetcherOptions? Fetcher { get; init; }

    /// <summary>When set, <c>localStorage</c> writes are persisted
    /// as JSON under this directory, one file per origin
    /// (<c>scheme://host[:port]</c>). When <c>null</c> (default),
    /// <c>localStorage</c> is transient — the phase-3 semantics.
    /// <c>sessionStorage</c> is always transient regardless of
    /// this value. Pass <see cref="Broski.DefaultStoragePath"/>
    /// for a sensible per-user location.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Optional request interceptor wired into the
    /// internal <see cref="HttpFetcherOptions.Interceptor"/>
    /// when <see cref="Fetcher"/> is null. When you provide
    /// your own <see cref="Fetcher"/>, that fetcher's
    /// interceptor wins. Use this for ad blocking, test
    /// scaffolding, or offline replay.</summary>
    public RequestInterceptor? Interceptor { get; init; }
}
