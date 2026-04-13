using Daisi.Broski.Engine;
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
