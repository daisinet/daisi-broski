using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Layout;
using Daisi.Broski.Engine.Paint;
using Daisi.Broski.Skimmer;
// Two identifier-collision aliases: the Broski namespace
// (Daisi.Broski) clashes with the Broski entry-point
// class (Daisi.Broski.Engine.Broski), and
// Daisi.Broski.Skimmer.Skimmer the class collides with its
// own namespace. Both need concrete aliases.
using BroskiApi = Daisi.Broski.Engine.Broski;
using BroskiOptions = Daisi.Broski.Engine.BroskiOptions;
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

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

    /// <summary>True when the previous "page" was the /search route
    /// rather than a URL. The Home page's back button reads this
    /// to decide whether an empty URL history means "dead end"
    /// (disable the button) or "route back to search" (re-render
    /// the /search page with its last query + hits intact). Set
    /// by the Search page when the user opens a hit; cleared by
    /// the Home back button when it routes back to /search.</summary>
    public bool HasSearchFallback { get; set; }

    /// <summary>True while a skim is in flight. Components use this
    /// to show a spinner / disable the address bar.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Last fetch / extraction error message, or <c>null</c>
    /// on success. Cleared at the start of each new navigation.</summary>
    public string? Error { get; private set; }

    private bool _scriptingEnabled = SurferSettings.ScriptingEnabled;

    /// <summary>Whether to execute page scripts before extraction.
    /// Toggle from the UI to flip between fast / static extraction
    /// (off) and full SPA rendering (on, the default). Takes effect
    /// on the next navigation. Change is persisted to
    /// <see cref="SurferSettings"/> so the next launch starts
    /// in the same mode.</summary>
    public bool ScriptingEnabled
    {
        get => _scriptingEnabled;
        set
        {
            if (_scriptingEnabled == value) return;
            _scriptingEnabled = value;
            SurferSettings.ScriptingEnabled = value;
        }
    }

    /// <summary>PNG bytes of the most recent snapshot render, or
    /// <c>null</c> when the user hasn't opened the Snapshot tab
    /// yet (or the render failed). Keyed by
    /// <see cref="SnapshotUrl"/> so switching tabs back and forth
    /// doesn't re-render unless the URL actually changed.</summary>
    public byte[]? SnapshotPng { get; private set; }

    /// <summary>URL the current <see cref="SnapshotPng"/> was
    /// rendered from — used as a cache key so opening the Snapshot
    /// tab after navigation invalidates and regenerates.</summary>
    public string? SnapshotUrl { get; private set; }

    /// <summary>True while a snapshot render is in flight. The
    /// engine's full load+layout+paint is a few seconds of work;
    /// the UI shows a spinner while this is true.</summary>
    public bool IsSnapshotting { get; private set; }

    /// <summary>Error message from the last snapshot attempt, or
    /// <c>null</c> on success / never-run.</summary>
    public string? SnapshotError { get; private set; }

    public event Action? StateChanged;

    /// <summary>Backs the current in-flight skim. Replaced on every
    /// new navigation; cancelled (then replaced) if the user clicks
    /// the Cancel button or kicks off a second navigation before
    /// the first finishes. Null when nothing is in flight.</summary>
    private CancellationTokenSource? _currentFetch;

    /// <summary>Navigate to <paramref name="rawUrl"/> — fetch + parse +
    /// optionally run scripts + extract + push onto the history stack.
    /// Safe to call concurrently; a new call cancels any in-flight
    /// one so only the latest navigation lands. The
    /// <see cref="StateChanged"/> event fires twice: once when loading
    /// begins, once when it ends.</summary>
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

        // Abort any in-flight skim so a new URL doesn't stall
        // behind the old one's timeout. The cancelled Task faults
        // with OperationCanceledException, which the catch below
        // quietly ignores — only the outcome of THIS navigation
        // should show in the UI.
        var previous = _currentFetch;
        var cts = new CancellationTokenSource();
        _currentFetch = cts;
        previous?.Cancel();

        CurrentRequestUrl = rawUrl;
        IsLoading = true;
        Error = null;
        // Invalidate any previous snapshot — it was for the
        // page we're about to leave. The Snapshot tab will
        // re-render on demand after the new page lands.
        SnapshotPng = null;
        SnapshotUrl = null;
        SnapshotError = null;
        StateChanged?.Invoke();

        try
        {
            Current = await SkimmerApi.SkimAsync(url, new SkimmerOptions
            {
                ScriptingEnabled = ScriptingEnabled,
            }, cts.Token);
            CurrentRequestUrl = Current.Url.AbsoluteUri;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // User clicked Cancel (or a newer navigation started).
            // Leave Error null — the UI should feel instant,
            // not surface a scary message.
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            // Only clear the CTS reference if it's still the one
            // we owned — a later navigation may have swapped it.
            if (ReferenceEquals(_currentFetch, cts)) _currentFetch = null;
            cts.Dispose();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Cancel the current in-flight skim (if any). No-op
    /// when nothing is loading. Wired to the Cancel button in the
    /// address bar; also fires on Surfer shutdown if the
    /// <see cref="SkimSession"/> instance is disposed.</summary>
    public void CancelCurrent()
    {
        _currentFetch?.Cancel();
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

    /// <summary>Cap the snapshot buffer height so an
    /// infinite-scroll page doesn't allocate gigabytes of
    /// RGBA pixels. 16000px at 1280 wide is ~82 MB, which
    /// still comfortably fits in memory and covers every
    /// realistic article length.</summary>
    private const int MaxSnapshotHeight = 16000;

    /// <summary>Walk the layout tree and return the maximum
    /// Y+Height of any box — the practical "how tall did
    /// this document end up" measure. Considers every
    /// descendant including absolutely-positioned overlays
    /// so decorative footers anchored below the main
    /// content still show up in the snapshot.</summary>
    private static double MaxBoxBottom(LayoutBox box)
    {
        double bottom = box.Y + box.Height
            + box.Padding.Bottom + box.Border.Bottom;
        foreach (var child in box.Children)
        {
            var cb = MaxBoxBottom(child);
            if (cb > bottom) bottom = cb;
        }
        return bottom;
    }

    /// <summary>Drop the current cached snapshot so the
    /// next <see cref="EnsureSnapshotAsync"/> call re-runs
    /// the engine. Used by the "Re-render" button when a
    /// user wants to force a fresh render on the same URL
    /// (e.g. after fonts / images arrived late).</summary>
    public void InvalidateSnapshot()
    {
        SnapshotPng = null;
        SnapshotUrl = null;
        SnapshotError = null;
        StateChanged?.Invoke();
    }

    /// <summary>Render a snapshot (rasterized PNG) of the
    /// current page through the Broski engine's full
    /// pipeline — load + layout + paint. This is a separate
    /// network fetch from the Skimmer's, because the
    /// Skimmer throws its Document away after extraction;
    /// re-fetching is the honest way to get the raw HTML
    /// rendered.
    ///
    /// <para>
    /// Cached by URL: calling again for the same URL returns
    /// immediately from the cached <see cref="SnapshotPng"/>.
    /// Navigation invalidates the cache so a re-render
    /// picks up the new page.
    /// </para></summary>
    public async Task EnsureSnapshotAsync(int width = 1280, int height = 800)
    {
        if (CurrentRequestUrl is null) return;
        if (IsSnapshotting) return;
        if (SnapshotPng is not null && SnapshotUrl == CurrentRequestUrl) return;
        if (!Uri.TryCreate(CurrentRequestUrl, UriKind.Absolute, out var url)) return;

        IsSnapshotting = true;
        SnapshotError = null;
        StateChanged?.Invoke();

        try
        {
            var page = await BroskiApi.LoadAsync(url, new BroskiOptions
            {
                ScriptingEnabled = ScriptingEnabled,
            });
            var vp = new Viewport { Width = width, Height = height };
            var root = LayoutTree.Build(page.Document, vp);

            // Grow the output canvas to the document's full
            // content height so long pages (articles, docs)
            // render as a single tall image instead of being
            // clipped at the 800px viewport. Layout itself
            // stays 1280×800 so vh / @media / fixed-position
            // still resolve the way the page's CSS expects.
            int docHeight = (int)Math.Ceiling(MaxBoxBottom(root));
            int bufHeight = Math.Clamp(docHeight, height, MaxSnapshotHeight);
            var buf = Painter.Paint(root, page.Document, vp, width, bufHeight);
            var png = PngEncoder.Encode(buf);

            SnapshotPng = png;
            SnapshotUrl = CurrentRequestUrl;
        }
        catch (Exception ex)
        {
            SnapshotError = ex.Message;
            SnapshotPng = null;
        }
        finally
        {
            IsSnapshotting = false;
            StateChanged?.Invoke();
        }
    }
}
