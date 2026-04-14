using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Layout;

/// <summary>
/// Phase 6d — single-line flex layout. Verifies the production
/// patterns that depend on flex: equal-width children with
/// flex:1, justify-content distribution, align-items
/// stretching, gap. Multi-line wrap and per-item align-self
/// are explicitly out of scope.
/// </summary>
public class FlexLayoutTests
{
    private static (LayoutBox container, LayoutBox[] children) FindFlex(
        string html, string containerSelector)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        var el = doc.QuerySelector(containerSelector);
        Assert.NotNull(el);
        var root = LayoutTree.Build(doc);
        var box = LayoutTree.Find(root, el!);
        Assert.NotNull(box);
        return (box!, box.Children.ToArray());
    }

    [Fact]
    public void Equal_flex_children_share_main_axis_evenly()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; }
              #f > * { flex: 1; height: 50px; }
            </style></head>
            <body><div id="f">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#f");
        Assert.Equal(3, ch.Length);
        Assert.Equal(200, ch[0].Width);
        Assert.Equal(200, ch[1].Width);
        Assert.Equal(200, ch[2].Width);
    }

    [Fact]
    public void Children_position_consecutively_along_main_axis()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; }
              #f > * { width: 200px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#f");
        Assert.Equal(0, ch[0].X);
        Assert.Equal(200, ch[1].X);
        Assert.Equal(400, ch[2].X);
    }

    [Fact]
    public void JustifyContent_center_distributes_remaining_space()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; justify-content: center; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div>
            </div></body></html>
            """, "#f");
        // 200px content, 400px remaining → 200px on the left.
        Assert.Equal(200, ch[0].X);
        Assert.Equal(300, ch[1].X);
    }

    [Fact]
    public void JustifyContent_end_pushes_to_the_far_side()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; justify-content: flex-end; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div>
            </div></body></html>
            """, "#f");
        // 400 + 200, items end at 600.
        Assert.Equal(400, ch[0].X);
        Assert.Equal(500, ch[1].X);
    }

    [Fact]
    public void JustifyContent_space_between_distributes_evenly()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; justify-content: space-between; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#f");
        // 300px used, 300px gap → 150px between each pair.
        Assert.Equal(0, ch[0].X);
        Assert.Equal(250, ch[1].X);
        Assert.Equal(500, ch[2].X);
    }

    [Fact]
    public void Gap_inserts_fixed_space_between_items()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; gap: 20px; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#f");
        Assert.Equal(0, ch[0].X);
        Assert.Equal(120, ch[1].X);
        Assert.Equal(240, ch[2].X);
    }

    [Fact]
    public void AlignItems_center_centers_on_cross_axis()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 400px; height: 200px;
                   align-items: center; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div>
            </div></body></html>
            """, "#f");
        // Container is 200px tall, item is 50px → top at 75.
        Assert.Equal(75, ch[0].Y);
    }

    [Fact]
    public void AlignItems_stretch_fills_cross_axis()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 400px; height: 200px; }
              #f > * { width: 100px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div>
            </div></body></html>
            """, "#f");
        // Default align-items is stretch — child fills 200px height.
        Assert.Equal(200, ch[0].Height);
    }

    [Fact]
    public void Flex_grow_distributes_proportionally()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; }
              .a { flex-grow: 1; height: 50px; }
              .b { flex-grow: 2; height: 50px; }
            </style></head>
            <body><div id="f">
              <div class="a"></div><div class="b"></div>
            </div></body></html>
            """, "#f");
        // 600 free space split 1:2 → a=200, b=400.
        Assert.Equal(200, ch[0].Width);
        Assert.Equal(400, ch[1].Width);
    }

    [Fact]
    public void Flex_direction_column_stacks_vertically()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; flex-direction: column;
                   width: 200px; height: 600px; }
              #f > * { height: 100px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#f");
        Assert.Equal(0, ch[0].Y);
        Assert.Equal(100, ch[1].Y);
        Assert.Equal(200, ch[2].Y);
    }

    [Fact]
    public void Row_reverse_flips_main_axis_order()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; flex-direction: row-reverse;
                   width: 600px; }
              #f > * { width: 100px; height: 50px; flex-grow: 0; }
            </style></head>
            <body><div id="f">
              <div id="first"></div>
              <div id="second"></div>
            </div></body></html>
            """, "#f");
        // Reverse order: items[0] (the C# child[0]) is the LAST
        // DOM child laid out first → at x=0... wait. Reverse
        // means the last DOM child sits at the start of the
        // main axis. Our items list is reversed before laying
        // out. So children[0] (the original first DOM child)
        // ends up RIGHT of children[1]. Verify by checking
        // both positions sum to 600 - item width.
        // First DOM child has id="first"; children[0] in box.Children
        // is "first" (because LayChildrenAndResolveHeight added in
        // doc order before flex repositioning).
        // Position for "first" should be at x=500 (right side).
        Assert.Equal(500, ch[0].X);
        Assert.Equal(400, ch[1].X);
    }

    [Fact]
    public void Display_none_child_is_skipped_from_flex_distribution()
    {
        var (c, ch) = FindFlex("""
            <html><head><style>
              body { margin: 0; }
              #f { display: flex; width: 600px; }
              #f > * { flex: 1; height: 50px; }
              .hide { display: none; }
            </style></head>
            <body><div id="f">
              <div></div><div class="hide"></div><div></div>
            </div></body></html>
            """, "#f");
        // Only 2 layout boxes — hidden child has no box.
        Assert.Equal(2, ch.Length);
        Assert.Equal(300, ch[0].Width);
        Assert.Equal(300, ch[1].Width);
    }
}
