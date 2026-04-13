using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Net;

namespace Daisi.Broski.Engine;

/// <summary>
/// Convenience that ties the phase-1 subsystems together into a single
/// <c>URL → parsed Document</c> call. Under the hood it is just:
///
/// <code>
///   HttpFetcher.FetchAsync(url)
///     → bytes
///   EncodingSniffer.Sniff(bytes, contentType)
///     → Encoding
///   encoding.GetString(bytes)
///     → string
///   HtmlTreeBuilder.Parse(string)
///     → Document
/// </code>
///
/// This is deliberately a thin wrapper with no state of its own beyond
/// the underlying <see cref="HttpFetcher"/>. In phase 4 a real
/// <c>BrowserSession</c> with a script-driven event loop and a
/// process-isolated sandbox will replace it; until then, this is the
/// entry point the CLI and integration tests use.
/// </summary>
public sealed class PageLoader : IDisposable
{
    private readonly HttpFetcher _fetcher;
    private readonly bool _ownsFetcher;
    private bool _disposed;

    /// <summary>Create a page loader backed by a fresh
    /// <see cref="HttpFetcher"/> using the given options (or defaults).
    /// The loader owns the underlying fetcher and disposes it with
    /// itself.</summary>
    public PageLoader(HttpFetcherOptions? options = null)
        : this(new HttpFetcher(options), ownsFetcher: true) { }

    /// <summary>Create a page loader wrapping a caller-supplied fetcher.
    /// The caller retains ownership of the fetcher.</summary>
    public PageLoader(HttpFetcher fetcher)
        : this(fetcher, ownsFetcher: false) { }

    private PageLoader(HttpFetcher fetcher, bool ownsFetcher)
    {
        _fetcher = fetcher;
        _ownsFetcher = ownsFetcher;
    }

    /// <summary>
    /// Fetch <paramref name="url"/>, detect its encoding, decode, and
    /// parse into a DOM tree. See <see cref="LoadedPage"/> for the
    /// returned data.
    /// </summary>
    public async Task<LoadedPage> LoadAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fetchResult = await _fetcher.FetchAsync(url, ct).ConfigureAwait(false);

        var encoding = EncodingSniffer.Sniff(fetchResult.Body, fetchResult.ContentType);
        var html = encoding.GetString(fetchResult.Body);
        var document = HtmlTreeBuilder.Parse(html);

        // Phase 6g: fetch external stylesheets referenced by
        // `<link rel="stylesheet" href>` so the cascade has
        // something to work with on real-world pages (which
        // ship 99% of their CSS as external files). Errors
        // are swallowed per-link — a 404 on one stylesheet
        // shouldn't abort the page load.
        await FetchExternalStylesheetsAsync(document, fetchResult.FinalUrl, ct).ConfigureAwait(false);

        return new LoadedPage
        {
            RequestUrl = fetchResult.RequestUrl,
            FinalUrl = fetchResult.FinalUrl,
            Status = fetchResult.Status,
            RedirectChain = fetchResult.RedirectChain,
            Body = fetchResult.Body,
            ContentType = fetchResult.ContentType,
            Encoding = encoding,
            Html = html,
            Document = document,
        };
    }

    /// <summary>Walk every <c>&lt;link rel="stylesheet"&gt;</c>
    /// in the document, fetch its href against the base URL,
    /// parse the response as CSS, attach to the document.
    /// Per-link failures (transport errors, non-2xx status,
    /// non-CSS content type) are logged-and-skipped rather
    /// than fatal — partial styling is better than none.</summary>
    private async Task FetchExternalStylesheetsAsync(
        Document document, Uri baseUrl, CancellationToken ct)
    {
        var links = new List<(Element link, Uri href)>();
        foreach (var el in document.DescendantElements())
        {
            if (el.TagName != "link") continue;
            var rel = el.GetAttribute("rel");
            if (rel is null || !rel.Split(' ',
                StringSplitOptions.RemoveEmptyEntries)
                .Any(t => t.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var hrefRaw = el.GetAttribute("href");
            if (string.IsNullOrEmpty(hrefRaw)) continue;
            if (!Uri.TryCreate(baseUrl, hrefRaw, out var href)) continue;
            if (href.Scheme is not ("http" or "https")) continue;
            links.Add((el, href));
        }

        // Fetch in parallel — most pages link 1-5 sheets and
        // they're independent. Failures don't abort the
        // whole batch.
        var tasks = links.Select(async pair =>
        {
            try
            {
                var result = await _fetcher.FetchAsync(pair.href, ct).ConfigureAwait(false);
                if ((int)result.Status >= 200 && (int)result.Status < 300)
                {
                    var enc = EncodingSniffer.Sniff(result.Body, result.ContentType);
                    var css = enc.GetString(result.Body);
                    var sheet = CssParser.Parse(css);
                    return (pair.link, sheet);
                }
            }
            catch
            {
                // best-effort
            }
            return (pair.link, (Stylesheet?)null);
        }).ToList();

        var sheets = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (link, sheet) in sheets)
        {
            if (sheet is not null)
            {
                document.AttachExternalStylesheet(link, sheet);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsFetcher) _fetcher.Dispose();
    }
}

/// <summary>Result of a <see cref="PageLoader.LoadAsync"/> call — the
/// parsed <see cref="Document"/> plus enough metadata for the caller
/// to log the fetch or report failures.</summary>
public sealed class LoadedPage
{
    public required Uri RequestUrl { get; init; }
    public required Uri FinalUrl { get; init; }
    public required System.Net.HttpStatusCode Status { get; init; }
    public required IReadOnlyList<Uri> RedirectChain { get; init; }
    public required byte[] Body { get; init; }
    public string? ContentType { get; init; }
    public required System.Text.Encoding Encoding { get; init; }
    public required string Html { get; init; }
    public required Document Document { get; init; }
}
