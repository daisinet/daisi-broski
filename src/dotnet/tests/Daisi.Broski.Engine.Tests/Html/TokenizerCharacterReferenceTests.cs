using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

public class TokenizerCharacterReferenceTests
{
    // -------- named references in data --------

    [Fact]
    public void Amp_entity_in_data_becomes_ampersand()
    {
        var text = TokenizeCharacterData("a &amp; b");
        Assert.Equal("a & b", text);
    }

    [Fact]
    public void Lt_and_gt_entities_in_data()
    {
        var text = TokenizeCharacterData("&lt;tag&gt;");
        Assert.Equal("<tag>", text);
    }

    [Fact]
    public void Quot_and_apos_entities_in_data()
    {
        var text = TokenizeCharacterData("&quot;hi&quot; &apos;x&apos;");
        Assert.Equal("\"hi\" 'x'", text);
    }

    [Fact]
    public void Nbsp_entity_becomes_non_breaking_space()
    {
        var text = TokenizeCharacterData("a&nbsp;b");
        Assert.Equal("a\u00A0b", text);
    }

    [Fact]
    public void Copyright_entity()
    {
        var text = TokenizeCharacterData("&copy; 2026");
        Assert.Equal("\u00A9 2026", text);
    }

    [Fact]
    public void Typographic_quotes_entities()
    {
        var text = TokenizeCharacterData("&ldquo;hello&rdquo;");
        Assert.Equal("\u201Chello\u201D", text);
    }

    [Fact]
    public void Greek_alpha_entity()
    {
        var text = TokenizeCharacterData("&alpha;");
        Assert.Equal("\u03B1", text);
    }

    // -------- numeric references --------

    [Fact]
    public void Decimal_numeric_reference_maps_to_codepoint()
    {
        // '&#65;' → 'A'
        var text = TokenizeCharacterData("&#65;BC");
        Assert.Equal("ABC", text);
    }

    [Fact]
    public void Hex_lowercase_numeric_reference()
    {
        // '&#x41;' → 'A'
        var text = TokenizeCharacterData("&#x41;BC");
        Assert.Equal("ABC", text);
    }

    [Fact]
    public void Hex_uppercase_X_numeric_reference()
    {
        // '&#X41;' → 'A'
        var text = TokenizeCharacterData("&#X41;BC");
        Assert.Equal("ABC", text);
    }

    [Fact]
    public void Supplementary_plane_numeric_reference_emits_surrogate_pair()
    {
        // U+1F600 grinning face — must emit a surrogate pair in UTF-16.
        var text = TokenizeCharacterData("&#x1F600;");
        Assert.Equal(2, text.Length);
        Assert.True(char.IsHighSurrogate(text[0]));
        Assert.True(char.IsLowSurrogate(text[1]));
        Assert.Equal(0x1F600, char.ConvertToUtf32(text[0], text[1]));
    }

    [Fact]
    public void Numeric_reference_in_windows1252_range_is_remapped()
    {
        // &#x80; → € (0x20AC), per WHATWG legacy fixup.
        var text = TokenizeCharacterData("&#x80;");
        Assert.Equal("\u20AC", text);
    }

    [Fact]
    public void Null_numeric_reference_becomes_replacement_character()
    {
        var text = TokenizeCharacterData("&#0;");
        Assert.Equal("\uFFFD", text);
    }

    [Fact]
    public void Out_of_range_numeric_reference_becomes_replacement_character()
    {
        // 0x110000 is one past the max valid code point.
        var text = TokenizeCharacterData("&#x110000;");
        Assert.Equal("\uFFFD", text);
    }

    [Fact]
    public void Surrogate_numeric_reference_becomes_replacement_character()
    {
        // High surrogate range — not a valid scalar.
        var text = TokenizeCharacterData("&#xD800;");
        Assert.Equal("\uFFFD", text);
    }

    // -------- malformed / recovery --------

    [Fact]
    public void Bare_ampersand_passes_through_as_literal()
    {
        var text = TokenizeCharacterData("a & b");
        Assert.Equal("a & b", text);
    }

    [Fact]
    public void Unknown_named_reference_passes_through_literally()
    {
        var text = TokenizeCharacterData("&nosuchentity;");
        Assert.Equal("&nosuchentity;", text);
    }

    [Fact]
    public void Named_reference_without_semicolon_passes_through_literally()
    {
        // Legacy no-semicolon forms are not supported — we pass through as literal.
        var text = TokenizeCharacterData("&amp rest");
        Assert.Equal("&amp rest", text);
    }

    [Fact]
    public void Numeric_reference_with_no_digits_passes_through_literally()
    {
        var text = TokenizeCharacterData("&#; more");
        Assert.Equal("&#; more", text);
    }

    // -------- references inside attribute values --------

    [Fact]
    public void Amp_in_double_quoted_attribute_value_is_decoded()
    {
        var tokens = TokenizeAll("<a href=\"/q?x=1&amp;y=2\">");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("/q?x=1&y=2", tag.Attributes[0].Value);
    }

    [Fact]
    public void Numeric_reference_in_single_quoted_attribute_value_is_decoded()
    {
        var tokens = TokenizeAll("<a title='&#65;'>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("A", tag.Attributes[0].Value);
    }

    [Fact]
    public void Reference_in_unquoted_attribute_value_is_decoded()
    {
        var tokens = TokenizeAll("<a data-x=a&amp;b>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("a&b", tag.Attributes[0].Value);
    }

    // -------- helpers --------

    private static string TokenizeCharacterData(string input)
    {
        var tokenizer = new Tokenizer(input);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var t = tokenizer.Next();
            if (t is CharacterToken c) sb.Append(c.Data);
            else if (t is EndOfFileToken) return sb.ToString();
        }
    }

    private static List<HtmlToken> TokenizeAll(string input)
    {
        var tokenizer = new Tokenizer(input);
        var result = new List<HtmlToken>();
        while (true)
        {
            var t = tokenizer.Next();
            result.Add(t);
            if (t is EndOfFileToken) return result;
        }
    }
}
