using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// Picks the best <see cref="TtfReader"/> for a computed
/// <c>font-family</c> + <c>font-weight</c> + <c>font-style</c>
/// trio, with per-document caching so each font file parses
/// once. Returns null when no loaded font matches; the painter
/// falls back to its bitmap font in that case.
/// </summary>
public static class FontResolver
{
    // Parsed-reader cache: WebFont instance → TtfReader (or
    // null if we've already tried and failed to parse it).
    // Stored on the document so it persists across paint
    // passes without being leaked globally.
    private sealed class ParsedCache
    {
        public Dictionary<WebFont, TtfReader?> Byte { get; } = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, ParsedCache> _caches = new();

    /// <summary>Resolve the best font for a text run on
    /// <paramref name="document"/>. <paramref name="fontFamily"/>
    /// is the raw cascade value — CSS allows a comma-separated
    /// stack, so we try each name in order and return the first
    /// one we can parse. The <paramref name="sampleChar"/>
    /// lets the resolver pick a file whose <c>unicode-range</c>
    /// actually covers the text — critical on pages that use
    /// Google Fonts' per-subset splits, where the same family
    /// arrives as 200+ files each covering a different block
    /// (Latin / Cyrillic / Vietnamese / ...).</summary>
    public static TtfReader? Resolve(
        Document document, string fontFamily, int weight, string style,
        int sampleChar = 'A')
    {
        if (document.Fonts.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(fontFamily)) return null;
        var cache = _caches.GetOrCreateValue(document);

        foreach (var rawName in SplitFamilyStack(fontFamily))
        {
            var name = TrimQuotes(rawName).Trim();
            if (name.Length == 0) continue;
            if (!document.Fonts.TryGetValue(name, out var candidates)) continue;

            // Pick the best weight+style+coverage match. Weight
            // closeness and style match drive the core score;
            // a font that doesn't cover the sample character
            // takes a large penalty so Latin text doesn't
            // accidentally resolve to the Cyrillic subset.
            WebFont? best = null;
            int bestScore = int.MaxValue;
            foreach (var font in candidates)
            {
                int score = Math.Abs(font.Weight - weight);
                if (!string.Equals(font.Style, style, StringComparison.OrdinalIgnoreCase))
                {
                    score += 500;
                }
                if (!font.Covers(sampleChar)) score += 10000;
                if (score < bestScore) { best = font; bestScore = score; }
            }
            if (best is null) continue;
            if (!cache.Byte.TryGetValue(best, out var reader))
            {
                reader = TtfReader.TryParse(best.Bytes);
                cache.Byte[best] = reader;
            }
            if (reader is not null) return reader;
        }
        return null;
    }

    private static IEnumerable<string> SplitFamilyStack(string value)
    {
        // CSS font-family is a comma list; generic keywords
        // (serif / sans-serif / monospace) at the tail are
        // fallbacks we don't have fonts for — the caller
        // returning null after all names miss is the right
        // behavior, so we don't filter them.
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == ',' && depth == 0)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string TrimQuotes(string s)
    {
        s = s.Trim();
        if (s.Length < 2) return s;
        if ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
        {
            return s.Substring(1, s.Length - 2);
        }
        return s;
    }
}
