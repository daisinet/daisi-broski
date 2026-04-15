using System.IO.Compression;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Decoders for PDF stream filters (PDF 1.7 §7.4). FlateDecode is
/// the only one needed for milestone 2 — the other filters
/// (ASCII85Decode, ASCIIHexDecode, LZWDecode, RunLengthDecode)
/// land in milestone 3 as we hit real PDFs that use them. The
/// public entry point ignores the filter name and returns the
/// input unchanged for any filter we don't recognize, so a PDF
/// whose content stream happens to carry a stripe of raw data
/// doesn't surface as a hard parse error — it just doesn't
/// extract text.
/// </summary>
internal static class PdfFilters
{
    /// <summary>Apply the <paramref name="filterChain"/>
    /// left-to-right to <paramref name="input"/>. The chain comes
    /// from the stream dictionary's <c>/Filter</c> entry, which is
    /// either a single name or an array. Filters unknown to this
    /// implementation are skipped (the caller sees partially
    /// decoded output); the method never throws for an unknown
    /// filter name.</summary>
    internal static byte[] Decode(
        byte[] input, IReadOnlyList<string> filterChain,
        IReadOnlyList<PdfDictionary?> parmsChain)
    {
        byte[] data = input;
        for (int i = 0; i < filterChain.Count; i++)
        {
            var name = filterChain[i];
            var parms = i < parmsChain.Count ? parmsChain[i] : null;
            data = name switch
            {
                "FlateDecode" or "Fl" => FlateDecode(data, parms),
                "ASCIIHexDecode" or "AHx" => AsciiHexDecode(data),
                "ASCII85Decode" or "A85" => Ascii85Decode(data),
                "LZWDecode" or "LZW" => LzwDecode(data, parms),
                "RunLengthDecode" or "RL" => RunLengthDecode(data),
                _ => data,
            };
        }
        return data;
    }

    /// <summary>zlib-wrapped DEFLATE. PDF streams include the
    /// 2-byte zlib header and adler32 trailer —
    /// <see cref="ZLibStream"/> handles both. Optionally applies a
    /// PNG-style predictor pass when
    /// <c>/DecodeParms /Predictor</c> is set to 10–15 (the only
    /// predictors FlateDecode supports in practice).</summary>
    internal static byte[] FlateDecode(byte[] input, PdfDictionary? parms)
    {
        byte[] inflated = Inflate(input);
        int predictor = GetIntEntry(parms, "Predictor", 1);
        if (predictor < 10) return inflated;
        int columns = GetIntEntry(parms, "Columns", 1);
        int colors = GetIntEntry(parms, "Colors", 1);
        int bpc = GetIntEntry(parms, "BitsPerComponent", 8);
        return PngPredictor(inflated, columns, colors, bpc);
    }

    /// <summary>Inflate via <see cref="ZLibStream"/>. The stream
    /// reader is lazy, so we buffer the full output into a
    /// <see cref="MemoryStream"/>.</summary>
    private static byte[] Inflate(byte[] input)
    {
        using var src = new MemoryStream(input, writable: false);
        using var zs = new ZLibStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        zs.CopyTo(dst);
        return dst.ToArray();
    }

    /// <summary>PNG-predictor unfilter for Flate streams that use
    /// predictor 10-15 ("up-to-and-including PNG filter N"). Row
    /// format: 1 tag byte (the filter type) + columns × bpp bytes.
    /// The tag byte selects one of five PNG filters: 0=none,
    /// 1=sub, 2=up, 3=average, 4=paeth. Output is the same rows
    /// with the tag byte stripped and the filter reversed.
    /// Non-PNG predictors (2 = TIFF) are not handled here because
    /// no real-world PDF content stream uses them.</summary>
    private static byte[] PngPredictor(byte[] input, int columns, int colors, int bpc)
    {
        int bpp = Math.Max(1, (colors * bpc + 7) / 8);
        int rowBytes = (columns * colors * bpc + 7) / 8;
        int stride = rowBytes + 1;
        if (stride <= 0 || input.Length == 0 || input.Length % stride != 0)
        {
            return input;
        }
        int rows = input.Length / stride;
        var output = new byte[rows * rowBytes];
        byte[] prev = new byte[rowBytes];
        for (int r = 0; r < rows; r++)
        {
            int srcRow = r * stride;
            int dstRow = r * rowBytes;
            byte filter = input[srcRow];
            for (int i = 0; i < rowBytes; i++)
            {
                byte raw = input[srcRow + 1 + i];
                byte left = i >= bpp ? output[dstRow + i - bpp] : (byte)0;
                byte up = prev[i];
                byte upLeft = i >= bpp ? prev[i - bpp] : (byte)0;
                byte recon = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (left + up) / 2),
                    4 => (byte)(raw + PaethPredictor(left, up, upLeft)),
                    _ => raw,
                };
                output[dstRow + i] = recon;
            }
            Array.Copy(output, dstRow, prev, 0, rowBytes);
        }
        return output;
    }

    private static int PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static int GetIntEntry(PdfDictionary? dict, string key, int defaultValue)
    {
        if (dict is null) return defaultValue;
        if (dict.Get(key) is PdfInt i) return (int)i.Value;
        return defaultValue;
    }

    /// <summary>ASCIIHexDecode (PDF 1.7 §7.4.2). Hex digit pairs
    /// become bytes; whitespace is skipped; <c>&gt;</c> ends the
    /// stream. An odd trailing nibble is padded with a zero.</summary>
    internal static byte[] AsciiHexDecode(byte[] input)
    {
        using var ms = new MemoryStream(input.Length / 2 + 1);
        int pending = -1;
        foreach (var b in input)
        {
            if (b == (byte)'>') break;
            if (PdfLexer.IsWhitespace(b)) continue;
            int v = PdfLexer.HexValue(b);
            if (v < 0) continue;
            if (pending < 0) pending = v;
            else
            {
                ms.WriteByte((byte)((pending << 4) | v));
                pending = -1;
            }
        }
        if (pending >= 0) ms.WriteByte((byte)(pending << 4));
        return ms.ToArray();
    }

    /// <summary>ASCII85Decode (PDF 1.7 §7.4.3). Five characters
    /// '!'..'u' encode four bytes in base 85 (offset 33). Special
    /// <c>z</c> means four zero bytes. Stream terminates at
    /// <c>~&gt;</c>. A short final group is padded with 'u' and
    /// truncated in the output.</summary>
    internal static byte[] Ascii85Decode(byte[] input)
    {
        using var ms = new MemoryStream(input.Length * 4 / 5 + 4);
        uint accum = 0;
        int count = 0;
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == (byte)'~')
            {
                // Expected '~>' — end of stream. Break regardless
                // of the second character; some producers drop it.
                break;
            }
            if (PdfLexer.IsWhitespace(b)) continue;
            if (b == (byte)'z' && count == 0)
            {
                ms.WriteByte(0); ms.WriteByte(0);
                ms.WriteByte(0); ms.WriteByte(0);
                continue;
            }
            if (b < (byte)'!' || b > (byte)'u') continue;
            accum = accum * 85 + (uint)(b - (byte)'!');
            count++;
            if (count == 5)
            {
                ms.WriteByte((byte)(accum >> 24));
                ms.WriteByte((byte)(accum >> 16));
                ms.WriteByte((byte)(accum >> 8));
                ms.WriteByte((byte)accum);
                accum = 0;
                count = 0;
            }
        }
        if (count > 0)
        {
            // Pad short group to 5 with the max digit ('u' = 84)
            // per spec; output (count-1) real bytes.
            for (int pad = count; pad < 5; pad++) accum = accum * 85 + 84;
            int emit = count - 1;
            if (emit >= 1) ms.WriteByte((byte)(accum >> 24));
            if (emit >= 2) ms.WriteByte((byte)(accum >> 16));
            if (emit >= 3) ms.WriteByte((byte)(accum >> 8));
            if (emit >= 4) ms.WriteByte((byte)accum);
        }
        return ms.ToArray();
    }

    /// <summary>RunLengthDecode (PDF 1.7 §7.4.5). Repeat-byte
    /// encoding used for simple binary data. Leading byte L: if
    /// L in 0..127, copy next L+1 bytes verbatim; if L in
    /// 129..255, repeat the next byte (2 - L + 256) = (257 - L)
    /// times; L == 128 is EOD.</summary>
    internal static byte[] RunLengthDecode(byte[] input)
    {
        using var ms = new MemoryStream(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            int len = input[i++];
            if (len == 128) break;
            if (len < 128)
            {
                int copyCount = len + 1;
                if (i + copyCount > input.Length) copyCount = input.Length - i;
                ms.Write(input, i, copyCount);
                i += copyCount;
            }
            else
            {
                int repeats = 257 - len;
                if (i >= input.Length) break;
                byte val = input[i++];
                for (int r = 0; r < repeats; r++) ms.WriteByte(val);
            }
        }
        return ms.ToArray();
    }

    /// <summary>LZWDecode (PDF 1.7 §7.4.4) — the TIFF-style
    /// variable-width LZW adapted by PDF. Codes start at 9 bits,
    /// grow to 12 bits. Code 256 clears the dictionary, 257 is
    /// EOD. <c>/EarlyChange</c> (default 1) advances the code
    /// width one code earlier than the strict spec, matching what
    /// Adobe and every real-world PDF writer actually produce.</summary>
    internal static byte[] LzwDecode(byte[] input, PdfDictionary? parms)
    {
        int earlyChange = GetIntEntry(parms, "EarlyChange", 1);
        const int clearCode = 256;
        const int eodCode = 257;
        // Output-size guard: real-world LZW decoded streams expand
        // at most ~100× the input. A higher ratio is almost always
        // a malformed stream or a fuzzer input that would run
        // unbounded. Bail out cleanly rather than keeping to fill
        // memory on hostile input.
        long outputCap = Math.Max(4096L, (long)input.Length * 100L);
        var dict = new List<byte[]>(4096);
        for (int i = 0; i < 256; i++) dict.Add(new[] { (byte)i });
        // Reserve entries 256 and 257.
        dict.Add(Array.Empty<byte>());
        dict.Add(Array.Empty<byte>());

        using var ms = new MemoryStream(input.Length * 2);
        int codeBits = 9;
        uint bitBuffer = 0;
        int bitsAvailable = 0;
        int inputPos = 0;
        int prevCode = -1;

        int ReadCode()
        {
            while (bitsAvailable < codeBits)
            {
                if (inputPos >= input.Length) return -1;
                bitBuffer = (bitBuffer << 8) | input[inputPos++];
                bitsAvailable += 8;
            }
            int shift = bitsAvailable - codeBits;
            int code = (int)((bitBuffer >> shift) & ((1u << codeBits) - 1));
            bitsAvailable -= codeBits;
            bitBuffer &= (1u << bitsAvailable) - 1;
            return code;
        }

        while (true)
        {
            int code = ReadCode();
            if (code < 0 || code == eodCode) break;
            if (code == clearCode)
            {
                dict.RemoveRange(258, dict.Count - 258);
                codeBits = 9;
                prevCode = -1;
                continue;
            }
            byte[] output;
            if (code < dict.Count)
            {
                output = dict[code];
            }
            else if (code == dict.Count && prevCode >= 0)
            {
                // Classic LZW special case: code not yet in
                // dictionary, build from previous + previous[0].
                var prev = dict[prevCode];
                output = new byte[prev.Length + 1];
                Array.Copy(prev, output, prev.Length);
                output[prev.Length] = prev[0];
            }
            else
            {
                break; // corrupt stream
            }
            ms.Write(output, 0, output.Length);
            if (ms.Length > outputCap) break; // guard hostile growth
            if (prevCode >= 0 && prevCode < dict.Count)
            {
                var prev = dict[prevCode];
                var entry = new byte[prev.Length + 1];
                Array.Copy(prev, entry, prev.Length);
                entry[prev.Length] = output[0];
                dict.Add(entry);
                int boundary = (1 << codeBits) - earlyChange;
                if (dict.Count >= boundary && codeBits < 12) codeBits++;
            }
            prevCode = code;
        }
        return ms.ToArray();
    }

    /// <summary>Extract the filter chain from a stream
    /// dictionary. Returns empty when <c>/Filter</c> is absent.
    /// A single name becomes a one-element list; an array
    /// becomes the list of names (non-name elements skipped).</summary>
    internal static IReadOnlyList<string> FilterChain(PdfDictionary dict)
    {
        if (!dict.Entries.TryGetValue("Filter", out var filter))
            return Array.Empty<string>();
        return filter switch
        {
            PdfName n => new[] { n.Value },
            PdfArray a => a.Items.OfType<PdfName>().Select(n => n.Value).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>Extract the per-filter parameter dictionaries
    /// aligned with <see cref="FilterChain"/>. Scalar
    /// <c>/DecodeParms</c> pairs with a single-filter chain;
    /// array <c>/DecodeParms</c> pairs positionally with an
    /// array <c>/Filter</c>.</summary>
    internal static IReadOnlyList<PdfDictionary?> ParmsChain(PdfDictionary dict)
    {
        if (!dict.Entries.TryGetValue("DecodeParms", out var parms))
            return Array.Empty<PdfDictionary?>();
        return parms switch
        {
            PdfDictionary d => new PdfDictionary?[] { d },
            PdfArray a => a.Items.Select(x => x as PdfDictionary).ToArray(),
            _ => Array.Empty<PdfDictionary?>(),
        };
    }
}
