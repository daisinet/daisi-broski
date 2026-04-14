using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Layout;

/// <summary>
/// Phase 6e — minimum-viable CSS Grid. Verifies the
/// auto-placement + track-resolution patterns real pages
/// reach for: fixed-pixel columns, fr units, repeat(),
/// gap, row + column overflow into implicit tracks.
/// </summary>
public class GridLayoutTests
{
    private static (LayoutBox container, LayoutBox[] children) FindGrid(
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
    public void Two_fr_columns_split_evenly()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid; grid-template-columns: 1fr 1fr;
                   width: 600px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(2, ch.Length);
        Assert.Equal(300, ch[0].Width);
        Assert.Equal(300, ch[1].Width);
        Assert.Equal(0, ch[0].X);
        Assert.Equal(300, ch[1].X);
    }

    [Fact]
    public void Mixed_pixel_and_fr_tracks_resolve()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid; grid-template-columns: 100px 1fr 200px;
                   width: 600px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(100, ch[0].Width);
        Assert.Equal(300, ch[1].Width);
        Assert.Equal(200, ch[2].Width);
    }

    [Fact]
    public void Repeat_expands_into_individual_tracks()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid; grid-template-columns: repeat(4, 1fr);
                   width: 800px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div><div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(4, ch.Length);
        Assert.All(ch, c => Assert.Equal(200, c.Width));
    }

    [Fact]
    public void Children_overflow_into_implicit_rows()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid;
                   grid-template-columns: 100px 100px;
                   grid-template-rows: 50px;
                   width: 200px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
              <div></div><div></div>
              <div></div>
            </div></body></html>
            """, "#g");
        // 5 children in a 2-column grid → 3 rows. Row height
        // implicitly extends from the last declared row (50px).
        Assert.Equal(5, ch.Length);
        Assert.Equal(0, ch[0].Y);
        Assert.Equal(0, ch[1].Y);
        Assert.Equal(50, ch[2].Y);
        Assert.Equal(50, ch[3].Y);
        Assert.Equal(100, ch[4].Y);
    }

    [Fact]
    public void Row_template_drives_row_heights()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid;
                   grid-template-columns: 1fr;
                   grid-template-rows: 50px 100px;
                   width: 200px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        // Children stretch to row heights by default in grid.
        Assert.Equal(50, ch[0].Height);
        Assert.Equal(100, ch[1].Height);
        Assert.Equal(0, ch[0].Y);
        Assert.Equal(50, ch[1].Y);
    }

    [Fact]
    public void Gap_inserts_space_between_tracks()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid;
                   grid-template-columns: 100px 100px;
                   gap: 20px;
                   width: 220px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(0, ch[0].X);
        Assert.Equal(120, ch[1].X);
    }

    [Fact]
    public void Row_and_column_gap_can_be_separate()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid;
                   grid-template-columns: 100px 100px;
                   row-gap: 30px;
                   column-gap: 10px;
                   width: 210px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        // Column gap 10px between the two columns.
        Assert.Equal(110, ch[1].X);
        // Row gap 30px between the two rows.
        Assert.Equal(80, ch[2].Y); // 50 (row1 height) + 30 (gap)
    }

    [Fact]
    public void Container_auto_height_sums_row_sizes_and_gaps()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid;
                   grid-template-columns: 1fr;
                   grid-template-rows: 100px 200px;
                   row-gap: 50px;
                   width: 300px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(350, c.Height); // 100 + 50 + 200
    }

    [Fact]
    public void Display_none_child_is_skipped_from_placement()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid; grid-template-columns: 1fr 1fr;
                   width: 200px; }
              #g > * { height: 50px; }
              .hide { display: none; }
            </style></head>
            <body><div id="g">
              <div></div><div class="hide"></div><div></div>
            </div></body></html>
            """, "#g");
        // The hidden child has no layout box. Auto-placement
        // walks the children list; the placement count
        // depends on how we handle display:none. Real CSS:
        // display:none children are SKIPPED from layout
        // entirely AND from auto-placement. So the two
        // visible children take the first two cells (row 0,
        // columns 0 and 1).
        Assert.Equal(2, ch.Length);
        Assert.Equal(0, ch[0].X);
        Assert.Equal(100, ch[1].X);
        Assert.Equal(0, ch[0].Y);
        Assert.Equal(0, ch[1].Y);
    }

    [Fact]
    public void Percentage_columns_resolve_against_container_width()
    {
        var (c, ch) = FindGrid("""
            <html><head><style>
              body { margin: 0; }
              #g { display: grid; grid-template-columns: 25% 75%;
                   width: 800px; }
              #g > * { height: 50px; }
            </style></head>
            <body><div id="g">
              <div></div><div></div>
            </div></body></html>
            """, "#g");
        Assert.Equal(200, ch[0].Width);
        Assert.Equal(600, ch[1].Width);
    }
}
