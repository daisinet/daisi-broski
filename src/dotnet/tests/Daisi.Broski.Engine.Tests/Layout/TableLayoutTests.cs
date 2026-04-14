using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Layout;

/// <summary>
/// Phase 6.x — CSS 2.1 §17 table layout. Replaces the
/// previous UA stylesheet hack (<c>tr { display: flex }</c>).
/// These tests lock in the common web-table cases:
/// cells stack side-by-side with real column math,
/// colspan covers multiple columns, rowspan extends into
/// subsequent rows, row-group elements flatten into a
/// single row grid, and the table container grows to fit
/// its rows.
/// </summary>
public class TableLayoutTests
{
    private static LayoutBox Layout(string html)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        return LayoutTree.Build(doc);
    }

    private static LayoutBox Find(string html, string selector)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        var el = doc.QuerySelector(selector);
        Assert.NotNull(el);
        var root = LayoutTree.Build(doc);
        var box = LayoutTree.Find(root, el!);
        Assert.NotNull(box);
        return box!;
    }

    [Fact]
    public void Two_cells_stack_side_by_side()
    {
        // Both cells should end up on the same row (same Y,
        // different X). This was broken before 6at for
        // tables without colgroup because td → block
        // stacked them vertically.
        var html = """
            <html><head><style>body { margin: 0; }</style></head>
            <body><table><tr>
              <td id="a">left</td>
              <td id="b">right</td>
            </tr></table></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var root = LayoutTree.Build(doc);
        var a = LayoutTree.Find(root, doc.QuerySelector("#a")!);
        var b = LayoutTree.Find(root, doc.QuerySelector("#b")!);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Y, b!.Y, 1);
        Assert.True(b.X > a.X, $"b.X={b.X} should be right of a.X={a.X}");
    }

    [Fact]
    public void Colspan_spans_multiple_columns()
    {
        // Header row has one colspan=3 cell; the body row
        // has three single-column cells. The header cell
        // should be roughly as wide as the sum of the three
        // body cells.
        var html = """
            <html><head><style>body { margin: 0; }</style></head>
            <body><table style="width:300px; border-spacing:0">
              <tr><th id="h" colspan="3">Header</th></tr>
              <tr><td id="c1">1</td><td>2</td><td>3</td></tr>
            </table></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var root = LayoutTree.Build(doc);
        var header = LayoutTree.Find(root, doc.QuerySelector("#h")!);
        var c1 = LayoutTree.Find(root, doc.QuerySelector("#c1")!);
        Assert.NotNull(header);
        Assert.NotNull(c1);
        // Header outer width should be near table width (300)
        // and much wider than a single-column cell.
        Assert.True(header!.Width > c1!.Width * 2,
            $"header.W={header.Width} c1.W={c1.Width} — colspan didn't expand");
    }

    [Fact]
    public void Rowspan_covers_subsequent_rows()
    {
        // The rowspan=2 cell in the first row should live
        // in column 0, and the second row's remaining cell
        // should skip column 0 and sit in column 1.
        var html = """
            <html><head><style>body { margin: 0; }</style></head>
            <body><table>
              <tr><td id="rs" rowspan="2">tall</td><td id="r1c1">r1</td></tr>
              <tr><td id="r2c1">r2</td></tr>
            </table></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var root = LayoutTree.Build(doc);
        var rs = LayoutTree.Find(root, doc.QuerySelector("#rs")!);
        var r1c1 = LayoutTree.Find(root, doc.QuerySelector("#r1c1")!);
        var r2c1 = LayoutTree.Find(root, doc.QuerySelector("#r2c1")!);
        Assert.NotNull(rs);
        Assert.NotNull(r1c1);
        Assert.NotNull(r2c1);
        // r2c1 should be in the same column as r1c1 (not
        // column 0) because rs reserves column 0 for both
        // rows.
        Assert.Equal(r1c1!.X, r2c1!.X, 1);
        // r2c1 sits below r1c1.
        Assert.True(r2c1.Y > r1c1.Y);
        // rs's vertical range covers both rows.
        Assert.True(rs!.Height >= r1c1.Height + r2c1.Height - 10,
            $"rs.Height={rs.Height} should cover both rows " +
            $"({r1c1.Height}+{r2c1.Height})");
    }

    [Fact]
    public void Thead_tbody_tfoot_flatten_into_one_row_grid()
    {
        // A table with thead/tbody/tfoot should position
        // rows in source order regardless of the grouping
        // element. thead row, then tbody row, then tfoot
        // row — stacked vertically.
        var html = """
            <html><head><style>body { margin: 0; }</style></head>
            <body><table>
              <thead><tr><td id="head">H</td></tr></thead>
              <tbody><tr><td id="body">B</td></tr></tbody>
              <tfoot><tr><td id="foot">F</td></tr></tfoot>
            </table></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var root = LayoutTree.Build(doc);
        var head = LayoutTree.Find(root, doc.QuerySelector("#head")!);
        var body = LayoutTree.Find(root, doc.QuerySelector("#body")!);
        var foot = LayoutTree.Find(root, doc.QuerySelector("#foot")!);
        Assert.NotNull(head);
        Assert.NotNull(body);
        Assert.NotNull(foot);
        Assert.True(body!.Y > head!.Y, "body should sit below head");
        Assert.True(foot!.Y > body.Y, "foot should sit below body");
    }

    [Fact]
    public void Table_height_grows_to_fit_rows()
    {
        // Three rows of a fixed-height cell → the table
        // container's height should accommodate all three
        // plus the border-spacing slots. The old flex-row
        // hack gave tables zero height when rows lacked an
        // explicit size.
        var html = """
            <html><head><style>
              body { margin: 0; }
              td { height: 20px; }
            </style></head>
            <body><table id="t" style="border-spacing:0">
              <tr><td>a</td></tr>
              <tr><td>b</td></tr>
              <tr><td>c</td></tr>
            </table></body></html>
            """;
        var table = Find(html, "#t");
        Assert.True(table.Height >= 60,
            $"table.Height={table.Height} should fit 3 × 20px rows");
    }

    [Fact]
    public void Explicit_cell_width_is_honored()
    {
        // A cell with a CSS `width` should get that width
        // (±1 for border-spacing rounding).
        var html = """
            <html><head><style>body { margin: 0; }</style></head>
            <body><table style="border-spacing:0">
              <tr><td id="fixed" style="width:100px">fixed</td>
                  <td>auto</td></tr>
            </table></body></html>
            """;
        var fixedCell = Find(html, "#fixed");
        Assert.InRange(fixedCell.Width, 90, 110);
    }
}
