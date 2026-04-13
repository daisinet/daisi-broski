using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Skimmer;

namespace Daisi.Broski.Surfer.Services;

/// <summary>
/// Per-app skim state — current article, history stack, in-flight
/// status. Singleton so the back/forward stack and cached article
/// survive Blazor component remounts (which happen on every
/// route change).
///
/// <para>
/// All mutating operations raise <see cref="StateChanged"/> on the
/// thread that called them. Blazor components subscribe in their
/// <c>OnInitialized</c> and call <c>InvokeAsync(StateHasChanged)</c>
/// on receipt to re-render.
/// </para>
/// </summary>
public sealed class SkimSession
{
    private readonly PageLoader _loader;

    /// <summary>Most recent successful skim, or <c>null</c> before
    /// the first navigation.</summary>
    public ArticleContent? Current { get; private set; }

    /// <summary>The URL the user typed / clicked for the most recent
    /// navigation. May differ from <see cref="ArticleContent.Url"/>
    /// when the server redirected.</summary>
    public string? CurrentRequestUrl { get; private set; }

    /// <summary>Back-stack of previously-visited URLs in arrival
    /// order. Excludes the current page.</summary>
    public List<string> History { get; } = new();

    /// <summary>True while a skim is in flight. Components use this
    /// to show a spinner / disable the address bar.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Last fetch / extraction error message, or <c>null</c>
    /// on success. Cleared at the start of each new navigation.</summary>
    public string? Error { get; private set; }

    public event Action? StateChanged;

    public SkimSession(PageLoader loader)
    {
        _loader = loader;
    }

    /// <summary>Navigate to <paramref name="rawUrl"/> — fetch + parse +
    /// extract + push onto the history stack. Safe to call concurrently;
    /// later calls win. The <see cref="StateChanged"/> event fires
    /// twice: once when loading begins, once when it ends.</summary>
    public async Task NavigateAsync(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return;

        // Auto-prepend https:// when the user types a bare host.
        if (!rawUrl.Contains("://", StringComparison.Ordinal))
        {
            rawUrl = "https://" + rawUrl.Trim();
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != "http" && url.Scheme != "https"))
        {
            Error = $"Not a valid http(s) URL: {rawUrl}";
            StateChanged?.Invoke();
            return;
        }

        // Push the previous URL onto history before swapping.
        if (CurrentRequestUrl is not null && CurrentRequestUrl != rawUrl)
        {
            History.Add(CurrentRequestUrl);
        }

        CurrentRequestUrl = rawUrl;
        IsLoading = true;
        Error = null;
        StateChanged?.Invoke();

        try
        {
            var page = await _loader.LoadAsync(url);

            // SPAs (Next.js, Nuxt, the daisi.ai Learn page) hide
            // their interesting content behind script execution.
            // Run the page's blocking + deferred scripts before
            // extraction so the content root the Skimmer picks
            // reflects what the user would actually see in a real
            // browser, not the static SSR shell.
            try
            {
                RunPageScripts(page);
            }
            catch
            {
                // Script-runner failures are non-fatal — extract
                // from whatever DOM we have.
            }

            Current = ContentExtractor.Extract(page.Document, page.FinalUrl);
            CurrentRequestUrl = page.FinalUrl.AbsoluteUri;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Execute every blocking + deferred <c>&lt;script&gt;</c>
    /// in <paramref name="page"/>'s document, in source order, against
    /// a fresh <see cref="JsEngine"/>. Mirrors what the
    /// <c>daisi-broski run</c> CLI command does — stripped to the
    /// essentials (no per-script error tracking, no async-script
    /// scheduling). Errors in any one script are swallowed so a
    /// single broken chunk doesn't prevent later scripts (and the
    /// final extraction) from running.</summary>
    private static void RunPageScripts(LoadedPage page)
    {
        var doc = page.Document;
        var engine = new JsEngine();
        engine.AttachDocument(doc, page.FinalUrl);

        using var fetcher = new HttpFetcher(new HttpFetcherOptions());

        // Two-pass: blocking first, then defer (browser order).
        var deferred = new List<Daisi.Broski.Engine.Dom.Element>();
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            var type = script.GetAttribute("type") ?? "text/javascript";
            if (type is not ("" or "text/javascript" or "application/javascript"))
                continue;
            if (script.HasAttribute("async")) continue;
            if (script.HasAttribute("defer")) { deferred.Add(script); continue; }
            RunOneScript(engine, fetcher, page.FinalUrl, script);
        }
        foreach (var script in deferred)
        {
            RunOneScript(engine, fetcher, page.FinalUrl, script);
        }
    }

    private static void RunOneScript(
        JsEngine engine,
        HttpFetcher fetcher,
        Uri pageUrl,
        Daisi.Broski.Engine.Dom.Element script)
    {
        string source;
        if (script.HasAttribute("src"))
        {
            var src = script.GetAttribute("src")!;
            Uri uri;
            try { uri = new Uri(pageUrl, src); }
            catch { return; }
            try
            {
                var result = fetcher.FetchAsync(uri).GetAwaiter().GetResult();
                source = System.Text.Encoding.UTF8.GetString(result.Body);
            }
            catch { return; }
        }
        else
        {
            source = script.TextContent;
            if (string.IsNullOrWhiteSpace(source)) return;
        }

        try
        {
            engine.ExecutingScript = script;
            engine.RunScript(source);
        }
        catch
        {
            // Single-script failures are non-fatal — keep going.
        }
        finally
        {
            engine.ExecutingScript = null;
        }
    }

    /// <summary>Pop the most recent history entry and re-navigate to
    /// it. No-op when the history is empty.</summary>
    public async Task GoBackAsync()
    {
        if (History.Count == 0) return;
        var last = History[^1];
        History.RemoveAt(History.Count - 1);
        // Avoid pushing the current page (which NavigateAsync would
        // do) — we want a true "back".
        var current = CurrentRequestUrl;
        CurrentRequestUrl = null;
        try
        {
            await NavigateAsync(last);
        }
        finally
        {
            // If the back-nav failed, don't lose the original.
            if (Current is null && current is not null)
            {
                CurrentRequestUrl = current;
                StateChanged?.Invoke();
            }
        }
    }
}
