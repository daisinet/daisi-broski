using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Engine.Paint;

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

        // Phase 6l: fetch + decode <img src> in parallel so
        // the painter can blit real pixels for hero / logo /
        // icon images. PNG only for v1; other formats fail
        // the decode and fall through to the placeholder rect.
        await FetchImagesAsync(document, fetchResult.FinalUrl, ct).ConfigureAwait(false);

        // Phase 6r: fetch @font-face sources in parallel. At
        // this stage we only store the raw bytes keyed by
        // family — parsing + glyph rasterization lands in a
        // follow-up slice. Swallows per-font errors so a 404
        // on an optional font variant doesn't nuke the page.
        await FetchFontsAsync(document, fetchResult.FinalUrl, ct).ConfigureAwait(false);

        // Phase 6z: fetch CSS-referenced background images
        // (background-image: url(...)). Walk every selector
        // that sets a background-image URL, match against
        // the DOM, and fetch+decode the referenced file so
        // the painter can blit it as a layer.
        await FetchBackgroundImagesAsync(document, fetchResult.FinalUrl, ct).ConfigureAwait(false);

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
                // Google Fonts' CSS endpoint returns different
                // @font-face src URLs based on User-Agent —
                // modern UAs get woff2 links; older UAs get
                // TTF. Since we parse TTF directly, ask for
                // the TTF-flavored CSS here so the downstream
                // font fetcher picks up parseable files.
                string? ua = LooksLikeGoogleFonts(pair.href)
                    ? "Mozilla/5.0 (Windows NT 6.1; Trident/5.0)" : null;
                var result = await _fetcher.FetchAsync(pair.href, ua, ct)
                    .ConfigureAwait(false);
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

    /// <summary>Fetch every <c>&lt;img src&gt;</c>, attempt
    /// PNG decode, attach the resulting RasterBuffer to the
    /// document. Per-image failures are silent — the painter
    /// renders the placeholder rect for images we couldn't
    /// fetch or decode (non-PNG formats, 404, etc.).</summary>
    private async Task FetchImagesAsync(
        Document document, Uri baseUrl, CancellationToken ct)
    {
        var imgs = new List<(Element img, Uri src)>();
        foreach (var el in document.DescendantElements())
        {
            if (el.TagName != "img") continue;
            var srcRaw = el.GetAttribute("src");
            if (string.IsNullOrEmpty(srcRaw)) continue;
            // Skip data: URIs and other non-http schemes for
            // now — they'd need their own decoder paths.
            if (!Uri.TryCreate(baseUrl, srcRaw, out var src)) continue;
            if (src.Scheme is not ("http" or "https")) continue;
            imgs.Add((el, src));
        }

        var tasks = imgs.Select(async pair =>
        {
            try
            {
                var result = await _fetcher.FetchAsync(pair.src, ct).ConfigureAwait(false);
                if ((int)result.Status >= 200 && (int)result.Status < 300)
                {
                    // Dispatch on content type / URL suffix:
                    // SVGs are parsed into a DOM subtree and
                    // rendered by the painter at paint time;
                    // PNGs go through the pixel decoder.
                    if (LooksLikeSvg(result.ContentType, pair.src))
                    {
                        var svg = Daisi.Broski.Engine.Html.HtmlTreeBuilder.Parse(
                            System.Text.Encoding.UTF8.GetString(result.Body));
                        var root = FindSvgRoot(svg.DocumentElement);
                        if (root is not null) return (pair.img, (object?)root);
                        return (pair.img, (object?)null);
                    }
                    var decoded = PngDecoder.TryDecode(result.Body);
                    return (pair.img, (object?)decoded);
                }
            }
            catch
            {
                // best-effort
            }
            return (pair.img, (object?)null);
        }).ToList();

        var images = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (img, decoded) in images)
        {
            if (decoded is not null)
            {
                document.AttachImage(img, decoded);
            }
        }
    }

    private static bool LooksLikeSvg(string? contentType, Uri src)
    {
        if (!string.IsNullOrEmpty(contentType)
            && contentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var path = src.AbsolutePath;
        return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static Daisi.Broski.Engine.Dom.Element? FindSvgRoot(
        Daisi.Broski.Engine.Dom.Element? root)
    {
        if (root is null) return null;
        if (root.TagName == "svg") return root;
        foreach (var child in root.Children)
        {
            var hit = FindSvgRoot(child);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Pull <c>@font-face</c> rules out of every
    /// already-fetched stylesheet, fetch each unique
    /// <c>src:url()</c>, and hand the raw bytes to the
    /// document's font store. Families are cached so the
    /// same font referenced by multiple weights doesn't
    /// refetch. Parsing + rendering is out of scope for this
    /// slice — we just need the bytes present so the next
    /// slice can rasterize them.</summary>
    private async Task FetchFontsAsync(
        Document document, Uri baseUrl, CancellationToken ct)
    {
        var candidates = new List<Daisi.Broski.Engine.Fonts.FontFaceParser.Candidate>();
        foreach (var sheet in document.StyleSheets)
        {
            candidates.AddRange(Daisi.Broski.Engine.Fonts.FontFaceParser.Extract(sheet));
        }
        if (candidates.Count == 0) return;

        // Deduplicate by absolute URL — many Google Fonts
        // CSS files list the same file under multiple
        // unicode-range blocks, and we only need to fetch
        // each once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<(Daisi.Broski.Engine.Fonts.FontFaceParser.Candidate c, Uri url)>();
        foreach (var c in candidates)
        {
            if (!Uri.TryCreate(baseUrl, c.Src, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(uri.AbsoluteUri)) continue;
            unique.Add((c, uri));
        }

        var tasks = unique.Select(async pair =>
        {
            try
            {
                // Google Fonts and most CDNs content-negotiate
                // on User-Agent — modern browsers get WOFF2,
                // older ones get TTF. Our parser reads TTF
                // directly, so we spoof an IE9-era UA on the
                // font request to skip the Brotli+glyf-transform
                // step entirely.
                string? ua = LooksLikeGoogleFonts(pair.url)
                    ? "Mozilla/5.0 (Windows NT 6.1; Trident/5.0)" : null;
                var result = await _fetcher.FetchAsync(pair.url, ua, ct)
                    .ConfigureAwait(false);
                if ((int)result.Status < 200 || (int)result.Status >= 300) return null;
                return new Daisi.Broski.Engine.Fonts.WebFont
                {
                    Family = pair.c.Family,
                    Weight = pair.c.Weight,
                    WeightMax = pair.c.WeightMax,
                    Style = pair.c.Style,
                    Source = pair.url,
                    Format = pair.c.Format,
                    Bytes = result.Body,
                    UnicodeRange = pair.c.UnicodeRange,
                };
            }
            catch
            {
                return null;
            }
        }).ToList();

        var loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var font in loaded)
        {
            if (font is not null) document.AttachFont(font);
        }
    }

    /// <summary>Walk every StyleRule in every loaded
    /// stylesheet; for each declaration that sets a
    /// <c>background-image: url(...)</c>, resolve the URL
    /// against the stylesheet's base (we track which sheet
    /// each rule came from), match the selector against the
    /// DOM, fetch + decode the image, and attach the decoded
    /// buffer to each matching element. PNG only for now.</summary>
    private async Task FetchBackgroundImagesAsync(
        Document document, Uri baseUri, CancellationToken ct)
    {
        var elementUrls = new List<(Element el, Uri url)>();
        var uniqueUrls = new HashSet<Uri>();

        foreach (var sheet in document.StyleSheets)
        {
            foreach (var rule in sheet.Rules)
            {
                ScanRule(rule, document, baseUri, elementUrls, uniqueUrls);
            }
        }

        if (uniqueUrls.Count == 0) return;

        // Fetch each unique URL once, decode, stash the
        // bytes keyed by URL so multiple matching elements
        // share the decoded buffer.
        var fetchTasks = uniqueUrls.Select(async url =>
        {
            try
            {
                var result = await _fetcher.FetchAsync(url, ct).ConfigureAwait(false);
                if ((int)result.Status < 200 || (int)result.Status >= 300)
                {
                    return (url, (RasterBuffer?)null);
                }
                return (url, PngDecoder.TryDecode(result.Body));
            }
            catch
            {
                return (url, (RasterBuffer?)null);
            }
        }).ToList();

        var decoded = (await Task.WhenAll(fetchTasks).ConfigureAwait(false))
            .Where(t => t.Item2 is not null)
            .ToDictionary(t => t.url, t => t.Item2!);

        foreach (var (el, url) in elementUrls)
        {
            if (decoded.TryGetValue(url, out var raster))
            {
                document.AttachBackgroundImage(el, raster);
            }
        }
    }

    private static void ScanRule(
        Css.Rule rule, Document document, Uri baseUri,
        List<(Element, Uri)> elementUrls, HashSet<Uri> uniqueUrls)
    {
        if (rule is Css.StyleRule sr)
        {
            // Find a background-image: url(...) declaration.
            string? urlStr = null;
            foreach (var d in sr.Declarations)
            {
                var prop = d.Property.ToLowerInvariant();
                if (prop == "background-image" || prop == "background")
                {
                    var u = ExtractUrl(d.Value);
                    if (u is not null) { urlStr = u; break; }
                }
            }
            if (urlStr is null) return;
            if (!Uri.TryCreate(baseUri, urlStr, out var absUrl)) return;
            if (absUrl.Scheme is not ("http" or "https")) return;
            uniqueUrls.Add(absUrl);
            if (sr.Selectors is null) return;
            foreach (var el in document.DescendantElements())
            {
                foreach (var complex in sr.Selectors.Selectors)
                {
                    if (Daisi.Broski.Engine.Dom.Selectors.SelectorMatcher.MatchesComplex(el, complex))
                    {
                        elementUrls.Add((el, absUrl));
                        break;
                    }
                }
            }
        }
        else if (rule is Css.AtRule ar && ar.Rules is { Count: > 0 })
        {
            foreach (var nested in ar.Rules)
            {
                ScanRule(nested, document, baseUri, elementUrls, uniqueUrls);
            }
        }
    }

    /// <summary>Pull the first <c>url(...)</c> target out of
    /// a CSS value. Accepts quoted and unquoted URLs; returns
    /// null when no url() is present (e.g. a pure gradient
    /// or keyword value).</summary>
    private static string? ExtractUrl(string value)
    {
        int i = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + 4;
        int end = value.IndexOf(')', start);
        if (end < 0) return null;
        var inner = value.Substring(start, end - start).Trim();
        if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'')
            && inner[^1] == inner[0])
        {
            inner = inner.Substring(1, inner.Length - 2);
        }
        return inner.Length == 0 ? null : inner;
    }

    private static bool LooksLikeGoogleFonts(Uri url) =>
        url.Host.EndsWith("gstatic.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase);

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
