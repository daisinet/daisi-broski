using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Parses a PDF <c>/ToUnicode</c> CMap — the inverse of an
/// encoding table, mapping raw character codes in a content
/// stream back to Unicode text. Required for composite (Type 0)
/// fonts and commonly attached to simple fonts that ship
/// custom glyph mappings (PDF 1.7 §9.10).
///
/// <para>A CMap is itself a small PostScript-esque program. We
/// read three sections: <c>begincodespacerange / endcodespacerange</c>
/// (declares the valid code widths), <c>bfchar / endbfchar</c>
/// (per-code entries), and <c>bfrange / endbfrange</c> (a range
/// of codes maps to a starting Unicode value that increments per
/// code, or to an array of target strings).</para>
/// </summary>
internal sealed class PdfCMap
{
    private readonly List<CodeSpaceRange> _ranges = new();
    private readonly Dictionary<int, string> _bfChar = new();
    private readonly List<BfRange> _bfRanges = new();

    /// <summary>Greatest code-space width (in bytes) declared by
    /// the CMap. Controls how many bytes the text extractor
    /// consumes per glyph from the content stream.</summary>
    public int MaxCodeLength { get; private set; } = 1;

    /// <summary>Try to map a code (already parsed out of the
    /// content stream as an integer) to Unicode. Returns null
    /// when nothing in the CMap covers it — the caller can then
    /// either fall back to a simple-encoding lookup or drop the
    /// glyph.</summary>
    public string? Map(int code)
    {
        if (_bfChar.TryGetValue(code, out var s)) return s;
        foreach (var range in _bfRanges)
        {
            if (code < range.Lo || code > range.Hi) continue;
            if (range.Targets is not null)
            {
                int idx = code - range.Lo;
                return idx < range.Targets.Count ? range.Targets[idx] : null;
            }
            if (range.StartTarget is not null)
            {
                return IncrementTrailingChar(range.StartTarget, code - range.Lo);
            }
        }
        return null;
    }

    /// <summary>Return the byte-width(s) valid for the next code
    /// in a content stream. Text extractor consumes this many
    /// bytes per glyph when decoding through this CMap.</summary>
    public IReadOnlyList<CodeSpaceRange> CodeSpaceRanges => _ranges;

    /// <summary>Parse a CMap stream's decoded bytes. Never
    /// throws for unknown constructs — unrecognized lines are
    /// skipped so a novel producer's CMap still yields whatever
    /// entries we can recognize.</summary>
    internal static PdfCMap Parse(byte[] bytes)
    {
        var map = new PdfCMap();
        var lexer = new PdfLexer(bytes);
        var parser = new PdfParser(lexer);
        // CMap "parsing" is best-effort: a mangled stream should
        // yield whatever entries we were able to recognize, not
        // take down the whole page. Tokenizer and parser errors
        // alike get swallowed here so the caller sees an empty-
        // ish map instead of an exception.
        try
        {
            while (true)
            {
                var tok = lexer.PeekToken();
                if (tok is null) break;
                if (tok.Kind != PdfTokenKind.Keyword)
                {
                    // Skip anything that isn't an operator.
                    lexer.NextToken();
                    continue;
                }
                lexer.NextToken();
                switch (tok.Text)
                {
                    case "beginbfchar":
                        ReadBfChar(parser, map);
                        break;
                    case "beginbfrange":
                        ReadBfRange(parser, map);
                        break;
                    case "begincodespacerange":
                        ReadCodeSpaceRange(parser, map);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (InvalidDataException) { /* best-effort */ }
        catch (FormatException) { /* best-effort */ }
        catch (OverflowException) { /* best-effort */ }
        return map;
    }

    private static void ReadCodeSpaceRange(PdfParser parser, PdfCMap map)
    {
        while (true)
        {
            var tok = parser.Lexer.PeekToken();
            if (tok is { Kind: PdfTokenKind.Keyword, Text: "endcodespacerange" })
            {
                parser.Lexer.NextToken();
                return;
            }
            if (tok is null) return;
            var lo = parser.ReadObject() as PdfString;
            var hi = parser.ReadObject() as PdfString;
            if (lo is null || hi is null) continue;
            int loInt = IntFromBytes(lo.Bytes);
            int hiInt = IntFromBytes(hi.Bytes);
            int width = Math.Max(lo.Bytes.Length, hi.Bytes.Length);
            map._ranges.Add(new CodeSpaceRange(loInt, hiInt, width));
            if (width > map.MaxCodeLength) map.MaxCodeLength = width;
        }
    }

    private static void ReadBfChar(PdfParser parser, PdfCMap map)
    {
        while (true)
        {
            var tok = parser.Lexer.PeekToken();
            if (tok is { Kind: PdfTokenKind.Keyword, Text: "endbfchar" })
            {
                parser.Lexer.NextToken();
                return;
            }
            if (tok is null) return;
            var srcObj = parser.ReadObject();
            var dstObj = parser.ReadObject();
            if (srcObj is not PdfString src || dstObj is not PdfString dst) continue;
            map._bfChar[IntFromBytes(src.Bytes)] = DecodeUnicodeBytes(dst.Bytes);
        }
    }

    private static void ReadBfRange(PdfParser parser, PdfCMap map)
    {
        while (true)
        {
            var tok = parser.Lexer.PeekToken();
            if (tok is { Kind: PdfTokenKind.Keyword, Text: "endbfrange" })
            {
                parser.Lexer.NextToken();
                return;
            }
            if (tok is null) return;
            var loObj = parser.ReadObject();
            var hiObj = parser.ReadObject();
            var dstObj = parser.ReadObject();
            if (loObj is not PdfString lo || hiObj is not PdfString hi) continue;
            int loInt = IntFromBytes(lo.Bytes);
            int hiInt = IntFromBytes(hi.Bytes);
            if (dstObj is PdfString dst)
            {
                map._bfRanges.Add(new BfRange(loInt, hiInt,
                    StartTarget: DecodeUnicodeBytes(dst.Bytes),
                    Targets: null));
            }
            else if (dstObj is PdfArray arr)
            {
                var list = new List<string>(arr.Count);
                foreach (var item in arr.Items)
                {
                    if (item is PdfString s) list.Add(DecodeUnicodeBytes(s.Bytes));
                    else list.Add("");
                }
                map._bfRanges.Add(new BfRange(loInt, hiInt,
                    StartTarget: null, Targets: list));
            }
        }
    }

    /// <summary>Pack up to 4 bytes into a big-endian integer —
    /// the canonical way CMap source code widths are represented.
    /// Longer keys (8+ byte CIDs for CJK Surrogate systems) are
    /// rare enough we punt on them in this milestone.</summary>
    private static int IntFromBytes(byte[] bytes)
    {
        int v = 0;
        foreach (var b in bytes) v = (v << 8) | b;
        return v;
    }

    /// <summary>Decode a hex-string literal from a CMap
    /// destination — those are always UTF-16BE big-endian char
    /// codes per spec. Two bytes yield one UTF-16 code unit; a
    /// final odd byte is ignored.</summary>
    private static string DecodeUnicodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        var sb = new StringBuilder(bytes.Length / 2);
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            char c = (char)((bytes[i] << 8) | bytes[i + 1]);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Compute a bfrange entry's Unicode target by
    /// incrementing the final UTF-16 code unit of
    /// <paramref name="start"/> by <paramref name="delta"/>.
    /// Matches Adobe's spec for bfrange with a single starting
    /// target: "the first byte of the source code maps to the
    /// string; subsequent source codes map to strings derived by
    /// incrementing the last byte of the starting string."</summary>
    private static string IncrementTrailingChar(string start, int delta)
    {
        if (delta == 0 || start.Length == 0) return start;
        var chars = start.ToCharArray();
        int last = chars.Length - 1;
        int v = chars[last] + delta;
        if (v < 0 || v > 0xFFFF) return start;
        chars[last] = (char)v;
        return new string(chars);
    }

    internal readonly record struct CodeSpaceRange(int Lo, int Hi, int Width);

    private readonly record struct BfRange(
        int Lo, int Hi, string? StartTarget, List<string>? Targets);
}
