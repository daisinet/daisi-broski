using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Decodes a PDF-font-encoded byte string to Unicode. Handles
/// both "simple" fonts (one byte per glyph, PDF 1.7 §9.6) and
/// composite "Type 0" fonts (multi-byte codes driven by a
/// <c>/ToUnicode</c> CMap, §9.10). The extractor always calls
/// into a single <see cref="Decode"/> method; the font is the one
/// that decides how many bytes a code is wide and how to map it.
/// </summary>
internal sealed class PdfFont
{
    private readonly string[] _codeToName;
    private readonly PdfCMap? _toUnicode;
    private readonly int _codeWidth;
    private readonly Dictionary<int, double> _widths;
    private readonly double _defaultWidth;

    /// <summary>Human-readable label (BaseFont name, useful for
    /// debugging). Not used for decoding.</summary>
    public string Name { get; }

    private PdfFont(
        string name, string[] codeToName, PdfCMap? toUnicode, int codeWidth,
        Dictionary<int, double> widths, double defaultWidth)
    {
        Name = name;
        _codeToName = codeToName;
        _toUnicode = toUnicode;
        _codeWidth = codeWidth;
        _widths = widths;
        _defaultWidth = defaultWidth;
    }

    /// <summary>Horizontal advance in text-space units for the
    /// given byte string. The caller multiplies by
    /// <c>fontSize / 1000</c> and adds to <c>TextX</c> after each
    /// show operator. Simple fonts consume one byte per code;
    /// composite fonts consume <see cref="_codeWidth"/> bytes.
    /// Missing widths use <see cref="_defaultWidth"/>.</summary>
    public double MeasureAdvance(byte[] bytes)
    {
        double total = 0;
        int i = 0;
        while (i < bytes.Length)
        {
            int width = Math.Min(_codeWidth, bytes.Length - i);
            int code = 0;
            for (int k = 0; k < width; k++)
                code = (code << 8) | bytes[i + k];
            total += _widths.TryGetValue(code, out var w) ? w : _defaultWidth;
            i += width;
        }
        return total;
    }

    /// <summary>Decode a content-stream byte string through this
    /// font. Single-byte code widths go through the encoding +
    /// AGL lookup; wider codes go through the ToUnicode CMap.
    /// ToUnicode entries win over AGL lookups when both exist.</summary>
    public string Decode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            int width = Math.Min(_codeWidth, bytes.Length - i);
            int code = 0;
            for (int k = 0; k < width; k++)
            {
                code = (code << 8) | bytes[i + k];
            }
            // Prefer ToUnicode when present (it's the authoritative
            // spec-declared mapping; the encoding table is only a
            // fallback for simple fonts without one).
            string? mapped = _toUnicode?.Map(code);
            if (mapped is not null)
            {
                sb.Append(mapped);
            }
            else if (_codeWidth == 1 && i < bytes.Length)
            {
                string name = _codeToName[bytes[i]];
                if (!string.IsNullOrEmpty(name))
                {
                    int cp = GlyphList.Lookup(name);
                    if (cp >= 0)
                    {
                        if (cp <= 0xFFFF) sb.Append((char)cp);
                        else sb.Append(char.ConvertFromUtf32(cp));
                    }
                }
            }
            i += width;
        }
        return sb.ToString();
    }

    /// <summary>Build a font decoder from a font dictionary.
    /// Dispatches on <c>/Subtype</c>: Type 0 composite fonts
    /// build from <c>/DescendantFonts</c> + <c>/ToUnicode</c>;
    /// everything else (Type 1, TrueType, Type 3, MMType1) goes
    /// through the simple-font path with
    /// <c>/Encoding</c> + <c>/Differences</c> + optional
    /// <c>/ToUnicode</c>.</summary>
    public static PdfFont FromDictionary(
        PdfDictionary fontDict, Func<PdfObject?, PdfObject?> resolve)
    {
        string baseFont = (fontDict.Get("BaseFont") as PdfName)?.Value ?? "(anonymous)";
        string subtype = (fontDict.Get("Subtype") as PdfName)?.Value ?? "Type1";
        var toUnicode = ReadToUnicode(fontDict, resolve);

        if (subtype == "Type0")
        {
            int width = toUnicode?.CodeSpaceRanges.Count > 0
                ? toUnicode.MaxCodeLength : 2;
            var (cidWidths, cidDefault) = ReadCidWidths(fontDict, resolve);
            return new PdfFont(baseFont,
                codeToName: Array.Empty<string>(),
                toUnicode: toUnicode,
                codeWidth: width,
                widths: cidWidths,
                defaultWidth: cidDefault);
        }

        string[] baseTable = SelectBaseEncoding(
            fontDict, resolve, baseFont, out PdfDictionary? differencesDict);
        string[] table = ApplyDifferences(baseTable, differencesDict, resolve);
        var (simpleWidths, simpleDefault) = ReadSimpleWidths(
            fontDict, resolve, baseFont);
        return new PdfFont(baseFont,
            codeToName: table,
            toUnicode: toUnicode,
            codeWidth: 1,
            widths: simpleWidths,
            defaultWidth: simpleDefault);
    }

    /// <summary>Read the <c>/Widths</c> array for a simple font.
    /// The array starts at <c>/FirstChar</c> and runs through
    /// <c>/LastChar</c>; each entry is the advance in glyph-space
    /// units (1/1000 of text-space). Fonts without an explicit
    /// Widths array fall back to <see cref="StandardFontAverageWidth"/>
    /// so we at least advance by a sensible amount.</summary>
    private static (Dictionary<int, double>, double) ReadSimpleWidths(
        PdfDictionary fontDict, Func<PdfObject?, PdfObject?> resolve,
        string baseFont)
    {
        var widths = new Dictionary<int, double>();
        double def = StandardFontAverageWidth(baseFont);
        int firstChar = (resolve(fontDict.Get("FirstChar")) as PdfInt)?.Value is long fc
            ? (int)fc : 0;
        if (resolve(fontDict.Get("Widths")) is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (resolve(arr.Items[i]) is PdfInt w) widths[firstChar + i] = w.Value;
                else if (resolve(arr.Items[i]) is PdfReal r) widths[firstChar + i] = r.Value;
            }
        }
        // /FontDescriptor /MissingWidth overrides the default when present.
        if (resolve(fontDict.Get("FontDescriptor")) is PdfDictionary fd
            && resolve(fd.Get("MissingWidth")) is PdfInt mw)
        {
            def = mw.Value;
        }
        return (widths, def);
    }

    /// <summary>Read the <c>/W</c> array from a composite font's
    /// descendant CID font dict. The spec allows two shapes:
    /// <c>[cid [w w w]]</c> (list of widths starting at cid) and
    /// <c>[cidFirst cidLast w]</c> (range mapped to a single w).
    /// <c>/DW</c> holds the default for CIDs not otherwise listed.</summary>
    private static (Dictionary<int, double>, double) ReadCidWidths(
        PdfDictionary fontDict, Func<PdfObject?, PdfObject?> resolve)
    {
        var widths = new Dictionary<int, double>();
        double def = 1000;
        if (resolve(fontDict.Get("DescendantFonts")) is PdfArray descArr
            && descArr.Count > 0
            && resolve(descArr.Items[0]) is PdfDictionary descFont)
        {
            if (resolve(descFont.Get("DW")) is PdfInt dw) def = dw.Value;
            if (resolve(descFont.Get("W")) is PdfArray w)
            {
                int i = 0;
                while (i < w.Count)
                {
                    var first = resolve(w.Items[i]);
                    if (first is not PdfInt f) { i++; continue; }
                    if (i + 1 >= w.Count) break;
                    var next = resolve(w.Items[i + 1]);
                    if (next is PdfArray widthsArr)
                    {
                        for (int j = 0; j < widthsArr.Count; j++)
                        {
                            var v = resolve(widthsArr.Items[j]);
                            double val = v is PdfInt vi ? vi.Value
                                : v is PdfReal vr ? vr.Value : def;
                            widths[(int)f.Value + j] = val;
                        }
                        i += 2;
                    }
                    else if (next is PdfInt last && i + 2 < w.Count
                             && resolve(w.Items[i + 2]) is { } rangeW)
                    {
                        double val = rangeW is PdfInt ri ? ri.Value
                            : rangeW is PdfReal rr ? rr.Value : def;
                        for (int cid = (int)f.Value; cid <= (int)last.Value; cid++)
                            widths[cid] = val;
                        i += 3;
                    }
                    else { i++; }
                }
            }
        }
        return (widths, def);
    }

    /// <summary>Heuristic default width for the Standard-14
    /// fonts when /Widths isn't shipped. Helvetica, Times, and
    /// Courier have different averages; we return a plausible
    /// middle-ground so text extraction still advances.</summary>
    private static double StandardFontAverageWidth(string baseFont)
    {
        if (baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase))
            return 600;  // monospace
        if (baseFont.Contains("Times", StringComparison.OrdinalIgnoreCase))
            return 500;
        return 500;  // Helvetica + fallback
    }

    /// <summary>Read and parse the <c>/ToUnicode</c> stream when
    /// present. Returns null when the font has no CMap (simple
    /// fonts without one, which is most of the Standard-14 ones,
    /// fall back to the encoding + AGL path).</summary>
    private static PdfCMap? ReadToUnicode(
        PdfDictionary fontDict, Func<PdfObject?, PdfObject?> resolve)
    {
        var tu = resolve(fontDict.Get("ToUnicode"));
        if (tu is not PdfStream stream) return null;
        try
        {
            var chain = PdfFilters.FilterChain(stream.Dictionary);
            var parms = PdfFilters.ParmsChain(stream.Dictionary);
            var bytes = chain.Count == 0
                ? stream.RawBytes
                : PdfFilters.Decode(stream.RawBytes, chain, parms);
            return PdfCMap.Parse(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string[] SelectBaseEncoding(
        PdfDictionary fontDict, Func<PdfObject?, PdfObject?> resolve,
        string baseFont, out PdfDictionary? differencesDict)
    {
        differencesDict = null;
        var enc = resolve(fontDict.Get("Encoding"));
        if (enc is PdfName n)
        {
            return StandardEncodings.ByName(n.Value)
                ?? FontDefaultEncoding(baseFont);
        }
        if (enc is PdfDictionary d)
        {
            differencesDict = d;
            var baseName = (resolve(d.Get("BaseEncoding")) as PdfName)?.Value;
            return (baseName is not null ? StandardEncodings.ByName(baseName) : null)
                ?? FontDefaultEncoding(baseFont);
        }
        return FontDefaultEncoding(baseFont);
    }

    private static string[] ApplyDifferences(
        string[] baseTable, PdfDictionary? dict,
        Func<PdfObject?, PdfObject?> resolve)
    {
        var result = (string[])baseTable.Clone();
        if (dict is null) return result;
        var diffObj = resolve(dict.Get("Differences"));
        if (diffObj is not PdfArray diffs) return result;
        int cursor = 0;
        foreach (var item in diffs.Items)
        {
            var resolved = resolve(item);
            switch (resolved)
            {
                case PdfInt i:
                    cursor = (int)i.Value;
                    break;
                case PdfName name:
                    if (cursor >= 0 && cursor < 256)
                    {
                        result[cursor] = name.Value;
                    }
                    cursor++;
                    break;
            }
        }
        return result;
    }

    private static string[] FontDefaultEncoding(string baseFont)
    {
        if (baseFont.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || baseFont.Contains("Dingbat", StringComparison.OrdinalIgnoreCase))
        {
            return new string[256];
        }
        return StandardEncodings.Standard;
    }
}
