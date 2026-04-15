using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Exercises the parser over hand-crafted PDF object-syntax
/// snippets. Covers the compound shapes — arrays, dicts, streams,
/// indirect refs — that higher layers (the xref reader, the
/// content-stream interpreter) lean on.
/// </summary>
public class PdfParserTests
{
    private static PdfParser ParserFor(string s)
        => new(new PdfLexer(Encoding.ASCII.GetBytes(s)));

    [Fact]
    public void Null_parses_to_null_instance()
    {
        var obj = ParserFor("null").ReadObject();
        Assert.Same(PdfNull.Instance, obj);
    }

    [Fact]
    public void True_and_false_parse_to_bool_singletons()
    {
        Assert.Same(PdfBool.True, ParserFor("true").ReadObject());
        Assert.Same(PdfBool.False, ParserFor("false").ReadObject());
    }

    [Fact]
    public void Integer_object()
    {
        var obj = Assert.IsType<PdfInt>(ParserFor("42").ReadObject());
        Assert.Equal(42L, obj.Value);
    }

    [Fact]
    public void Real_object()
    {
        var obj = Assert.IsType<PdfReal>(ParserFor("3.14").ReadObject());
        Assert.Equal(3.14, obj.Value, precision: 6);
    }

    [Fact]
    public void Name_object()
    {
        var obj = Assert.IsType<PdfName>(ParserFor("/Type").ReadObject());
        Assert.Equal("Type", obj.Value);
    }

    [Fact]
    public void Literal_string_object()
    {
        var obj = Assert.IsType<PdfString>(ParserFor("(hello)").ReadObject());
        Assert.False(obj.Hex);
        Assert.Equal("hello", Encoding.ASCII.GetString(obj.Bytes));
    }

    [Fact]
    public void Hex_string_object()
    {
        var obj = Assert.IsType<PdfString>(ParserFor("<48656C6C6F>").ReadObject());
        Assert.True(obj.Hex);
        Assert.Equal("Hello", Encoding.ASCII.GetString(obj.Bytes));
    }

    [Fact]
    public void Array_of_mixed_objects()
    {
        var obj = Assert.IsType<PdfArray>(ParserFor("[1 2.5 /Foo null]").ReadObject());
        Assert.Equal(4, obj.Count);
        Assert.IsType<PdfInt>(obj[0]);
        Assert.IsType<PdfReal>(obj[1]);
        Assert.IsType<PdfName>(obj[2]);
        Assert.Same(PdfNull.Instance, obj[3]);
    }

    [Fact]
    public void Dictionary_with_name_keys()
    {
        var obj = Assert.IsType<PdfDictionary>(
            ParserFor("<</Type /Catalog /Pages 2 0 R>>").ReadObject());
        Assert.Equal(2, obj.Entries.Count);
        var type = Assert.IsType<PdfName>(obj.Get("Type"));
        Assert.Equal("Catalog", type.Value);
        var pages = Assert.IsType<PdfRef>(obj.Get("Pages"));
        Assert.Equal(2, pages.ObjectNumber);
        Assert.Equal(0, pages.Generation);
    }

    [Fact]
    public void Indirect_reference_parses_as_PdfRef()
    {
        var obj = Assert.IsType<PdfRef>(ParserFor("12 0 R").ReadObject());
        Assert.Equal(12, obj.ObjectNumber);
        Assert.Equal(0, obj.Generation);
    }

    [Fact]
    public void Integer_pair_not_followed_by_R_parses_separately()
    {
        var parser = ParserFor("12 0");
        var first = Assert.IsType<PdfInt>(parser.ReadObject());
        Assert.Equal(12L, first.Value);
        var second = Assert.IsType<PdfInt>(parser.ReadObject());
        Assert.Equal(0L, second.Value);
    }

    [Fact]
    public void Nested_dictionary()
    {
        var obj = Assert.IsType<PdfDictionary>(
            ParserFor("<</Inner <</K /V>>>>").ReadObject());
        var inner = Assert.IsType<PdfDictionary>(obj.Get("Inner"));
        var v = Assert.IsType<PdfName>(inner.Get("K"));
        Assert.Equal("V", v.Value);
    }

    [Fact]
    public void Stream_object_body_captured_verbatim()
    {
        // A valid PDF stream: dict with /Length, then "stream\n",
        // then exactly that many bytes, then "\nendstream".
        var payload = "q BT /F1 12 Tf (Hi) Tj ET Q";
        var source =
            "<</Length " + payload.Length + ">>\n" +
            "stream\n" + payload + "\nendstream\n";
        var parser = ParserFor(source);
        // Consume the stream dictionary + body in one call.
        var stream = Assert.IsType<PdfStream>(parser.ReadIndirectObjectBody());
        Assert.Equal(payload.Length, stream.RawBytes.Length);
        Assert.Equal(payload, Encoding.ASCII.GetString(stream.RawBytes));
        // Stream dict preserved.
        var len = Assert.IsType<PdfInt>(stream.Dictionary.Get("Length"));
        Assert.Equal(payload.Length, len.Value);
    }

    [Fact]
    public void ReadIndirectObjectBody_consumes_trailing_endobj()
    {
        var parser = ParserFor("42 endobj");
        var obj = Assert.IsType<PdfInt>(parser.ReadIndirectObjectBody());
        Assert.Equal(42L, obj.Value);
        // Lexer should now be at end of input.
        Assert.Null(parser.Lexer.NextToken());
    }
}
