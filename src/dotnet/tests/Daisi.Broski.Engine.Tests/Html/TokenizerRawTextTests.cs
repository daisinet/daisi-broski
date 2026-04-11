using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

public class TokenizerRawTextTests
{
    // -------- <script> --------

    [Fact]
    public void Script_body_with_tag_like_text_is_one_character_token()
    {
        var tokens = TokenizeAll("<script>var x = \"<b>\";</script>");
        AssertStart(tokens[0], "script");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("var x = \"<b>\";", body.Data);
        AssertEnd(tokens[2], "script");
    }

    [Fact]
    public void Script_body_with_entity_is_left_literal()
    {
        // RAWTEXT / script data does NOT decode entities.
        var tokens = TokenizeAll("<script>a &amp; b</script>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("a &amp; b", body.Data);
    }

    [Fact]
    public void Script_body_with_comparison_operators_is_not_tokenized_as_markup()
    {
        var tokens = TokenizeAll("<script>if (a < b && c > d) {}</script>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("if (a < b && c > d) {}", body.Data);
    }

    [Fact]
    public void Script_body_containing_closing_tag_like_substring_with_trailing_chars()
    {
        // '</scripts' is NOT </script> — the trailing 's' means the name
        // continues and the matcher should not fire.
        var tokens = TokenizeAll("<script>x </scripts y</script>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("x </scripts y", body.Data);
    }

    [Fact]
    public void Script_closing_tag_is_case_insensitive()
    {
        var tokens = TokenizeAll("<script>x</SCRIPT>");
        AssertStart(tokens[0], "script");
        Assert.Equal("x", Assert.IsType<CharacterToken>(tokens[1]).Data);
        AssertEnd(tokens[2], "script");
    }

    [Fact]
    public void Script_closing_tag_with_whitespace_before_gt_is_recognized()
    {
        var tokens = TokenizeAll("<script>x</script >after");
        AssertEnd(tokens[2], "script");
        Assert.Equal("after", Assert.IsType<CharacterToken>(tokens[3]).Data);
    }

    [Fact]
    public void After_script_tokenizer_returns_to_data_state()
    {
        var tokens = TokenizeAll("<script>x</script><p>after</p>");
        AssertEnd(tokens[2], "script");
        AssertStart(tokens[3], "p");
        Assert.Equal("after", Assert.IsType<CharacterToken>(tokens[4]).Data);
        AssertEnd(tokens[5], "p");
    }

    // -------- <style> --------

    [Fact]
    public void Style_body_preserves_css_syntax()
    {
        var tokens = TokenizeAll("<style>a > b { color: red; }</style>");
        AssertStart(tokens[0], "style");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("a > b { color: red; }", body.Data);
        AssertEnd(tokens[2], "style");
    }

    [Fact]
    public void Style_body_does_not_decode_entities()
    {
        var tokens = TokenizeAll("<style>.x::after { content: \"&amp;\"; }</style>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal(".x::after { content: \"&amp;\"; }", body.Data);
    }

    // -------- <title> (RCDATA) --------

    [Fact]
    public void Title_body_is_character_data()
    {
        var tokens = TokenizeAll("<title>hello</title>");
        AssertStart(tokens[0], "title");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("hello", body.Data);
        AssertEnd(tokens[2], "title");
    }

    [Fact]
    public void Title_body_decodes_character_references()
    {
        // RCDATA *does* decode entities.
        var tokens = TokenizeAll("<title>a &amp; b</title>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("a & b", body.Data);
    }

    [Fact]
    public void Title_body_with_tag_like_text_is_not_parsed_as_markup()
    {
        var tokens = TokenizeAll("<title>5 < 10</title>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("5 < 10", body.Data);
    }

    // -------- <textarea> (RCDATA) --------

    [Fact]
    public void Textarea_body_decodes_entities_but_keeps_tag_like_chars_literal()
    {
        var tokens = TokenizeAll("<textarea>a &lt; b</textarea>");
        var body = Assert.IsType<CharacterToken>(tokens[1]);
        Assert.Equal("a < b", body.Data);
    }

    // -------- helpers --------

    private static void AssertStart(HtmlToken t, string name) =>
        Assert.Equal(name, Assert.IsType<StartTagToken>(t).Name);

    private static void AssertEnd(HtmlToken t, string name) =>
        Assert.Equal(name, Assert.IsType<EndTagToken>(t).Name);

    private static List<HtmlToken> TokenizeAll(string input)
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
