using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Pins the simple-font decode path: StandardEncoding / WinAnsi /
/// MacRoman fall-throughs, Differences-array overlay, and the
/// glyph-list Unicode lookup.
/// </summary>
public class PdfFontTests
{
    // Pass-through resolver — the tests don't involve indirect refs.
    private static PdfObject? Resolve(PdfObject? o) => o;

    [Fact]
    public void WinAnsi_decodes_ascii_text()
    {
        var font = BuildFont("Helvetica", "WinAnsiEncoding");
        Assert.Equal("Hello", font.Decode(Encoding.ASCII.GetBytes("Hello")));
    }

    [Fact]
    public void Standard_encoding_is_default_when_absent()
    {
        var dict = new PdfDictionary();
        dict.Entries["Subtype"] = new PdfName("Type1");
        dict.Entries["BaseFont"] = new PdfName("Times-Roman");
        var font = PdfFont.FromDictionary(dict, Resolve);
        Assert.Equal("Hi!", font.Decode(Encoding.ASCII.GetBytes("Hi!")));
    }

    [Fact]
    public void WinAnsi_decodes_windows1252_extras()
    {
        // 0x80 → Euro sign.
        var font = BuildFont("Helvetica", "WinAnsiEncoding");
        Assert.Equal("€", font.Decode(new byte[] { 0x80 }));
    }

    [Fact]
    public void Unknown_codes_produce_empty_output()
    {
        var font = BuildFont("Helvetica", "WinAnsiEncoding");
        // 0x81 is unassigned in WinAnsi and carries no glyph.
        Assert.Equal("", font.Decode(new byte[] { 0x81 }));
    }

    [Fact]
    public void Differences_array_overrides_encoding_slots()
    {
        var dict = BuildFontDict("Helvetica");
        var enc = new PdfDictionary();
        enc.Entries["BaseEncoding"] = new PdfName("WinAnsiEncoding");
        var diffs = new PdfArray();
        // Replace 0x41 ('A' position) with "bullet".
        diffs.Items.Add(new PdfInt(0x41));
        diffs.Items.Add(new PdfName("bullet"));
        enc.Entries["Differences"] = diffs;
        dict.Entries["Encoding"] = enc;
        var font = PdfFont.FromDictionary(dict, Resolve);
        Assert.Equal("•", font.Decode(new byte[] { 0x41 }));
    }

    [Fact]
    public void Differences_array_consecutive_names_advance_cursor()
    {
        var dict = BuildFontDict("Helvetica");
        var enc = new PdfDictionary();
        enc.Entries["BaseEncoding"] = new PdfName("WinAnsiEncoding");
        var diffs = new PdfArray();
        // Starting at 0x90, fill three slots with A / B / C.
        diffs.Items.Add(new PdfInt(0x90));
        diffs.Items.Add(new PdfName("A"));
        diffs.Items.Add(new PdfName("B"));
        diffs.Items.Add(new PdfName("C"));
        enc.Entries["Differences"] = diffs;
        dict.Entries["Encoding"] = enc;
        var font = PdfFont.FromDictionary(dict, Resolve);
        Assert.Equal("ABC", font.Decode(new byte[] { 0x90, 0x91, 0x92 }));
    }

    [Fact]
    public void Ligature_fi_resolves_to_fb01()
    {
        var dict = BuildFontDict("Helvetica");
        var enc = new PdfDictionary();
        enc.Entries["BaseEncoding"] = new PdfName("WinAnsiEncoding");
        var diffs = new PdfArray();
        diffs.Items.Add(new PdfInt(0x7F));
        diffs.Items.Add(new PdfName("fi"));
        enc.Entries["Differences"] = diffs;
        dict.Entries["Encoding"] = enc;
        var font = PdfFont.FromDictionary(dict, Resolve);
        Assert.Equal("\uFB01", font.Decode(new byte[] { 0x7F }));
    }

    private static PdfDictionary BuildFontDict(string baseFont)
    {
        var d = new PdfDictionary();
        d.Entries["Subtype"] = new PdfName("Type1");
        d.Entries["BaseFont"] = new PdfName(baseFont);
        return d;
    }

    private static PdfFont BuildFont(string baseFont, string encoding)
    {
        var d = BuildFontDict(baseFont);
        d.Entries["Encoding"] = new PdfName(encoding);
        return PdfFont.FromDictionary(d, Resolve);
    }

    // ---------- composite / Type 0 font path ----------

    [Fact]
    public void Type0_font_uses_tounicode_cmap_for_multi_byte_codes()
    {
        // Build a Type 0 font dict with a ToUnicode CMap stream.
        // CMap maps 0x0001 → "A", 0x0002 → "B".
        var cmapSource = System.Text.Encoding.ASCII.GetBytes("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            2 beginbfchar
            <0001> <0041>
            <0002> <0042>
            endbfchar
            """);
        var cmapDict = new PdfDictionary();
        cmapDict.Entries["Length"] = new PdfInt(cmapSource.Length);
        var cmap = new PdfStream(cmapDict, cmapSource);

        var fontDict = new PdfDictionary();
        fontDict.Entries["Subtype"] = new PdfName("Type0");
        fontDict.Entries["BaseFont"] = new PdfName("HeiseiMin-W3-UniJIS-UTF16-H");
        fontDict.Entries["ToUnicode"] = cmap;
        var font = PdfFont.FromDictionary(fontDict, Resolve);

        // Two 2-byte codes → "AB"
        var input = new byte[] { 0x00, 0x01, 0x00, 0x02 };
        Assert.Equal("AB", font.Decode(input));
    }

    [Fact]
    public void Simple_font_with_tounicode_prefers_cmap_over_encoding()
    {
        // Font has both WinAnsiEncoding (which would map 0x41 to "A")
        // and a ToUnicode CMap mapping 0x41 → "Z". CMap wins.
        var cmapSource = System.Text.Encoding.ASCII.GetBytes("""
            1 beginbfchar
            <41> <005A>
            endbfchar
            """);
        var cmapDict = new PdfDictionary();
        cmapDict.Entries["Length"] = new PdfInt(cmapSource.Length);
        var cmap = new PdfStream(cmapDict, cmapSource);
        var fontDict = BuildFontDict("Helvetica");
        fontDict.Entries["Encoding"] = new PdfName("WinAnsiEncoding");
        fontDict.Entries["ToUnicode"] = cmap;
        var font = PdfFont.FromDictionary(fontDict, Resolve);
        Assert.Equal("Z", font.Decode(new byte[] { 0x41 }));
    }

    [Fact]
    public void Type0_font_cmap_range_increments()
    {
        // bfrange maps 0x0001..0x0003 → "A", "B", "C"
        var cmapSource = System.Text.Encoding.ASCII.GetBytes("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            1 beginbfrange
            <0001> <0003> <0041>
            endbfrange
            """);
        var cmapDict = new PdfDictionary();
        cmapDict.Entries["Length"] = new PdfInt(cmapSource.Length);
        var cmap = new PdfStream(cmapDict, cmapSource);

        var fontDict = new PdfDictionary();
        fontDict.Entries["Subtype"] = new PdfName("Type0");
        fontDict.Entries["BaseFont"] = new PdfName("NotoSans");
        fontDict.Entries["ToUnicode"] = cmap;
        var font = PdfFont.FromDictionary(fontDict, Resolve);

        var input = new byte[] { 0x00, 0x01, 0x00, 0x02, 0x00, 0x03 };
        Assert.Equal("ABC", font.Decode(input));
    }
}
