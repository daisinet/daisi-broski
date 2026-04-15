namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Byte code → glyph name tables for the three standard encodings
/// PDF Font objects can reference (PDF 1.7 Annex D.1). Each table
/// is 256 entries; the empty string at a position means ".notdef"
/// (no glyph assigned at that position in the encoding). Names
/// resolved through <see cref="GlyphList"/> to get their Unicode.
///
/// <para>StandardEncoding is the default for simple Type1 fonts
/// that don't supply their own <c>/Encoding</c>. WinAnsiEncoding
/// is the Windows-1252-derived encoding used by the vast majority
/// of modern digital-PDF producers (Word, Google Docs, Chrome's
/// PDF printer). MacRomanEncoding covers the older Mac Office
/// pipeline.</para>
/// </summary>
internal static class StandardEncodings
{
    internal static readonly string[] Standard = BuildStandard();
    internal static readonly string[] WinAnsi = BuildWinAnsi();
    internal static readonly string[] MacRoman = BuildMacRoman();

    private static string[] BuildStandard()
    {
        var t = new string[256];
        // 32..126: same as ASCII for most printable characters,
        // with a handful of Standard-specific names.
        t[32] = "space";      t[33] = "exclam";    t[34] = "quotedbl";
        t[35] = "numbersign"; t[36] = "dollar";    t[37] = "percent";
        t[38] = "ampersand";  t[39] = "quoteright"; t[40] = "parenleft";
        t[41] = "parenright"; t[42] = "asterisk";  t[43] = "plus";
        t[44] = "comma";      t[45] = "hyphen";    t[46] = "period";
        t[47] = "slash";
        for (int i = 0; i <= 9; i++)
            t[48 + i] = DigitName(i);
        t[58] = "colon";      t[59] = "semicolon"; t[60] = "less";
        t[61] = "equal";      t[62] = "greater";   t[63] = "question";
        t[64] = "at";
        for (int i = 0; i < 26; i++)
            t[65 + i] = ((char)('A' + i)).ToString();
        t[91] = "bracketleft"; t[92] = "backslash"; t[93] = "bracketright";
        t[94] = "asciicircum"; t[95] = "underscore"; t[96] = "quoteleft";
        for (int i = 0; i < 26; i++)
            t[97 + i] = ((char)('a' + i)).ToString();
        t[123] = "braceleft"; t[124] = "bar";      t[125] = "braceright";
        t[126] = "asciitilde";
        // 161+: Standard-specific names
        t[161] = "exclamdown"; t[162] = "cent";    t[163] = "sterling";
        t[164] = "fraction";   t[165] = "yen";     t[166] = "florin";
        t[167] = "section";    t[168] = "currency"; t[169] = "quotesingle";
        t[170] = "quotedblleft"; t[171] = "guillemotleft"; t[172] = "guilsinglleft";
        t[173] = "guilsinglright"; t[174] = "fi";  t[175] = "fl";
        t[177] = "endash";     t[178] = "dagger";  t[179] = "daggerdbl";
        t[180] = "periodcentered"; t[182] = "paragraph"; t[183] = "bullet";
        t[184] = "quotesinglbase"; t[185] = "quotedblbase"; t[186] = "quotedblright";
        t[187] = "guillemotright"; t[188] = "ellipsis"; t[189] = "perthousand";
        t[191] = "questiondown";
        t[193] = "grave";      t[194] = "acute";   t[195] = "circumflex";
        t[196] = "tilde";      t[197] = "macron";  t[198] = "breve";
        t[199] = "dotaccent";  t[200] = "dieresis"; t[202] = "ring";
        t[203] = "cedilla";    t[205] = "hungarumlaut"; t[206] = "ogonek";
        t[207] = "caron";
        t[225] = "AE";         t[227] = "ordfeminine"; t[232] = "Lslash";
        t[233] = "Oslash";     t[234] = "OE";      t[235] = "ordmasculine";
        t[241] = "ae";         t[245] = "dotlessi"; t[248] = "lslash";
        t[249] = "oslash";     t[250] = "oe";      t[251] = "germandbls";
        NormalizeGaps(t);
        return t;
    }

    private static string[] BuildWinAnsi()
    {
        var t = new string[256];
        // 32..126: same names as Standard.
        for (int i = 32; i <= 126; i++)
        {
            // WinAnsi uses "quotesingle" at 39 instead of
            // "quoteright".
            t[i] = i == 39 ? "quotesingle" : ResolveAscii(i);
        }
        // 128..159: Windows-1252's "extra" region.
        t[128] = "Euro";        t[130] = "quotesinglbase";
        t[131] = "florin";      t[132] = "quotedblbase";
        t[133] = "ellipsis";    t[134] = "dagger";
        t[135] = "daggerdbl";   t[136] = "circumflex";
        t[137] = "perthousand"; t[138] = "Scaron";
        t[139] = "guilsinglleft"; t[140] = "OE";
        t[142] = "Zcaron";
        t[145] = "quoteleft";   t[146] = "quoteright";
        t[147] = "quotedblleft"; t[148] = "quotedblright";
        t[149] = "bullet";      t[150] = "endash";
        t[151] = "emdash";      t[152] = "tilde";
        t[153] = "trademark";   t[154] = "scaron";
        t[155] = "guilsinglright"; t[156] = "oe";
        t[158] = "zcaron";      t[159] = "Ydieresis";
        // 160..255: Latin-1 direct name mapping (Latin-1 byte = codepoint).
        t[160] = "space";       t[161] = "exclamdown";
        t[162] = "cent";        t[163] = "sterling";
        t[164] = "currency";    t[165] = "yen";
        t[166] = "brokenbar";   t[167] = "section";
        t[168] = "dieresis";    t[169] = "copyright";
        t[170] = "ordfeminine"; t[171] = "guillemotleft";
        t[172] = "logicalnot";  t[173] = "hyphen";
        t[174] = "registered";  t[175] = "macron";
        t[176] = "degree";      t[177] = "plusminus";
        t[178] = "twosuperior"; t[179] = "threesuperior";
        t[180] = "acute";       t[181] = "mu";
        t[182] = "paragraph";   t[183] = "periodcentered";
        t[184] = "cedilla";     t[185] = "onesuperior";
        t[186] = "ordmasculine"; t[187] = "guillemotright";
        t[188] = "onequarter";  t[189] = "onehalf";
        t[190] = "threequarters"; t[191] = "questiondown";
        t[192] = "Agrave";      t[193] = "Aacute";
        t[194] = "Acircumflex"; t[195] = "Atilde";
        t[196] = "Adieresis";   t[197] = "Aring";
        t[198] = "AE";          t[199] = "Ccedilla";
        t[200] = "Egrave";      t[201] = "Eacute";
        t[202] = "Ecircumflex"; t[203] = "Edieresis";
        t[204] = "Igrave";      t[205] = "Iacute";
        t[206] = "Icircumflex"; t[207] = "Idieresis";
        t[208] = "Eth";         t[209] = "Ntilde";
        t[210] = "Ograve";      t[211] = "Oacute";
        t[212] = "Ocircumflex"; t[213] = "Otilde";
        t[214] = "Odieresis";   t[215] = "multiply";
        t[216] = "Oslash";      t[217] = "Ugrave";
        t[218] = "Uacute";      t[219] = "Ucircumflex";
        t[220] = "Udieresis";   t[221] = "Yacute";
        t[222] = "Thorn";       t[223] = "germandbls";
        t[224] = "agrave";      t[225] = "aacute";
        t[226] = "acircumflex"; t[227] = "atilde";
        t[228] = "adieresis";   t[229] = "aring";
        t[230] = "ae";          t[231] = "ccedilla";
        t[232] = "egrave";      t[233] = "eacute";
        t[234] = "ecircumflex"; t[235] = "edieresis";
        t[236] = "igrave";      t[237] = "iacute";
        t[238] = "icircumflex"; t[239] = "idieresis";
        t[240] = "eth";         t[241] = "ntilde";
        t[242] = "ograve";      t[243] = "oacute";
        t[244] = "ocircumflex"; t[245] = "otilde";
        t[246] = "odieresis";   t[247] = "divide";
        t[248] = "oslash";      t[249] = "ugrave";
        t[250] = "uacute";      t[251] = "ucircumflex";
        t[252] = "udieresis";   t[253] = "yacute";
        t[254] = "thorn";       t[255] = "ydieresis";
        NormalizeGaps(t);
        return t;
    }

    private static string[] BuildMacRoman()
    {
        var t = new string[256];
        for (int i = 32; i <= 126; i++) t[i] = ResolveAscii(i);
        // MacRoman specifics 128..255 — a subset covering the
        // letters that show up in real MacRoman-encoded text.
        t[128] = "Adieresis"; t[129] = "Aring";   t[130] = "Ccedilla";
        t[131] = "Eacute";    t[132] = "Ntilde";  t[133] = "Odieresis";
        t[134] = "Udieresis"; t[135] = "aacute";  t[136] = "agrave";
        t[137] = "acircumflex"; t[138] = "adieresis"; t[139] = "atilde";
        t[140] = "aring";     t[141] = "ccedilla"; t[142] = "eacute";
        t[143] = "egrave";    t[144] = "ecircumflex"; t[145] = "edieresis";
        t[146] = "iacute";    t[147] = "igrave";  t[148] = "icircumflex";
        t[149] = "idieresis"; t[150] = "ntilde";  t[151] = "oacute";
        t[152] = "ograve";    t[153] = "ocircumflex"; t[154] = "odieresis";
        t[155] = "otilde";    t[156] = "uacute";  t[157] = "ugrave";
        t[158] = "ucircumflex"; t[159] = "udieresis";
        t[160] = "dagger";    t[161] = "degree";  t[162] = "cent";
        t[163] = "sterling";  t[164] = "section"; t[165] = "bullet";
        t[166] = "paragraph"; t[167] = "germandbls"; t[168] = "registered";
        t[169] = "copyright"; t[170] = "trademark"; t[171] = "acute";
        t[172] = "dieresis";  t[173] = "notequal"; t[174] = "AE";
        t[175] = "Oslash";    t[176] = "infinity"; t[177] = "plusminus";
        t[178] = "lessequal"; t[179] = "greaterequal"; t[180] = "yen";
        t[181] = "mu";        t[182] = "partialdiff"; t[183] = "summation";
        t[184] = "product";   t[185] = "pi";      t[186] = "integral";
        t[187] = "ordfeminine"; t[188] = "ordmasculine"; t[189] = "Omega";
        t[190] = "ae";        t[191] = "oslash";  t[192] = "questiondown";
        t[193] = "exclamdown"; t[194] = "logicalnot"; t[195] = "radical";
        t[196] = "florin";    t[197] = "approxequal"; t[198] = "Delta";
        t[199] = "guillemotleft"; t[200] = "guillemotright"; t[201] = "ellipsis";
        t[202] = "space";     t[203] = "Agrave";  t[204] = "Atilde";
        t[205] = "Otilde";    t[206] = "OE";      t[207] = "oe";
        t[208] = "endash";    t[209] = "emdash";  t[210] = "quotedblleft";
        t[211] = "quotedblright"; t[212] = "quoteleft"; t[213] = "quoteright";
        t[214] = "divide";    t[215] = "lozenge"; t[216] = "ydieresis";
        t[217] = "Ydieresis"; t[218] = "fraction"; t[219] = "currency";
        t[220] = "guilsinglleft"; t[221] = "guilsinglright"; t[222] = "fi";
        t[223] = "fl";        t[224] = "daggerdbl"; t[225] = "periodcentered";
        t[226] = "quotesinglbase"; t[227] = "quotedblbase"; t[228] = "perthousand";
        t[229] = "Acircumflex"; t[230] = "Ecircumflex"; t[231] = "Aacute";
        t[232] = "Edieresis"; t[233] = "Egrave";  t[234] = "Iacute";
        t[235] = "Icircumflex"; t[236] = "Idieresis"; t[237] = "Igrave";
        t[238] = "Oacute";    t[239] = "Ocircumflex"; t[240] = "apple";
        t[241] = "Ograve";    t[242] = "Uacute";  t[243] = "Ucircumflex";
        t[244] = "Ugrave";    t[245] = "dotlessi"; t[246] = "circumflex";
        t[247] = "tilde";     t[248] = "macron";  t[249] = "breve";
        t[250] = "dotaccent"; t[251] = "ring";    t[252] = "cedilla";
        t[253] = "hungarumlaut"; t[254] = "ogonek"; t[255] = "caron";
        NormalizeGaps(t);
        return t;
    }

    private static string DigitName(int i) => i switch
    {
        0 => "zero", 1 => "one", 2 => "two", 3 => "three", 4 => "four",
        5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => ".notdef",
    };

    /// <summary>ASCII-range resolution used by all three tables
    /// (positions 32-126 are spec-identical apart from 39). Uses
    /// AGL-canonical names so the downstream name→Unicode step
    /// works unchanged.</summary>
    private static string ResolveAscii(int i) => i switch
    {
        32 => "space",      33 => "exclam",    34 => "quotedbl",
        35 => "numbersign", 36 => "dollar",    37 => "percent",
        38 => "ampersand",  39 => "quoteright", 40 => "parenleft",
        41 => "parenright", 42 => "asterisk",  43 => "plus",
        44 => "comma",      45 => "hyphen",    46 => "period",
        47 => "slash",
        >= 48 and <= 57 => DigitName(i - 48),
        58 => "colon",      59 => "semicolon", 60 => "less",
        61 => "equal",      62 => "greater",   63 => "question",
        64 => "at",
        >= 65 and <= 90 => ((char)i).ToString(),
        91 => "bracketleft", 92 => "backslash", 93 => "bracketright",
        94 => "asciicircum", 95 => "underscore", 96 => "quoteleft",
        >= 97 and <= 122 => ((char)i).ToString(),
        123 => "braceleft", 124 => "bar",      125 => "braceright",
        126 => "asciitilde",
        _ => "",
    };

    /// <summary>Fill any unset slots with the sentinel empty
    /// string so callers can uniformly check
    /// <c>!string.IsNullOrEmpty</c> for "has a glyph here".</summary>
    private static void NormalizeGaps(string[] t)
    {
        for (int i = 0; i < t.Length; i++) t[i] ??= "";
    }

    /// <summary>Look up an encoding by PDF name. Returns null for
    /// unknown encodings (the caller falls back to Standard
    /// encoding).</summary>
    internal static string[]? ByName(string name) => name switch
    {
        "StandardEncoding" => Standard,
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        _ => null,
    };
}
