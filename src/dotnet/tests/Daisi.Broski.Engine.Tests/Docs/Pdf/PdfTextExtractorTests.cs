using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Test the text extractor against hand-crafted content streams.
/// Covers Tj, TJ, ', ", Td/TD, T*, Tm; font selection via Tf;
/// the TJ kerning-space heuristic; and graphics operators that
/// should pass silently.
/// </summary>
public class PdfTextExtractorTests
{
    private static PdfFont WinAnsi(string name = "F1")
    {
        var d = new PdfDictionary();
        d.Entries["Subtype"] = new PdfName("Type1");
        d.Entries["BaseFont"] = new PdfName("Helvetica");
        d.Entries["Encoding"] = new PdfName("WinAnsiEncoding");
        return PdfFont.FromDictionary(d, o => o);
    }

    private static string Run(string contentStream, PdfFont font)
    {
        var bytes = Encoding.ASCII.GetBytes(contentStream);
        return PdfTextExtractor.Extract(bytes, _ => font);
    }

    [Fact]
    public void Tj_emits_string()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (Hello) Tj ET", font);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void TJ_concatenates_strings_and_skips_kerning_integers()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf [(Hel) 5 (lo)] TJ ET", font);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void TJ_large_negative_kerning_inserts_space()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf [(Hello) -300 (world)] TJ ET", font);
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Apostrophe_moves_to_next_line_before_showing()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (Line1) Tj (Line2) ' ET", font);
        Assert.Contains("Line1", result);
        Assert.Contains("Line2", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void Double_quote_ignores_spacing_operands()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf 0 0 (Hello) \" ET", font);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void T_star_inserts_newline_between_shows()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (First) Tj T* (Second) Tj ET", font);
        Assert.Equal("First\nSecond", result);
    }

    [Fact]
    public void Td_with_zero_y_does_not_insert_newline()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (A) Tj 10 0 Td (B) Tj ET", font);
        Assert.Equal("AB", result);
    }

    [Fact]
    public void Td_with_nonzero_y_inserts_newline()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (A) Tj 0 -14 Td (B) Tj ET", font);
        Assert.Equal("A\nB", result);
    }

    [Fact]
    public void Tm_inserts_newline()
    {
        var font = WinAnsi();
        var result = Run("BT /F1 12 Tf (A) Tj 1 0 0 1 100 200 Tm (B) Tj ET", font);
        Assert.Equal("A\nB", result);
    }

    [Fact]
    public void Unknown_font_key_produces_no_text_but_does_not_throw()
    {
        var bytes = Encoding.ASCII.GetBytes("BT /NotFound 12 Tf (Hello) Tj ET");
        var result = PdfTextExtractor.Extract(bytes, _ => null);
        Assert.Equal("", result);
    }

    [Fact]
    public void Graphics_operators_do_not_affect_output()
    {
        var font = WinAnsi();
        var result = Run(
            "q 1 0 0 1 50 50 cm BT /F1 12 Tf (Hi) Tj ET Q",
            font);
        Assert.Equal("Hi", result);
    }

    [Fact]
    public void Hex_strings_work_like_literal_strings()
    {
        var font = WinAnsi();
        // <48656C6C6F> = "Hello"
        var result = Run("BT /F1 12 Tf <48656C6C6F> Tj ET", font);
        Assert.Equal("Hello", result);
    }
}
