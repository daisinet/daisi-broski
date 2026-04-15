using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Pins the ToUnicode CMap parser against the three entry shapes
/// described in PDF 1.7 §9.10.3 — codespaceranges, bfchar, and
/// both bfrange variants (start-string-increment and array).
/// </summary>
public class PdfCMapTests
{
    private static PdfCMap Parse(string source)
        => PdfCMap.Parse(Encoding.ASCII.GetBytes(source));

    [Fact]
    public void Bfchar_single_byte_to_single_char()
    {
        var cmap = Parse("""
            2 beginbfchar
            <41> <0041>
            <42> <00E9>
            endbfchar
            """);
        Assert.Equal("A", cmap.Map(0x41));
        Assert.Equal("é", cmap.Map(0x42));
        Assert.Null(cmap.Map(0x43));
    }

    [Fact]
    public void Bfchar_maps_to_multi_char_unicode_string()
    {
        // A single source code resolving to a two-char string —
        // used for ligatures like 'fi' that CIDs unpack to two
        // Unicode code units.
        var cmap = Parse("""
            1 beginbfchar
            <01> <00660069>
            endbfchar
            """);
        Assert.Equal("fi", cmap.Map(0x01));
    }

    [Fact]
    public void Bfrange_increments_trailing_char()
    {
        // 0x41..0x43 → "A", "B", "C"
        var cmap = Parse("""
            1 beginbfrange
            <41> <43> <0041>
            endbfrange
            """);
        Assert.Equal("A", cmap.Map(0x41));
        Assert.Equal("B", cmap.Map(0x42));
        Assert.Equal("C", cmap.Map(0x43));
    }

    [Fact]
    public void Bfrange_array_form_maps_positionally()
    {
        // 0x01..0x03 → array of three strings.
        var cmap = Parse("""
            1 beginbfrange
            <01> <03> [<0041> <0042> <0043>]
            endbfrange
            """);
        Assert.Equal("A", cmap.Map(0x01));
        Assert.Equal("B", cmap.Map(0x02));
        Assert.Equal("C", cmap.Map(0x03));
    }

    [Fact]
    public void Codespacerange_updates_max_width()
    {
        var cmap = Parse("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            """);
        Assert.Equal(2, cmap.MaxCodeLength);
    }

    [Fact]
    public void Multi_byte_code_maps_correctly()
    {
        var cmap = Parse("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            1 beginbfchar
            <0041> <0041>
            endbfchar
            """);
        Assert.Equal("A", cmap.Map(0x0041));
    }

    [Fact]
    public void Unknown_code_returns_null()
    {
        var cmap = Parse("""
            1 beginbfchar
            <20> <0020>
            endbfchar
            """);
        Assert.Null(cmap.Map(0x30));
    }

    [Fact]
    public void Bogus_content_does_not_throw()
    {
        // Garbage before / after the recognized section is fine.
        var cmap = Parse("""
            /CMapName /Adobe-Identity-H def
            1 beginbfchar
            <41> <0041>
            endbfchar
            CMapName currentdict /CMap defineresource pop
            """);
        Assert.Equal("A", cmap.Map(0x41));
    }
}
