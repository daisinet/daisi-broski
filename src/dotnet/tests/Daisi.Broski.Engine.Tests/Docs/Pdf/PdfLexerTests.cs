using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;
using PTK = Daisi.Broski.Docs.Pdf.PdfTokenKind;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Every token shape from PDF 1.7 spec §7.2. One test per token
/// kind plus the expected whitespace / comment handling around
/// them. These pin the exact boundary behavior that every higher
/// layer (parser, xref, content-stream interpreter) relies on.
/// </summary>
public class PdfLexerTests
{
    private static PdfLexer LexerFor(string s)
        => new(Encoding.ASCII.GetBytes(s));

    // ---------- numbers ----------

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-7", -7L)]
    [InlineData("+42", 42L)]
    [InlineData("0", 0L)]
    public void Integer_tokens(string input, long expected)
    {
        var tok = LexerFor(input).NextToken();
        Assert.NotNull(tok);
        Assert.Equal("Integer", tok!.Kind.ToString());
        Assert.Equal(expected, tok.IntValue);
    }

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("-0.5", -0.5)]
    [InlineData(".2", 0.2)]
    [InlineData("4.", 4.0)]
    public void Real_tokens(string input, double expected)
    {
        var tok = LexerFor(input).NextToken();
        Assert.NotNull(tok);
        Assert.Equal(PTK.Real, tok!.Kind);
        Assert.Equal(expected, tok.NumberValue, precision: 6);
    }

    // ---------- names ----------

    [Theory]
    [InlineData("/Type", "Type")]
    [InlineData("/A#20B", "A B")]   // #20 = space
    [InlineData("/Name1", "Name1")]
    [InlineData("/#41#42", "AB")]   // #41 = 'A', #42 = 'B'
    public void Name_tokens_decode_hash_escapes(string input, string expected)
    {
        var tok = LexerFor(input).NextToken();
        Assert.NotNull(tok);
        Assert.Equal(PTK.Name, tok!.Kind);
        Assert.Equal(expected, tok.Text);
    }

    // ---------- literal strings ----------

    [Fact]
    public void Literal_string_with_balanced_parens()
    {
        var tok = LexerFor("(hello (world))").NextToken()!;
        Assert.Equal(PTK.LiteralString, tok.Kind);
        Assert.Equal("hello (world)", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    [Fact]
    public void Literal_string_escape_sequences()
    {
        var tok = LexerFor(@"(a\nb\t\(c\))").NextToken()!;
        Assert.Equal("a\nb\t(c)", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    [Fact]
    public void Literal_string_octal_escape()
    {
        // \101 = 0x41 = 'A'
        var tok = LexerFor(@"(\101BC)").NextToken()!;
        Assert.Equal("ABC", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    [Fact]
    public void Literal_string_line_continuation()
    {
        // Backslash-newline inside a string continues it with no
        // output.
        var tok = LexerFor("(a\\\nb)").NextToken()!;
        Assert.Equal("ab", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    [Fact]
    public void Literal_string_cr_normalized_to_lf()
    {
        var tok = LexerFor("(a\rb)").NextToken()!;
        Assert.Equal("a\nb", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    // ---------- hex strings ----------

    [Fact]
    public void Hex_string_basic()
    {
        var tok = LexerFor("<48656C6C6F>").NextToken()!;
        Assert.Equal(PTK.HexString, tok.Kind);
        Assert.Equal("Hello", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    [Fact]
    public void Hex_string_odd_trailing_nibble_pads_zero()
    {
        var tok = LexerFor("<A>").NextToken()!;
        Assert.Equal(new byte[] { 0xA0 }, tok.ByteValue);
    }

    [Fact]
    public void Hex_string_tolerates_whitespace()
    {
        var tok = LexerFor("<48 65 6C 6C 6F>").NextToken()!;
        Assert.Equal("Hello", Encoding.ASCII.GetString(tok.ByteValue!));
    }

    // ---------- delimiters ----------

    [Fact]
    public void Double_open_angle_is_open_dict()
    {
        Assert.Equal(PTK.OpenDict, LexerFor("<<").NextToken()!.Kind);
    }

    [Fact]
    public void Double_close_angle_is_close_dict()
    {
        Assert.Equal(PTK.CloseDict, LexerFor(">>").NextToken()!.Kind);
    }

    [Fact]
    public void Square_brackets()
    {
        Assert.Equal(PTK.OpenArray, LexerFor("[").NextToken()!.Kind);
        Assert.Equal(PTK.CloseArray, LexerFor("]").NextToken()!.Kind);
    }

    // ---------- literals + keywords ----------

    [Theory]
    [InlineData("true", "BooleanTrue")]
    [InlineData("false", "BooleanFalse")]
    [InlineData("null", "Null")]
    public void Boolean_and_null_literals(string input, string expectedKind)
    {
        Assert.Equal(expectedKind, LexerFor(input).NextToken()!.Kind.ToString());
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("endobj")]
    [InlineData("R")]
    [InlineData("stream")]
    [InlineData("BT")]
    [InlineData("Tj")]
    public void Keywords(string keyword)
    {
        var tok = LexerFor(keyword).NextToken()!;
        Assert.Equal(PTK.Keyword, tok.Kind);
        Assert.Equal(keyword, tok.Text);
    }

    // ---------- whitespace + comments ----------

    [Fact]
    public void Comments_are_skipped()
    {
        var tok = LexerFor("%this is a comment\n123").NextToken()!;
        Assert.Equal(PTK.Integer, tok.Kind);
        Assert.Equal(123L, tok.IntValue);
    }

    [Fact]
    public void Whitespace_between_tokens_is_skipped()
    {
        var lex = LexerFor("  1 \t 2\r\n 3 ");
        Assert.Equal(1L, lex.NextToken()!.IntValue);
        Assert.Equal(2L, lex.NextToken()!.IntValue);
        Assert.Equal(3L, lex.NextToken()!.IntValue);
        Assert.Null(lex.NextToken());
    }

    // ---------- sequence ----------

    [Fact]
    public void Dictionary_open_name_integer_close_sequence()
    {
        var lex = LexerFor("<</Length 42>>");
        Assert.Equal(PTK.OpenDict, lex.NextToken()!.Kind);
        var name = lex.NextToken()!;
        Assert.Equal(PTK.Name, name.Kind);
        Assert.Equal("Length", name.Text);
        Assert.Equal(42L, lex.NextToken()!.IntValue);
        Assert.Equal(PTK.CloseDict, lex.NextToken()!.Kind);
        Assert.Null(lex.NextToken());
    }

    [Fact]
    public void Peek_does_not_advance_position()
    {
        var lex = LexerFor("7 8");
        var peek = lex.PeekToken()!;
        Assert.Equal(7L, peek.IntValue);
        var next = lex.NextToken()!;
        Assert.Equal(7L, next.IntValue);
    }

    [Fact]
    public void ConsumeStreamPreamble_handles_crlf()
    {
        var bytes = Encoding.ASCII.GetBytes("stream\r\nPAYLOAD");
        var lex = new PdfLexer(bytes);
        // Read the 'stream' keyword.
        var tok = lex.NextToken()!;
        Assert.Equal("stream", tok.Text);
        lex.ConsumeStreamPreamble();
        Assert.Equal('P', (char)bytes[lex.Position]);
    }

    [Fact]
    public void ConsumeStreamPreamble_handles_lone_lf()
    {
        var bytes = Encoding.ASCII.GetBytes("stream\nPAYLOAD");
        var lex = new PdfLexer(bytes);
        var tok = lex.NextToken()!;
        Assert.Equal("stream", tok.Text);
        lex.ConsumeStreamPreamble();
        Assert.Equal('P', (char)bytes[lex.Position]);
    }
}
