using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// The result of running <see cref="ContentExtractor"/> against a parsed
/// document. Captures everything a downstream summarizer / index /
/// reader-mode renderer is likely to want, in a stable shape that's
/// cheap to serialize.
///
/// <para>
/// The single source of truth for the article body is
/// <see cref="ContentRoot"/>, the <see cref="Element"/> subtree picked
/// by the extractor. <see cref="PlainText"/> and the <c>JsonFormatter</c>
/// / <c>MarkdownFormatter</c> outputs are derivations of that subtree;
/// callers can re-render against alternate formatters by reading
/// <see cref="ContentRoot"/> directly.
/// </para>
/// </summary>
public sealed class ArticleContent
{
    /// <summary>The fetched URL after redirects (the page the extractor
    /// actually saw).</summary>
    public required Uri Url { get; init; }

    /// <summary>Page title — preferred order: <c>og:title</c>,
    /// <c>twitter:title</c>, <c>&lt;title&gt;</c>, first <c>&lt;h1&gt;</c>.</summary>
    public string? Title { get; init; }

    /// <summary>Author / byline — preferred order:
    /// <c>article:author</c>, <c>author</c> meta, <c>[rel=author]</c>,
    /// <c>.author</c> / <c>.byline</c> common class names.</summary>
    public string? Byline { get; init; }

    /// <summary>Publication timestamp — preferred order:
    /// <c>article:published_time</c>, <c>&lt;time datetime&gt;</c>,
    /// <c>date</c> meta. Stored as the raw string the page reported
    /// (typically ISO-8601) so downstream consumers can reparse against
    /// their own clock conventions.</summary>
    public string? PublishedAt { get; init; }

    /// <summary>Document language hint from <c>&lt;html lang&gt;</c>.</summary>
    public string? Lang { get; init; }

    /// <summary>Open Graph / Twitter card description — useful as a
    /// fallback summary when the body is short or paywalled.</summary>
    public string? Description { get; init; }

    /// <summary>The site name — typically <c>og:site_name</c>, falling
    /// back to the URL host.</summary>
    public string? SiteName { get; init; }

    /// <summary>Hero image — Open Graph or Twitter card image, or the
    /// first <c>&lt;img&gt;</c> inside the picked content root.</summary>
    public string? HeroImage { get; init; }

    /// <summary>The DOM subtree chosen as the article body. Walk this
    /// to render in your own format. Will be <c>null</c> when the
    /// extractor couldn't find a clear content root (very short pages,
    /// pure-form pages, login walls).</summary>
    public Element? ContentRoot { get; init; }

    /// <summary>Plain text rendering of <see cref="ContentRoot"/> with
    /// runs of whitespace collapsed to single spaces and paragraph
    /// breaks preserved as double newlines.</summary>
    public string PlainText { get; init; } = "";

    /// <summary>Whitespace-separated word count of <see cref="PlainText"/>.
    /// Cheap heuristic used by the extractor's scoring pass and exposed
    /// to callers as a quick "is this worth reading" signal.</summary>
    public int WordCount { get; init; }

    /// <summary>Images discovered inside <see cref="ContentRoot"/>, in
    /// document order. Each is the resolved absolute URL plus its
    /// <c>alt</c> text (empty string when omitted).</summary>
    public IReadOnlyList<ExtractedImage> Images { get; init; } =
        Array.Empty<ExtractedImage>();

    /// <summary>Hyperlinks discovered inside <see cref="ContentRoot"/>,
    /// in document order. Each is the resolved absolute URL plus the
    /// link's text content. Self-links and empty-text decorative
    /// links are dropped.</summary>
    public IReadOnlyList<ExtractedLink> Links { get; init; } =
        Array.Empty<ExtractedLink>();
}

public readonly record struct ExtractedImage(string Src, string Alt);

public readonly record struct ExtractedLink(string Href, string Text);
