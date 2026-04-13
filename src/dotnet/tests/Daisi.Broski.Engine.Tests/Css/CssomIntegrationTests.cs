using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Css;

/// <summary>
/// Phase 6a — wiring of the CSS parser into the Document /
/// JS-side <c>document.styleSheets</c>. Verifies that
/// <c>&lt;style&gt;</c> blocks parsed by the HTML tree builder
/// flow into a queryable CSSOM shape.
/// </summary>
public class CssomIntegrationTests
{
    [Fact]
    public void Document_StyleSheets_collects_inline_style_blocks()
    {
        var doc = HtmlTreeBuilder.Parse("""
            <!DOCTYPE html>
            <html>
              <head>
                <style>body { color: red; }</style>
                <style>p { padding: 1rem; } a { color: blue; }</style>
              </head>
              <body></body>
            </html>
            """);

        Assert.Equal(2, doc.StyleSheets.Count);
        Assert.Equal(1, doc.StyleSheets[0].Rules.Count);
        Assert.Equal(2, doc.StyleSheets[1].Rules.Count);
    }

    [Fact]
    public void Document_with_no_style_blocks_returns_empty_collection()
    {
        var doc = HtmlTreeBuilder.Parse("<html><body><p>hi</p></body></html>");
        Assert.Empty(doc.StyleSheets);
    }

    [Fact]
    public void Invalidate_forces_reparse_after_dom_mutation()
    {
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>a { color: red; }</style></head><body></body></html>
            """);
        Assert.Single(doc.StyleSheets);
        var added = doc.CreateElement("style");
        added.AppendChild(doc.CreateTextNode("b { color: blue; }"));
        doc.Head!.AppendChild(added);

        doc.InvalidateStyleSheets();
        Assert.Equal(2, doc.StyleSheets.Count);
    }

    [Fact]
    public void Document_styleSheets_visible_to_script()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html>
              <head>
                <style>
                  body { color: red; padding: 1rem; }
                  a, button { cursor: pointer; }
                </style>
              </head>
              <body></body>
            </html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));

        Assert.Equal(1.0, eng.Evaluate("document.styleSheets.length;"));
        Assert.Equal(2.0, eng.Evaluate("document.styleSheets[0].cssRules.length;"));
        Assert.Equal("body",
            eng.Evaluate("document.styleSheets[0].cssRules[0].selectorText;"));
        Assert.Equal("a, button",
            eng.Evaluate("document.styleSheets[0].cssRules[1].selectorText;"));
        Assert.Equal("red",
            eng.Evaluate("document.styleSheets[0].cssRules[0].style.color;"));
    }

    [Fact]
    public void Script_can_walk_at_media_block_via_cssRules()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              @media (max-width: 600px) {
                p { font-size: 12px; }
                h1 { font-size: 18px; }
              }
            </style></head><body></body></html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));

        Assert.Equal(1.0, eng.Evaluate("document.styleSheets[0].cssRules.length;"));
        Assert.Equal(4.0, eng.Evaluate("document.styleSheets[0].cssRules[0].type;"));
        Assert.Equal("(max-width: 600px)",
            eng.Evaluate("document.styleSheets[0].cssRules[0].conditionText;"));
        Assert.Equal(2.0,
            eng.Evaluate("document.styleSheets[0].cssRules[0].cssRules.length;"));
    }

    [Fact]
    public void Style_rule_cssText_round_trips_an_important_flag()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse(
            "<html><head><style>p { color: red !important; }</style></head><body></body></html>");
        eng.AttachDocument(doc, new Uri("https://example.com/"));
        var text = (string)eng.Evaluate("document.styleSheets[0].cssRules[0].cssText;")!;
        Assert.Contains("!important", text);
    }
}
