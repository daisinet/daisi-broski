namespace Daisi.Broski.Engine.Html;

/// <summary>
/// Lookup table for HTML5 named character references (e.g. <c>&amp;amp;</c>,
/// <c>&amp;nbsp;</c>).
///
/// The WHATWG standard defines ~2200 named entities. This table ships the
/// ~120 that cover >99% of real-world usage: the ASCII / punctuation set,
/// the Latin-1 supplement, common typographic characters, currency symbols,
/// math symbols, Greek letters, and a handful of arrow / shape symbols.
///
/// If we encounter a real site that relies on an entity we don't have, add
/// it here. Expansion is mechanical — the whole table is a plain dictionary.
///
/// Legacy no-semicolon forms (<c>&amp;amp</c> without the trailing <c>;</c>)
/// are not supported; real sites use the semicolon form. The tokenizer
/// requires a terminating <c>;</c> before consulting this table.
/// </summary>
internal static class HtmlEntities
{
    // ASCII / structural.
    // Latin-1 supplement & Windows-1252 typography.
    // Currency & legal symbols.
    // Greek letters (lowercase + uppercase).
    // Math / arrow / shape symbols.
    // Keep this alphabetized within each section so additions are easy.
    private static readonly Dictionary<string, string> _named = new(StringComparer.Ordinal)
    {
        // Structural
        ["amp"] = "&",
        ["apos"] = "'",
        ["gt"] = ">",
        ["lt"] = "<",
        ["quot"] = "\"",

        // Whitespace & non-printing
        ["nbsp"] = "\u00A0",
        ["ensp"] = "\u2002",
        ["emsp"] = "\u2003",
        ["thinsp"] = "\u2009",
        ["zwj"] = "\u200D",
        ["zwnj"] = "\u200C",
        ["shy"] = "\u00AD",

        // Latin-1 supplement (selected)
        ["iexcl"] = "\u00A1",
        ["cent"] = "\u00A2",
        ["pound"] = "\u00A3",
        ["curren"] = "\u00A4",
        ["yen"] = "\u00A5",
        ["brvbar"] = "\u00A6",
        ["sect"] = "\u00A7",
        ["uml"] = "\u00A8",
        ["copy"] = "\u00A9",
        ["ordf"] = "\u00AA",
        ["laquo"] = "\u00AB",
        ["not"] = "\u00AC",
        ["reg"] = "\u00AE",
        ["macr"] = "\u00AF",
        ["deg"] = "\u00B0",
        ["plusmn"] = "\u00B1",
        ["sup2"] = "\u00B2",
        ["sup3"] = "\u00B3",
        ["acute"] = "\u00B4",
        ["micro"] = "\u00B5",
        ["para"] = "\u00B6",
        ["middot"] = "\u00B7",
        ["cedil"] = "\u00B8",
        ["sup1"] = "\u00B9",
        ["ordm"] = "\u00BA",
        ["raquo"] = "\u00BB",
        ["frac14"] = "\u00BC",
        ["frac12"] = "\u00BD",
        ["frac34"] = "\u00BE",
        ["iquest"] = "\u00BF",
        ["times"] = "\u00D7",
        ["divide"] = "\u00F7",

        // Accented letters (common ones)
        ["Agrave"] = "\u00C0",
        ["Aacute"] = "\u00C1",
        ["Acirc"] = "\u00C2",
        ["Atilde"] = "\u00C3",
        ["Auml"] = "\u00C4",
        ["Aring"] = "\u00C5",
        ["AElig"] = "\u00C6",
        ["Ccedil"] = "\u00C7",
        ["Egrave"] = "\u00C8",
        ["Eacute"] = "\u00C9",
        ["Ecirc"] = "\u00CA",
        ["Euml"] = "\u00CB",
        ["Igrave"] = "\u00CC",
        ["Iacute"] = "\u00CD",
        ["Icirc"] = "\u00CE",
        ["Iuml"] = "\u00CF",
        ["Ntilde"] = "\u00D1",
        ["Ograve"] = "\u00D2",
        ["Oacute"] = "\u00D3",
        ["Ocirc"] = "\u00D4",
        ["Otilde"] = "\u00D5",
        ["Ouml"] = "\u00D6",
        ["Oslash"] = "\u00D8",
        ["Ugrave"] = "\u00D9",
        ["Uacute"] = "\u00DA",
        ["Ucirc"] = "\u00DB",
        ["Uuml"] = "\u00DC",
        ["Yacute"] = "\u00DD",
        ["szlig"] = "\u00DF",
        ["agrave"] = "\u00E0",
        ["aacute"] = "\u00E1",
        ["acirc"] = "\u00E2",
        ["atilde"] = "\u00E3",
        ["auml"] = "\u00E4",
        ["aring"] = "\u00E5",
        ["aelig"] = "\u00E6",
        ["ccedil"] = "\u00E7",
        ["egrave"] = "\u00E8",
        ["eacute"] = "\u00E9",
        ["ecirc"] = "\u00EA",
        ["euml"] = "\u00EB",
        ["igrave"] = "\u00EC",
        ["iacute"] = "\u00ED",
        ["icirc"] = "\u00EE",
        ["iuml"] = "\u00EF",
        ["ntilde"] = "\u00F1",
        ["ograve"] = "\u00F2",
        ["oacute"] = "\u00F3",
        ["ocirc"] = "\u00F4",
        ["otilde"] = "\u00F5",
        ["ouml"] = "\u00F6",
        ["oslash"] = "\u00F8",
        ["ugrave"] = "\u00F9",
        ["uacute"] = "\u00FA",
        ["ucirc"] = "\u00FB",
        ["uuml"] = "\u00FC",
        ["yacute"] = "\u00FD",
        ["yuml"] = "\u00FF",

        // Typographic punctuation
        ["ndash"] = "\u2013",
        ["mdash"] = "\u2014",
        ["lsquo"] = "\u2018",
        ["rsquo"] = "\u2019",
        ["sbquo"] = "\u201A",
        ["ldquo"] = "\u201C",
        ["rdquo"] = "\u201D",
        ["bdquo"] = "\u201E",
        ["dagger"] = "\u2020",
        ["Dagger"] = "\u2021",
        ["bull"] = "\u2022",
        ["hellip"] = "\u2026",
        ["permil"] = "\u2030",
        ["prime"] = "\u2032",
        ["Prime"] = "\u2033",
        ["lsaquo"] = "\u2039",
        ["rsaquo"] = "\u203A",
        ["oline"] = "\u203E",

        // Currency / legal
        ["euro"] = "\u20AC",
        ["trade"] = "\u2122",

        // Math / logic
        ["minus"] = "\u2212",
        ["lowast"] = "\u2217",
        ["radic"] = "\u221A",
        ["infin"] = "\u221E",
        ["cap"] = "\u2229",
        ["cup"] = "\u222A",
        ["int"] = "\u222B",
        ["asymp"] = "\u2248",
        ["ne"] = "\u2260",
        ["equiv"] = "\u2261",
        ["le"] = "\u2264",
        ["ge"] = "\u2265",
        ["sub"] = "\u2282",
        ["sup"] = "\u2283",
        ["nsub"] = "\u2284",
        ["sube"] = "\u2286",
        ["supe"] = "\u2287",
        ["oplus"] = "\u2295",
        ["otimes"] = "\u2297",
        ["perp"] = "\u22A5",
        ["sdot"] = "\u22C5",
        ["forall"] = "\u2200",
        ["part"] = "\u2202",
        ["exist"] = "\u2203",
        ["empty"] = "\u2205",
        ["nabla"] = "\u2207",
        ["isin"] = "\u2208",
        ["notin"] = "\u2209",
        ["ni"] = "\u220B",
        ["prod"] = "\u220F",
        ["sum"] = "\u2211",
        ["and"] = "\u2227",
        ["or"] = "\u2228",

        // Arrows
        ["larr"] = "\u2190",
        ["uarr"] = "\u2191",
        ["rarr"] = "\u2192",
        ["darr"] = "\u2193",
        ["harr"] = "\u2194",
        ["crarr"] = "\u21B5",
        ["lArr"] = "\u21D0",
        ["uArr"] = "\u21D1",
        ["rArr"] = "\u21D2",
        ["dArr"] = "\u21D3",
        ["hArr"] = "\u21D4",

        // Greek lowercase
        ["alpha"] = "\u03B1",
        ["beta"] = "\u03B2",
        ["gamma"] = "\u03B3",
        ["delta"] = "\u03B4",
        ["epsilon"] = "\u03B5",
        ["zeta"] = "\u03B6",
        ["eta"] = "\u03B7",
        ["theta"] = "\u03B8",
        ["iota"] = "\u03B9",
        ["kappa"] = "\u03BA",
        ["lambda"] = "\u03BB",
        ["mu"] = "\u03BC",
        ["nu"] = "\u03BD",
        ["xi"] = "\u03BE",
        ["omicron"] = "\u03BF",
        ["pi"] = "\u03C0",
        ["rho"] = "\u03C1",
        ["sigmaf"] = "\u03C2",
        ["sigma"] = "\u03C3",
        ["tau"] = "\u03C4",
        ["upsilon"] = "\u03C5",
        ["phi"] = "\u03C6",
        ["chi"] = "\u03C7",
        ["psi"] = "\u03C8",
        ["omega"] = "\u03C9",

        // Greek uppercase (selected)
        ["Alpha"] = "\u0391",
        ["Beta"] = "\u0392",
        ["Gamma"] = "\u0393",
        ["Delta"] = "\u0394",
        ["Theta"] = "\u0398",
        ["Lambda"] = "\u039B",
        ["Pi"] = "\u03A0",
        ["Sigma"] = "\u03A3",
        ["Phi"] = "\u03A6",
        ["Psi"] = "\u03A8",
        ["Omega"] = "\u03A9",

        // Shapes & misc
        ["loz"] = "\u25CA",
        ["spades"] = "\u2660",
        ["clubs"] = "\u2663",
        ["hearts"] = "\u2665",
        ["diams"] = "\u2666",
        ["fnof"] = "\u0192",
    };

    /// <summary>
    /// Look up a named character reference by its bare name (no leading
    /// '&amp;' or trailing ';'). Returns <c>true</c> on hit.
    /// </summary>
    public static bool TryGetNamed(string name, out string replacement)
    {
        if (_named.TryGetValue(name, out var value))
        {
            replacement = value;
            return true;
        }
        replacement = "";
        return false;
    }

    /// <summary>
    /// Translate a numeric character reference code point into its string
    /// representation. Applies the HTML5 numeric reference fixup table for
    /// legacy Windows-1252 code points (0x80–0x9F) and rejects surrogates /
    /// out-of-range values by substituting U+FFFD.
    /// </summary>
    public static string NumericReferenceToString(int codePoint)
    {
        // WHATWG §13.2.5.80: these legacy Windows-1252 code points are remapped.
        switch (codePoint)
        {
            case 0x00: return "\uFFFD";
            case 0x80: return "\u20AC"; // €
            case 0x82: return "\u201A"; // ‚
            case 0x83: return "\u0192"; // ƒ
            case 0x84: return "\u201E"; // „
            case 0x85: return "\u2026"; // …
            case 0x86: return "\u2020"; // †
            case 0x87: return "\u2021"; // ‡
            case 0x88: return "\u02C6"; // ˆ
            case 0x89: return "\u2030"; // ‰
            case 0x8A: return "\u0160"; // Š
            case 0x8B: return "\u2039"; // ‹
            case 0x8C: return "\u0152"; // Œ
            case 0x8E: return "\u017D"; // Ž
            case 0x91: return "\u2018"; // '
            case 0x92: return "\u2019"; // '
            case 0x93: return "\u201C"; // "
            case 0x94: return "\u201D"; // "
            case 0x95: return "\u2022"; // •
            case 0x96: return "\u2013"; // –
            case 0x97: return "\u2014"; // —
            case 0x98: return "\u02DC"; // ˜
            case 0x99: return "\u2122"; // ™
            case 0x9A: return "\u0161"; // š
            case 0x9B: return "\u203A"; // ›
            case 0x9C: return "\u0153"; // œ
            case 0x9E: return "\u017E"; // ž
            case 0x9F: return "\u0178"; // Ÿ
        }

        // Surrogates and out-of-range values are parse errors → U+FFFD.
        if ((codePoint >= 0xD800 && codePoint <= 0xDFFF) || codePoint > 0x10FFFF)
        {
            return "\uFFFD";
        }

        return char.ConvertFromUtf32(codePoint);
    }
}
