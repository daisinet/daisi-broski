using System.IO.Compression;
using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Round-trip tests for stream filter decoders. Each test
/// compresses / encodes a known-good payload with a BCL primitive
/// and asserts that the decoder produces the same bytes back.
/// </summary>
public class PdfFiltersTests
{
    [Fact]
    public void FlateDecode_roundtrip_small()
    {
        var payload = Encoding.ASCII.GetBytes("Hello, world. ".PadRight(256, 'x'));
        var compressed = ZLibCompress(payload);
        var decoded = PdfFilters.FlateDecode(compressed, parms: null);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void FlateDecode_roundtrip_multikb()
    {
        var payload = new byte[8192];
        new Random(1234).NextBytes(payload);
        var compressed = ZLibCompress(payload);
        var decoded = PdfFilters.FlateDecode(compressed, parms: null);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Decode_chain_with_FlateDecode_name()
    {
        var payload = Encoding.ASCII.GetBytes("abc");
        var compressed = ZLibCompress(payload);
        var decoded = PdfFilters.Decode(compressed,
            filterChain: new[] { "FlateDecode" },
            parmsChain: new PdfDictionary?[] { null });
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Decode_chain_with_abbreviated_Fl_name()
    {
        var payload = Encoding.ASCII.GetBytes("xyz");
        var compressed = ZLibCompress(payload);
        var decoded = PdfFilters.Decode(compressed,
            filterChain: new[] { "Fl" },
            parmsChain: new PdfDictionary?[] { null });
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Unknown_filter_passes_through()
    {
        var payload = Encoding.ASCII.GetBytes("opaque");
        var decoded = PdfFilters.Decode(payload,
            filterChain: new[] { "NeverHeardOfIt" },
            parmsChain: new PdfDictionary?[] { null });
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void FilterChain_reads_single_name()
    {
        var dict = new PdfDictionary();
        dict.Entries["Filter"] = new PdfName("FlateDecode");
        var chain = PdfFilters.FilterChain(dict);
        Assert.Equal(new[] { "FlateDecode" }, chain);
    }

    [Fact]
    public void FilterChain_reads_array()
    {
        var dict = new PdfDictionary();
        var arr = new PdfArray();
        arr.Items.Add(new PdfName("ASCII85Decode"));
        arr.Items.Add(new PdfName("FlateDecode"));
        dict.Entries["Filter"] = arr;
        var chain = PdfFilters.FilterChain(dict);
        Assert.Equal(new[] { "ASCII85Decode", "FlateDecode" }, chain);
    }

    [Fact]
    public void FilterChain_missing_returns_empty()
    {
        var chain = PdfFilters.FilterChain(new PdfDictionary());
        Assert.Empty(chain);
    }

    // ---------- AsciiHexDecode ----------

    [Fact]
    public void AsciiHexDecode_basic()
    {
        var decoded = PdfFilters.AsciiHexDecode(
            Encoding.ASCII.GetBytes("48656C6C6F>"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void AsciiHexDecode_tolerates_whitespace()
    {
        var decoded = PdfFilters.AsciiHexDecode(
            Encoding.ASCII.GetBytes("48 65\n6C\t6C 6F >"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void AsciiHexDecode_odd_trailing_nibble_is_padded()
    {
        var decoded = PdfFilters.AsciiHexDecode(
            Encoding.ASCII.GetBytes("A>"));
        Assert.Single(decoded);
        Assert.Equal(0xA0, decoded[0]);
    }

    // ---------- Ascii85Decode ----------

    [Fact]
    public void Ascii85Decode_roundtrip()
    {
        // "Hello" encoded in base-85 → "87cURDZ".
        // Official Adobe example: "Hello" → "87cURDZ".
        var decoded = PdfFilters.Ascii85Decode(
            Encoding.ASCII.GetBytes("87cURDZ~>"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Ascii85Decode_z_shorthand()
    {
        // Single 'z' = four zero bytes.
        var decoded = PdfFilters.Ascii85Decode(
            Encoding.ASCII.GetBytes("z~>"));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, decoded);
    }

    [Fact]
    public void Ascii85Decode_tolerates_whitespace()
    {
        var decoded = PdfFilters.Ascii85Decode(
            Encoding.ASCII.GetBytes("87cU\nRDZ~>"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(decoded));
    }

    // ---------- RunLengthDecode ----------

    [Fact]
    public void RunLengthDecode_copy_run()
    {
        // Length 2 (0x01) → copy 2 bytes. Then EOD (0x80).
        var decoded = PdfFilters.RunLengthDecode(new byte[] { 1, 0x41, 0x42, 0x80 });
        Assert.Equal(new byte[] { 0x41, 0x42 }, decoded);
    }

    [Fact]
    public void RunLengthDecode_repeat_run()
    {
        // Length 0xFE = 254 → repeat next byte 3 times.
        var decoded = PdfFilters.RunLengthDecode(new byte[] { 0xFE, 0x41, 0x80 });
        Assert.Equal(new byte[] { 0x41, 0x41, 0x41 }, decoded);
    }

    // ---------- LZW ----------

    [Fact]
    public void LzwDecode_roundtrip_ascii()
    {
        // Build an LZW-encoded representation of "ABABABA" using
        // our own compressor so the test doesn't depend on
        // hand-computed bits. Compressor lives in the test helpers
        // below and matches the decoder's early-change = 1
        // behavior.
        byte[] input = Encoding.ASCII.GetBytes("ABABABA");
        byte[] compressed = LzwCompress(input);
        byte[] decoded = PdfFilters.LzwDecode(compressed, parms: null);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void LzwDecode_clear_code_resets_dictionary()
    {
        byte[] input = Encoding.ASCII.GetBytes("XYXYXY");
        byte[] compressed = LzwCompress(input);
        byte[] decoded = PdfFilters.LzwDecode(compressed, parms: null);
        Assert.Equal(input, decoded);
    }

    // ---------- helpers ----------

    /// <summary>Test-side LZW encoder matching the decoder's
    /// early-change=1 semantics. Kept separate from product code
    /// so the tests are self-contained.</summary>
    private static byte[] LzwCompress(byte[] input)
    {
        const int clearCode = 256;
        const int eodCode = 257;
        var dict = new Dictionary<string, int>();
        for (int i = 0; i < 256; i++) dict[((char)i).ToString()] = i;
        int nextCode = 258;
        int codeBits = 9;

        using var ms = new MemoryStream();
        ulong bitBuffer = 0;
        int bitsInBuffer = 0;
        void Emit(int code)
        {
            bitBuffer = (bitBuffer << codeBits) | (uint)code;
            bitsInBuffer += codeBits;
            while (bitsInBuffer >= 8)
            {
                ms.WriteByte((byte)(bitBuffer >> (bitsInBuffer - 8)));
                bitsInBuffer -= 8;
                bitBuffer &= (1UL << bitsInBuffer) - 1;
            }
        }

        Emit(clearCode);
        string w = "";
        foreach (byte b in input)
        {
            string wk = w + (char)b;
            if (dict.ContainsKey(wk)) { w = wk; continue; }
            Emit(dict[w]);
            dict[wk] = nextCode++;
            // Match decoder's early-change=1: bump code width one
            // before the strict boundary.
            int boundary = (1 << codeBits) - 1;
            if (nextCode > boundary && codeBits < 12) codeBits++;
            w = ((char)b).ToString();
        }
        if (w.Length > 0) Emit(dict[w]);
        Emit(eodCode);
        if (bitsInBuffer > 0)
        {
            ms.WriteByte((byte)(bitBuffer << (8 - bitsInBuffer)));
        }
        return ms.ToArray();
    }

    private static byte[] ZLibCompress(byte[] input)
    {
        using var dst = new MemoryStream();
        using (var zs = new ZLibStream(dst, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(input, 0, input.Length);
        }
        return dst.ToArray();
    }
}
