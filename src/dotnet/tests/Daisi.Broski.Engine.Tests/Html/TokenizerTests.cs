using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

public class TokenizerTests
{
    // -------- plain text --------

    [Fact]
    public void Empty_input_emits_only_EOF()
    {
        var tokens = Tokenize("");
        Assert.Single(tokens);
        Assert.IsType<EndOfFileToken>(tokens[0]);
    }

    [Fact]
    public void Plain_text_is_one_character_token()
    {
        var tokens = Tokenize("hello world");
        Assert.IsType<CharacterToken>(tokens[0]);
        Assert.Equal("hello world", ((CharacterToken)tokens[0]).Data);
        Assert.IsType<EndOfFileToken>(tokens[1]);
    }

    // -------- start tags --------

    [Fact]
    public void Bare_start_tag()
    {
        var tokens = Tokenize("<p>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("p", tag.Name);
        Assert.Empty(tag.Attributes);
        Assert.False(tag.SelfClosing);
    }

    [Fact]
    public void Uppercase_tag_name_is_lowercased()
    {
        var tokens = Tokenize("<DIV>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("div", tag.Name);
    }

    [Fact]
    public void Start_tag_with_double_quoted_attribute()
    {
        var tokens = Tokenize("<a href=\"https://x\">");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("a", tag.Name);
        Assert.Single(tag.Attributes);
        Assert.Equal("href", tag.Attributes[0].Name);
        Assert.Equal("https://x", tag.Attributes[0].Value);
    }

    [Fact]
    public void Start_tag_with_single_quoted_attribute()
    {
        var tokens = Tokenize("<a href='x'>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("x", tag.Attributes[0].Value);
    }

    [Fact]
    public void Start_tag_with_unquoted_attribute()
    {
        var tokens = Tokenize("<input type=text>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("input", tag.Name);
        Assert.Single(tag.Attributes);
        Assert.Equal("type", tag.Attributes[0].Name);
        Assert.Equal("text", tag.Attributes[0].Value);
    }

    [Fact]
    public void Start_tag_with_multiple_attributes()
    {
        var tokens = Tokenize("<a href=\"/x\" class='main' data-id=42>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal(3, tag.Attributes.Count);
        Assert.Equal("href", tag.Attributes[0].Name);
        Assert.Equal("/x", tag.Attributes[0].Value);
        Assert.Equal("class", tag.Attributes[1].Name);
        Assert.Equal("main", tag.Attributes[1].Value);
        Assert.Equal("data-id", tag.Attributes[2].Name);
        Assert.Equal("42", tag.Attributes[2].Value);
    }

    [Fact]
    public void Attribute_name_without_value()
    {
        var tokens = Tokenize("<input disabled>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Single(tag.Attributes);
        Assert.Equal("disabled", tag.Attributes[0].Name);
        Assert.Equal("", tag.Attributes[0].Value);
    }

    [Fact]
    public void Duplicate_attribute_names_keep_first()
    {
        var tokens = Tokenize("<a id=\"first\" id=\"second\">");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Single(tag.Attributes);
        Assert.Equal("first", tag.Attributes[0].Value);
    }

    [Fact]
    public void Uppercase_attribute_name_is_lowercased()
    {
        var tokens = Tokenize("<a HREF=\"x\">");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("href", tag.Attributes[0].Name);
    }

    // -------- self-closing --------

    [Fact]
    public void Self_closing_tag_flag_is_set()
    {
        var tokens = Tokenize("<br/>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("br", tag.Name);
        Assert.True(tag.SelfClosing);
    }

    [Fact]
    public void Self_closing_tag_with_attribute()
    {
        var tokens = Tokenize("<img src=\"x.png\" alt=\"hi\"/>");
        var tag = Assert.IsType<StartTagToken>(tokens[0]);
        Assert.Equal("img", tag.Name);
        Assert.True(tag.SelfClosing);
        Assert.Equal(2, tag.Attributes.Count);
    }

    // -------- end tags --------

    [Fact]
    public void End_tag_emits_end_tag_token()
    {
        var tokens = Tokenize("</div>");
        var tag = Assert.IsType<EndTagToken>(tokens[0]);
        Assert.Equal("div", tag.Name);
    }

    // -------- comments --------

    [Fact]
    public void Simple_comment()
    {
        var tokens = Tokenize("<!-- hi -->");
        var c = Assert.IsType<CommentToken>(tokens[0]);
        Assert.Equal(" hi ", c.Data);
    }

    [Fact]
    public void Empty_comment()
    {
        var tokens = Tokenize("<!---->");
        var c = Assert.IsType<CommentToken>(tokens[0]);
        Assert.Equal("", c.Data);
    }

    // -------- DOCTYPE --------

    [Fact]
    public void Html5_doctype_is_recognized()
    {
        var tokens = Tokenize("<!DOCTYPE html>");
        var d = Assert.IsType<DoctypeToken>(tokens[0]);
        Assert.Equal("html", d.Name);
    }

    [Fact]
    public void Doctype_with_extra_content_parses_name()
    {
        var tokens = Tokenize("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">");
        var d = Assert.IsType<DoctypeToken>(tokens[0]);
        Assert.Equal("html", d.Name);
    }

    // -------- compound documents --------

    [Fact]
    public void Doctype_and_nested_tags_sequence()
    {
        var tokens = Tokenize("<!DOCTYPE html><html><head><title>x</title></head></html>");

        Assert.IsType<DoctypeToken>(tokens[0]);

        var html = Assert.IsType<StartTagToken>(tokens[1]);
        Assert.Equal("html", html.Name);

        var head = Assert.IsType<StartTagToken>(tokens[2]);
        Assert.Equal("head", head.Name);

        var title = Assert.IsType<StartTagToken>(tokens[3]);
        Assert.Equal("title", title.Name);

        var text = Assert.IsType<CharacterToken>(tokens[4]);
        Assert.Equal("x", text.Data);

        Assert.Equal("title", Assert.IsType<EndTagToken>(tokens[5]).Name);
        Assert.Equal("head", Assert.IsType<EndTagToken>(tokens[6]).Name);
        Assert.Equal("html", Assert.IsType<EndTagToken>(tokens[7]).Name);
        Assert.IsType<EndOfFileToken>(tokens[8]);
    }

    [Fact]
    public void Character_data_surrounding_tags_is_batched()
    {
        var tokens = Tokenize("hello <b>bold</b> world");

        Assert.Equal("hello ", Assert.IsType<CharacterToken>(tokens[0]).Data);
        Assert.Equal("b", Assert.IsType<StartTagToken>(tokens[1]).Name);
        Assert.Equal("bold", Assert.IsType<CharacterToken>(tokens[2]).Data);
        Assert.Equal("b", Assert.IsType<EndTagToken>(tokens[3]).Name);
        Assert.Equal(" world", Assert.IsType<CharacterToken>(tokens[4]).Data);
        Assert.IsType<EndOfFileToken>(tokens[5]);
    }

    [Fact]
    public void Stray_less_than_is_emitted_as_character_data()
    {
        var tokens = Tokenize("a < b");
        // The '<' that isn't followed by a letter falls back to data.
        // It may end up split across multiple character tokens; just
        // assert the concatenation round-trips.
        var chars = new System.Text.StringBuilder();
        foreach (var t in tokens)
        {
            if (t is CharacterToken c) chars.Append(c.Data);
        }
        Assert.Equal("a < b", chars.ToString());
    }

    // -------- helper --------

    private static List<HtmlToken> Tokenize(string input)
    {
        var tokenizer = new Tokenizer(input);
        var result = new List<HtmlToken>();
        while (true)
        {
            var t = tokenizer.Next();
            result.Add(t);
            if (t is EndOfFileToken) return result;
            if (result.Count > 10_000)
                throw new InvalidOperationException("Tokenizer did not terminate");
        }
    }
}
