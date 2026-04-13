using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Layout;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Layout;

/// <summary>
/// Phase 6c — block layout. Verifies the box model wires
/// width, padding, border, margin against the resolved
/// cascade; siblings stack vertically; nested boxes inherit
/// the parent's content width; getBoundingClientRect
/// returns sensible numbers via the JS bridge.
/// </summary>
public class LayoutTreeTests
{
    private static LayoutBox? Find(string html, string selector)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        var el = doc.QuerySelector(selector);
        Assert.NotNull(el);
        var root = LayoutTree.Build(doc);
        return LayoutTree.Find(root, el!);
    }

    [Fact]
    public void Block_box_fills_viewport_width_by_default()
    {
        var box = Find("""
            <html><body><div id="x"></div></body></html>
            """, "#x");
        Assert.NotNull(box);
        // viewport width 1280 - body margin (8 each side) = 1264
        Assert.Equal(1264, box!.Width);
    }

    [Fact]
    public void Explicit_pixel_width_is_honored()
    {
        var box = Find("""
            <html><head><style>#x { width: 300px; }</style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        Assert.Equal(300, box!.Width);
    }

    [Fact]
    public void Percentage_width_resolves_against_parent()
    {
        var box = Find("""
            <html><head><style>
              #parent { width: 800px; }
              #child { width: 25%; }
            </style></head>
            <body><div id="parent"><div id="child"></div></div></body></html>
            """, "#child");
        Assert.Equal(200, box!.Width);
    }

    [Fact]
    public void Padding_pushes_outward_in_content_box_sizing()
    {
        var box = Find("""
            <html><head><style>
              #x { width: 200px; padding: 20px; }
            </style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        // content width remains 200, padding adds 20 on each side
        Assert.Equal(200, box!.Width);
        Assert.Equal(20, box.Padding.Left);
        Assert.Equal(20, box.Padding.Right);
    }

    [Fact]
    public void Margin_shorthand_distributes_per_CSS_2_1_rules()
    {
        var box = Find("""
            <html><head><style>
              #x { margin: 1px 2px 3px 4px; }
            </style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        Assert.Equal(1, box!.Margin.Top);
        Assert.Equal(2, box.Margin.Right);
        Assert.Equal(3, box.Margin.Bottom);
        Assert.Equal(4, box.Margin.Left);
    }

    [Fact]
    public void Margin_two_token_shorthand_vertical_horizontal()
    {
        var box = Find("""
            <html><head><style>
              #x { margin: 10px 20px; }
            </style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        Assert.Equal(10, box!.Margin.Top);
        Assert.Equal(20, box.Margin.Right);
        Assert.Equal(10, box.Margin.Bottom);
        Assert.Equal(20, box.Margin.Left);
    }

    [Fact]
    public void Longhand_margin_top_wins_over_shorthand()
    {
        var box = Find("""
            <html><head><style>
              #x { margin: 5px; margin-top: 50px; }
            </style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        Assert.Equal(50, box!.Margin.Top);
        Assert.Equal(5, box.Margin.Right);
    }

    [Fact]
    public void Sibling_blocks_stack_vertically()
    {
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              #a, #b { height: 100px; margin: 0; }
              body { margin: 0; }
            </style></head>
            <body><div id="a"></div><div id="b"></div></body></html>
            """);
        var root = LayoutTree.Build(doc);
        var a = LayoutTree.Find(root, doc.QuerySelector("#a")!);
        var b = LayoutTree.Find(root, doc.QuerySelector("#b")!);
        Assert.NotNull(a);
        Assert.NotNull(b);
        // b sits directly below a (no margin between them)
        Assert.True(b!.Y > a!.Y);
        Assert.Equal(a.Y + a.Height, b.Y);
    }

    [Fact]
    public void Display_none_excludes_box_from_tree()
    {
        var box = Find("""
            <html><head><style>#x { display: none; }</style></head>
            <body><div id="x"></div></body></html>
            """, "#x");
        Assert.Null(box);
    }

    [Fact]
    public void Auto_height_sums_child_outer_heights()
    {
        var box = Find("""
            <html><head><style>
              body { margin: 0; }
              #parent { margin: 0; padding: 0; }
              #parent .row { height: 50px; margin: 0; padding: 0; }
            </style></head>
            <body><div id="parent">
              <div class="row"></div>
              <div class="row"></div>
              <div class="row"></div>
            </div></body></html>
            """, "#parent");
        Assert.NotNull(box);
        Assert.Equal(150, box!.Height);
    }

    [Fact]
    public void GetBoundingClientRect_returns_a_DOMRect_shaped_object()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("""
            <html><head><style>
              body { margin: 0; }
              #x { width: 320px; height: 80px; margin: 10px; }
            </style></head>
            <body><div id="x"></div></body></html>
            """);
        eng.AttachDocument(doc, new Uri("https://example.com/"));

        Assert.Equal(10.0, eng.Evaluate(@"
            document.getElementById('x').getBoundingClientRect().left;
        "));
        Assert.Equal(320.0, eng.Evaluate(@"
            document.getElementById('x').getBoundingClientRect().width;
        "));
        Assert.Equal(80.0, eng.Evaluate(@"
            document.getElementById('x').getBoundingClientRect().height;
        "));
    }

    [Fact]
    public void GetClientRects_returns_a_one_element_list_in_phase_6c()
    {
        var eng = new JsEngine();
        var doc = HtmlTreeBuilder.Parse("<html><body><div id='x'></div></body></html>");
        eng.AttachDocument(doc, new Uri("https://example.com/"));
        Assert.Equal(1.0, eng.Evaluate(@"
            document.getElementById('x').getClientRects().length;
        "));
    }

    [Fact]
    public void UA_stylesheet_gives_body_the_default_8px_margin()
    {
        // The user-agent stylesheet sets `body { margin: 8px }`.
        // Without it, body would butt against the viewport
        // edge — visible as the body box's left/top being 0,0
        // instead of 8,8.
        var box = Find("<html><body><div id='x'></div></body></html>", "body");
        Assert.NotNull(box);
        Assert.Equal(8, box!.Margin.Top);
        Assert.Equal(8, box.Margin.Left);
    }

    [Fact]
    public void Nested_blocks_inherit_containing_block_width_for_percentages()
    {
        // outer has 800px, inner uses 50% = 400px, deepest
        // uses 50% of that = 200px.
        var deepest = Find("""
            <html><head><style>
              body { margin: 0; }
              #outer { width: 800px; }
              #middle { width: 50%; }
              #inner { width: 50%; }
            </style></head>
            <body><div id="outer"><div id="middle">
              <div id="inner"></div>
            </div></div></body></html>
            """, "#inner");
        Assert.Equal(200, deepest!.Width);
    }
}
