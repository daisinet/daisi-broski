using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Css;

/// <summary>
/// Phase 6b — cascade + computed style. Verifies specificity
/// sort, !important precedence, source order, inline override,
/// inheritance for inheritable properties, and @media query
/// gating against the viewport.
/// </summary>
public class StyleResolverTests
{
    private static ComputedStyle ResolveTarget(string html, string targetSelector,
        Viewport? viewport = null)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        var el = doc.QuerySelector(targetSelector);
        Assert.NotNull(el);
        return StyleResolver.Resolve(el!, viewport);
    }

    // ---------------------------------------------------------
    // Specificity arithmetic
    // ---------------------------------------------------------

    [Fact]
    public void Specificity_id_beats_classes()
    {
        var s1 = new Specificity(1, 0, 0);
        var s2 = new Specificity(0, 5, 5);
        Assert.True(s1.CompareTo(s2) > 0);
    }

    [Fact]
    public void Specificity_class_beats_types()
    {
        Assert.True(new Specificity(0, 1, 0).CompareTo(new Specificity(0, 0, 99)) > 0);
    }

    // ---------------------------------------------------------
    // Cascade rules
    // ---------------------------------------------------------

    [Fact]
    public void More_specific_rule_wins()
    {
        var style = ResolveTarget("""
            <html><head><style>
              p { color: red; }
              p.x { color: blue; }
            </style></head><body><p class="x">hi</p></body></html>
            """, "p");
        Assert.Equal("blue", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Equal_specificity_last_in_source_order_wins()
    {
        var style = ResolveTarget("""
            <html><head><style>
              .a { color: red; }
              .a { color: blue; }
            </style></head><body><div class="a"></div></body></html>
            """, ".a");
        Assert.Equal("blue", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Important_beats_more_specific_non_important()
    {
        var style = ResolveTarget("""
            <html><head><style>
              p#x { color: red; }
              p { color: green !important; }
            </style></head><body><p id="x">hi</p></body></html>
            """, "p");
        Assert.Equal("green", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Id_selector_beats_many_classes()
    {
        var style = ResolveTarget("""
            <html><head><style>
              #a { color: red; }
              .x.y.z { color: blue; }
            </style></head><body><div id="a" class="x y z"></div></body></html>
            """, "#a");
        Assert.Equal("red", style.GetPropertyValue("color"));
    }

    // ---------------------------------------------------------
    // Inline style
    // ---------------------------------------------------------

    [Fact]
    public void Inline_style_overrides_author_rules()
    {
        var style = ResolveTarget("""
            <html><head><style>
              #a { color: red; }
            </style></head><body><div id="a" style="color: blue"></div></body></html>
            """, "#a");
        Assert.Equal("blue", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Inline_style_layers_in_per_property()
    {
        // Inline only sets color; the author background-color
        // stays.
        var style = ResolveTarget("""
            <html><head><style>
              #a { color: red; background-color: yellow; }
            </style></head>
            <body><div id="a" style="color: blue"></div></body></html>
            """, "#a");
        Assert.Equal("blue", style.GetPropertyValue("color"));
        Assert.Equal("yellow", style.GetPropertyValue("background-color"));
    }

    // ---------------------------------------------------------
    // Inheritance
    // ---------------------------------------------------------

    [Fact]
    public void Color_inherits_from_parent()
    {
        var style = ResolveTarget("""
            <html><head><style>
              body { color: red; }
            </style></head><body><p>hi</p></body></html>
            """, "p");
        Assert.Equal("red", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Padding_does_not_inherit()
    {
        var style = ResolveTarget("""
            <html><head><style>
              body { padding: 10px; }
            </style></head><body><p>hi</p></body></html>
            """, "p");
        Assert.Equal("", style.GetPropertyValue("padding"));
    }

    [Fact]
    public void Element_own_value_beats_inherited()
    {
        var style = ResolveTarget("""
            <html><head><style>
              body { color: red; }
              p { color: green; }
            </style></head><body><p>hi</p></body></html>
            """, "p");
        Assert.Equal("green", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Inheritance_walks_up_through_multiple_ancestors()
    {
        var style = ResolveTarget("""
            <html><head><style>
              html { color: red; }
            </style></head><body><div><p>hi</p></div></body></html>
            """, "p");
        Assert.Equal("red", style.GetPropertyValue("color"));
    }

    // ---------------------------------------------------------
    // @media queries
    // ---------------------------------------------------------

    [Fact]
    public void Max_width_media_query_applies_when_viewport_smaller()
    {
        var style = ResolveTarget("""
            <html><head><style>
              p { color: black; }
              @media (max-width: 600px) { p { color: red; } }
            </style></head><body><p>hi</p></body></html>
            """, "p", new Viewport { Width = 400, Height = 600 });
        Assert.Equal("red", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Max_width_media_query_skipped_when_viewport_larger()
    {
        var style = ResolveTarget("""
            <html><head><style>
              p { color: black; }
              @media (max-width: 600px) { p { color: red; } }
            </style></head><body><p>hi</p></body></html>
            """, "p", new Viewport { Width = 1200, Height = 800 });
        Assert.Equal("black", style.GetPropertyValue("color"));
    }

    [Fact]
    public void Media_query_combined_with_and()
    {
        var style = ResolveTarget("""
            <html><head><style>
              p { color: black; }
              @media (min-width: 500px) and (max-width: 1000px) { p { color: red; } }
            </style></head><body><p>hi</p></body></html>
            """, "p", new Viewport { Width = 700, Height = 600 });
        Assert.Equal("red", style.GetPropertyValue("color"));
    }

    // ---------------------------------------------------------
    // JS-side getComputedStyle wiring
    // ---------------------------------------------------------

    [Fact]
    public void GetComputedStyle_reads_cascaded_value()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              .x { color: red; padding: 10px; }
            </style></head><body><div class="x">hi</div></body></html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));

        Assert.Equal("red", eng.Evaluate(@"
            getComputedStyle(document.querySelector('.x')).color;
        "));
        Assert.Equal("red", eng.Evaluate(@"
            getComputedStyle(document.querySelector('.x')).getPropertyValue('color');
        "));
    }

    [Fact]
    public void GetComputedStyle_supports_camelCase_lookup()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              p { background-color: yellow; line-height: 1.5; }
            </style></head><body><p>hi</p></body></html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));

        Assert.Equal("yellow",
            eng.Evaluate("getComputedStyle(document.querySelector('p')).backgroundColor;"));
        Assert.Equal("1.5",
            eng.Evaluate("getComputedStyle(document.querySelector('p')).lineHeight;"));
    }

    [Fact]
    public void GetComputedStyle_returns_empty_string_for_unset_properties()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse(
            "<html><body><p>hi</p></body></html>");
        eng.AttachDocument(doc, new Uri("https://example.com/"));
        Assert.Equal("",
            eng.Evaluate("getComputedStyle(document.querySelector('p')).color;"));
    }

    [Fact]
    public void GetComputedStyle_inherits_color_through_the_chain()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              body { color: blue; font-family: serif; }
            </style></head><body><div><p><span>hi</span></p></div></body></html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));
        Assert.Equal("blue",
            eng.Evaluate("getComputedStyle(document.querySelector('span')).color;"));
        Assert.Equal("serif",
            eng.Evaluate("getComputedStyle(document.querySelector('span')).fontFamily;"));
    }
}
