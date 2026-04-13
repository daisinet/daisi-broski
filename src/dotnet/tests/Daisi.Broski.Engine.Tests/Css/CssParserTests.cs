using Daisi.Broski.Engine.Css;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Css;

/// <summary>
/// Phase 6a — CSS parser. Verifies the AST shape over the
/// production patterns real sites use: simple style rules,
/// selector lists, !important, comments, at-rules with
/// rule and declaration bodies, malformed-input recovery.
/// </summary>
public class CssParserTests
{
    [Fact]
    public void Empty_input_returns_an_empty_stylesheet()
    {
        Assert.Empty(CssParser.Parse("").Rules);
        Assert.Empty(CssParser.Parse("   ").Rules);
        Assert.Empty(CssParser.Parse("/* just a comment */").Rules);
    }

    [Fact]
    public void Simple_rule_parses_into_one_StyleRule()
    {
        var sheet = CssParser.Parse("body { color: red; }");
        Assert.Single(sheet.Rules);
        var rule = (StyleRule)sheet.Rules[0];
        Assert.Equal("body", rule.SelectorText);
        Assert.Single(rule.Declarations);
        Assert.Equal("color", rule.Declarations[0].Property);
        Assert.Equal("red", rule.Declarations[0].Value);
        Assert.False(rule.Declarations[0].Important);
    }

    [Fact]
    public void Multiple_declarations_separated_by_semicolons()
    {
        var rule = (StyleRule)CssParser.Parse(
            ".x { color: red; background: blue; padding: 10px; }").Rules[0];
        Assert.Equal(3, rule.Declarations.Count);
        Assert.Equal("background", rule.Declarations[1].Property);
        Assert.Equal("blue", rule.Declarations[1].Value);
    }

    [Fact]
    public void Trailing_semicolon_is_optional()
    {
        var rule = (StyleRule)CssParser.Parse("p { color: red }").Rules[0];
        Assert.Equal("red", rule.Declarations[0].Value);
    }

    [Fact]
    public void Important_flag_lifts_off_value()
    {
        var rule = (StyleRule)CssParser.Parse(
            "p { color: red !important; }").Rules[0];
        Assert.True(rule.Declarations[0].Important);
        Assert.Equal("red", rule.Declarations[0].Value);
    }

    [Fact]
    public void Selector_lists_round_trip_as_text()
    {
        var rule = (StyleRule)CssParser.Parse(
            "h1, h2, h3 { color: red; }").Rules[0];
        Assert.Equal("h1, h2, h3", rule.SelectorText);
        Assert.NotNull(rule.Selectors);
    }

    [Fact]
    public void Comments_are_stripped_from_the_AST()
    {
        var sheet = CssParser.Parse(@"
            /* leading comment */
            body { /* inside */ color: /* mid */ red /* tail */; }
            /* trailing */
        ");
        var rule = (StyleRule)Assert.Single(sheet.Rules);
        Assert.Equal("body", rule.SelectorText);
        Assert.Equal("red", rule.Declarations[0].Value);
    }

    [Fact]
    public void Multiple_rules_in_source_order()
    {
        var sheet = CssParser.Parse("a { color: red; } b { color: blue; }");
        Assert.Equal(2, sheet.Rules.Count);
        Assert.Equal("a", ((StyleRule)sheet.Rules[0]).SelectorText);
        Assert.Equal("b", ((StyleRule)sheet.Rules[1]).SelectorText);
    }

    [Fact]
    public void Url_inside_value_does_not_terminate_at_internal_punctuation()
    {
        // url() contains a colon and may contain semicolons in
        // data URIs; the parser must skip them as part of the
        // value, not as the next declaration's separator.
        var rule = (StyleRule)CssParser.Parse(
            "div { background: url('data:image/png;base64,abc=='); color: red; }").Rules[0];
        Assert.Equal(2, rule.Declarations.Count);
        Assert.Contains("data:image/png", rule.Declarations[0].Value);
        Assert.Equal("red", rule.Declarations[1].Value);
    }

    [Fact]
    public void Var_with_fallback_keeps_the_inner_comma()
    {
        var rule = (StyleRule)CssParser.Parse(
            "div { color: var(--c, red); }").Rules[0];
        Assert.Equal("var(--c, red)", rule.Declarations[0].Value);
    }

    [Fact]
    public void At_media_block_holds_nested_style_rules()
    {
        var sheet = CssParser.Parse(@"
            @media (max-width: 600px) {
              body { color: red; }
              p { font-size: 12px; }
            }
        ");
        var atRule = (AtRule)Assert.Single(sheet.Rules);
        Assert.Equal("media", atRule.Name);
        Assert.Equal("(max-width: 600px)", atRule.Prelude);
        Assert.Equal(2, atRule.Rules.Count);
    }

    [Fact]
    public void At_keyframes_block_holds_nested_step_rules()
    {
        var sheet = CssParser.Parse(@"
            @keyframes spin {
              0% { transform: rotate(0deg); }
              100% { transform: rotate(360deg); }
            }
        ");
        var atRule = (AtRule)Assert.Single(sheet.Rules);
        Assert.Equal("keyframes", atRule.Name);
        Assert.Equal(2, atRule.Rules.Count);
    }

    [Fact]
    public void At_font_face_holds_declarations()
    {
        var sheet = CssParser.Parse(@"
            @font-face {
              font-family: 'My Font';
              src: url('/fonts/x.woff2') format('woff2');
            }
        ");
        var atRule = (AtRule)Assert.Single(sheet.Rules);
        Assert.Equal("font-face", atRule.Name);
        Assert.Empty(atRule.Rules);
        Assert.Equal(2, atRule.Declarations.Count);
        Assert.Equal("font-family", atRule.Declarations[0].Property);
    }

    [Fact]
    public void At_import_with_no_body()
    {
        var sheet = CssParser.Parse("@import 'reset.css';");
        var atRule = (AtRule)Assert.Single(sheet.Rules);
        Assert.Equal("import", atRule.Name);
        Assert.Equal("'reset.css'", atRule.Prelude);
        Assert.Empty(atRule.Rules);
        Assert.Empty(atRule.Declarations);
    }

    [Fact]
    public void Malformed_declaration_skipped_subsequent_decls_still_parse()
    {
        var rule = (StyleRule)CssParser.Parse(
            "p { junk_no_colon; color: red; }").Rules[0];
        Assert.Single(rule.Declarations);
        Assert.Equal("color", rule.Declarations[0].Property);
    }

    [Fact]
    public void Property_names_are_lowercased()
    {
        var rule = (StyleRule)CssParser.Parse("p { COLOR: red; }").Rules[0];
        Assert.Equal("color", rule.Declarations[0].Property);
    }

    [Fact]
    public void ParseDeclarationList_handles_inline_style_attributes()
    {
        var decls = CssParser.ParseDeclarationList("color: red; padding: 10px");
        Assert.Equal(2, decls.Count);
        Assert.Equal("color", decls[0].Property);
        Assert.Equal("padding", decls[1].Property);
    }

    [Fact]
    public void AllStyleRules_walks_top_level_and_into_media()
    {
        var sheet = CssParser.Parse(@"
            body { color: red; }
            @media (min-width: 1000px) {
              section { padding: 1rem; }
              p { line-height: 1.5; }
            }
            @keyframes ignored { 0% { x: 0 } 100% { x: 1 } }
            footer { color: blue; }
        ");
        var selectors = sheet.AllStyleRules()
            .Select(r => r.SelectorText)
            .ToArray();
        // Top-level + nested-in-media; @keyframes is skipped
        // because it doesn't carry matchable selectors.
        Assert.Equal(new[] { "body", "section", "p", "footer" }, selectors);
    }

    [Fact]
    public void Unmatched_brace_does_not_throw()
    {
        // Forgiving parser: don't blow up, just stop at EOF.
        var sheet = CssParser.Parse("body { color: red");
        Assert.Single(sheet.Rules);
    }

    [Fact]
    public void Selector_that_fails_to_parse_keeps_the_text_but_drops_Selectors()
    {
        var rule = (StyleRule)CssParser.Parse(
            "::-webkit-scrollbar { width: 5px; }").Rules[0];
        // The selector parser doesn't know about ::-webkit-*
        // pseudo-elements; the CSSOM should still surface the
        // rule for inspection, just with Selectors == null.
        Assert.Equal("::-webkit-scrollbar", rule.SelectorText);
        Assert.Single(rule.Declarations);
    }
}
