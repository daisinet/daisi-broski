namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// A single fetched web-font file held in memory. One entry per
/// <c>@font-face</c> declaration the cascade surfaces; the
/// raw bytes are the unmodified HTTP response body so the
/// downstream parser can dispatch on format (TTF, OTF, WOFF,
/// WOFF2) via magic-number sniff at rendering time.
///
/// <para>
/// This phase only handles fetching + in-memory storage. The
/// subsequent slice parses the binary into glyph outlines and
/// wires the Painter to render with it in place of the bitmap
/// font. Keeping the fetched bytes raw (not pre-parsed) lets
/// us iterate on the parser without re-downloading.
/// </para>
/// </summary>
public sealed record WebFont
{
    /// <summary>CSS <c>font-family</c> name from the
    /// <c>@font-face</c> rule. Compared case-insensitively
    /// against the cascade's resolved <c>font-family</c> when
    /// the painter looks up which font to use for a given
    /// text run.</summary>
    public required string Family { get; init; }

    /// <summary>CSS <c>font-weight</c> from the rule — 400 =
    /// regular, 700 = bold, etc. The spec permits a range
    /// (<c>100 900</c>); we store the first token only.</summary>
    public int Weight { get; init; } = 400;

    /// <summary>CSS <c>font-style</c>: <c>normal</c>,
    /// <c>italic</c>, or <c>oblique</c>.</summary>
    public string Style { get; init; } = "normal";

    /// <summary>Absolute URL the font was fetched from.
    /// Informational; the bytes have already been loaded.</summary>
    public required Uri Source { get; init; }

    /// <summary>Format hint from the <c>src: url(...) format(...)</c>
    /// declaration — <c>woff2</c>, <c>woff</c>, <c>truetype</c>,
    /// <c>opentype</c>, or empty when absent. The font parser
    /// also sniffs the magic number, so this is a hint, not
    /// the source of truth.</summary>
    public string Format { get; init; } = "";

    /// <summary>Raw HTTP response body for the font. Parsed on
    /// demand by the glyph rasterizer.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>Parsed <c>unicode-range</c> from the
    /// <c>@font-face</c> declaration. Each entry is a
    /// [start, end] inclusive code-point range. Empty list =
    /// covers all code points (the CSS default when the
    /// descriptor is omitted).
    ///
    /// <para>Google Fonts slices one family into 200+
    /// subsetted files, one per unicode-range block
    /// (Latin / Latin-ext / Cyrillic / Vietnamese / ...). Without
    /// checking this at resolve time, the font picker grabs
    /// the first match — often a Cyrillic subset — and every
    /// Latin glyph lookup returns .notdef.</para></summary>
    public IReadOnlyList<(int Start, int End)> UnicodeRange { get; init; } =
        Array.Empty<(int, int)>();

    /// <summary>True when the font's declared
    /// <c>unicode-range</c> covers the given char (or when no
    /// range was declared, which means "covers everything").</summary>
    public bool Covers(int codePoint)
    {
        if (UnicodeRange.Count == 0) return true;
        foreach (var (s, e) in UnicodeRange)
        {
            if (codePoint >= s && codePoint <= e) return true;
        }
        return false;
    }
}
