namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Adobe Glyph List For New Fonts (AGLFN) subset — glyph name to
/// Unicode code point. Covers the names used by the three standard
/// encodings (<see cref="StandardEncodings"/>) plus the most
/// common Latin-1 letter names. Larger fonts ship custom glyph
/// names (<c>"a100"</c>, <c>"cid1234"</c>) that AGL doesn't know
/// — for those the caller falls back to a <c>/ToUnicode</c> CMap
/// (milestone 3) or gives up and produces ".notdef".
/// </summary>
internal static class GlyphList
{
    /// <summary>Map a glyph name to its Unicode code point. Returns
    /// <c>-1</c> for names not in the table. A few names resolve
    /// to multi-code-point sequences (<c>fi</c>, <c>fl</c> — the
    /// ligatures) which AGL spells as single U+FBxx code points;
    /// we return the precomposed form for those.</summary>
    internal static int Lookup(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        // Single-char ASCII letter names — "A".."Z", "a".."z" —
        // resolve to themselves without a table lookup.
        if (name.Length == 1) return name[0];
        return Table.TryGetValue(name, out var cp) ? cp : -1;
    }

    private static readonly Dictionary<string, int> Table = new(StringComparer.Ordinal)
    {
        // Digits
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3',
        ["four"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7',
        ["eight"] = '8', ["nine"] = '9',
        // ASCII punctuation
        ["space"] = ' ', ["exclam"] = '!', ["quotedbl"] = '"',
        ["numbersign"] = '#', ["dollar"] = '$', ["percent"] = '%',
        ["ampersand"] = '&', ["quoteright"] = '\u2019',
        ["quotesingle"] = '\'', ["parenleft"] = '(', ["parenright"] = ')',
        ["asterisk"] = '*', ["plus"] = '+', ["comma"] = ',',
        ["hyphen"] = '-', ["period"] = '.', ["slash"] = '/',
        ["colon"] = ':', ["semicolon"] = ';', ["less"] = '<',
        ["equal"] = '=', ["greater"] = '>', ["question"] = '?',
        ["at"] = '@',
        ["bracketleft"] = '[', ["backslash"] = '\\', ["bracketright"] = ']',
        ["asciicircum"] = '^', ["underscore"] = '_', ["quoteleft"] = '\u2018',
        ["braceleft"] = '{', ["bar"] = '|', ["braceright"] = '}',
        ["asciitilde"] = '~',
        // Latin-1 punctuation + symbols
        ["exclamdown"] = '\u00A1', ["cent"] = '\u00A2', ["sterling"] = '\u00A3',
        ["currency"] = '\u00A4', ["yen"] = '\u00A5', ["brokenbar"] = '\u00A6',
        ["section"] = '\u00A7', ["dieresis"] = '\u00A8', ["copyright"] = '\u00A9',
        ["ordfeminine"] = '\u00AA', ["guillemotleft"] = '\u00AB',
        ["logicalnot"] = '\u00AC', ["registered"] = '\u00AE',
        ["macron"] = '\u00AF', ["degree"] = '\u00B0', ["plusminus"] = '\u00B1',
        ["twosuperior"] = '\u00B2', ["threesuperior"] = '\u00B3',
        ["acute"] = '\u00B4', ["mu"] = '\u00B5', ["paragraph"] = '\u00B6',
        ["periodcentered"] = '\u00B7', ["cedilla"] = '\u00B8',
        ["onesuperior"] = '\u00B9', ["ordmasculine"] = '\u00BA',
        ["guillemotright"] = '\u00BB', ["onequarter"] = '\u00BC',
        ["onehalf"] = '\u00BD', ["threequarters"] = '\u00BE',
        ["questiondown"] = '\u00BF',
        // Latin-1 letters
        ["Agrave"] = '\u00C0', ["Aacute"] = '\u00C1', ["Acircumflex"] = '\u00C2',
        ["Atilde"] = '\u00C3', ["Adieresis"] = '\u00C4', ["Aring"] = '\u00C5',
        ["AE"] = '\u00C6', ["Ccedilla"] = '\u00C7',
        ["Egrave"] = '\u00C8', ["Eacute"] = '\u00C9', ["Ecircumflex"] = '\u00CA',
        ["Edieresis"] = '\u00CB',
        ["Igrave"] = '\u00CC', ["Iacute"] = '\u00CD', ["Icircumflex"] = '\u00CE',
        ["Idieresis"] = '\u00CF',
        ["Eth"] = '\u00D0', ["Ntilde"] = '\u00D1',
        ["Ograve"] = '\u00D2', ["Oacute"] = '\u00D3', ["Ocircumflex"] = '\u00D4',
        ["Otilde"] = '\u00D5', ["Odieresis"] = '\u00D6', ["multiply"] = '\u00D7',
        ["Oslash"] = '\u00D8',
        ["Ugrave"] = '\u00D9', ["Uacute"] = '\u00DA', ["Ucircumflex"] = '\u00DB',
        ["Udieresis"] = '\u00DC', ["Yacute"] = '\u00DD', ["Thorn"] = '\u00DE',
        ["germandbls"] = '\u00DF',
        ["agrave"] = '\u00E0', ["aacute"] = '\u00E1', ["acircumflex"] = '\u00E2',
        ["atilde"] = '\u00E3', ["adieresis"] = '\u00E4', ["aring"] = '\u00E5',
        ["ae"] = '\u00E6', ["ccedilla"] = '\u00E7',
        ["egrave"] = '\u00E8', ["eacute"] = '\u00E9', ["ecircumflex"] = '\u00EA',
        ["edieresis"] = '\u00EB',
        ["igrave"] = '\u00EC', ["iacute"] = '\u00ED', ["icircumflex"] = '\u00EE',
        ["idieresis"] = '\u00EF',
        ["eth"] = '\u00F0', ["ntilde"] = '\u00F1',
        ["ograve"] = '\u00F2', ["oacute"] = '\u00F3', ["ocircumflex"] = '\u00F4',
        ["otilde"] = '\u00F5', ["odieresis"] = '\u00F6', ["divide"] = '\u00F7',
        ["oslash"] = '\u00F8',
        ["ugrave"] = '\u00F9', ["uacute"] = '\u00FA', ["ucircumflex"] = '\u00FB',
        ["udieresis"] = '\u00FC', ["yacute"] = '\u00FD', ["thorn"] = '\u00FE',
        ["ydieresis"] = '\u00FF',
        // Additional common AGL entries used by StandardEncoding
        ["Ydieresis"] = '\u0178', ["OE"] = '\u0152', ["oe"] = '\u0153',
        ["Scaron"] = '\u0160', ["scaron"] = '\u0161',
        ["Zcaron"] = '\u017D', ["zcaron"] = '\u017E',
        ["Lslash"] = '\u0141', ["lslash"] = '\u0142',
        ["florin"] = '\u0192',
        ["circumflex"] = '\u02C6', ["tilde"] = '\u02DC', ["breve"] = '\u02D8',
        ["dotaccent"] = '\u02D9', ["ring"] = '\u02DA',
        ["hungarumlaut"] = '\u02DD', ["ogonek"] = '\u02DB', ["caron"] = '\u02C7',
        ["endash"] = '\u2013', ["emdash"] = '\u2014',
        ["quotedblleft"] = '\u201C', ["quotedblright"] = '\u201D',
        ["quotesinglbase"] = '\u201A', ["quotedblbase"] = '\u201E',
        ["ellipsis"] = '\u2026', ["dagger"] = '\u2020', ["daggerdbl"] = '\u2021',
        ["bullet"] = '\u2022',
        ["perthousand"] = '\u2030', ["guilsinglleft"] = '\u2039',
        ["guilsinglright"] = '\u203A',
        ["trademark"] = '\u2122', ["Euro"] = '\u20AC',
        ["fraction"] = '\u2044',
        ["grave"] = '\u0060', ["dotlessi"] = '\u0131',
        // Ligatures used by real PDF fonts
        ["fi"] = '\uFB01', ["fl"] = '\uFB02',
        ["ff"] = '\uFB00', ["ffi"] = '\uFB03', ["ffl"] = '\uFB04',
        // Math + misc that show up in MacRoman / symbol fonts
        ["notequal"] = '\u2260', ["infinity"] = '\u221E',
        ["lessequal"] = '\u2264', ["greaterequal"] = '\u2265',
        ["partialdiff"] = '\u2202', ["summation"] = '\u2211',
        ["product"] = '\u220F', ["pi"] = '\u03C0', ["integral"] = '\u222B',
        ["Omega"] = '\u03A9', ["radical"] = '\u221A',
        ["approxequal"] = '\u2248', ["Delta"] = '\u0394',
        ["lozenge"] = '\u25CA', ["apple"] = '\uF8FF',
    };
}
