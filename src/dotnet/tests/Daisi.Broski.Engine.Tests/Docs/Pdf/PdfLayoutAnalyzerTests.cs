using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Unit tests for the layout analyzer. Each test constructs
/// positioned <see cref="PdfTextRun"/>s directly — no PDF bytes
/// involved — so the column-clustering, row-grouping, and
/// table-detection heuristics can be pinned individually.
/// </summary>
public class PdfLayoutAnalyzerTests
{
    private static PdfTextRun R(double x, double y, string text) => new(x, y, text);

    [Fact]
    public void Empty_input_returns_no_blocks()
    {
        var blocks = PdfLayoutAnalyzer.Analyze(Array.Empty<PdfTextRun>());
        Assert.Empty(blocks);
    }

    [Fact]
    public void Single_row_becomes_a_paragraph()
    {
        var blocks = PdfLayoutAnalyzer.Analyze(new[]
        {
            R(72, 700, "Hello"),
            R(120, 700, "world"),
        });
        var para = Assert.IsType<LayoutParagraph>(Assert.Single(blocks));
        Assert.Single(para.Lines);
        Assert.Contains("Hello", para.Lines[0]);
        Assert.Contains("world", para.Lines[0]);
    }

    [Fact]
    public void Paragraph_rows_emit_in_descending_y()
    {
        // Runs arrive in non-reading order but should be regrouped
        // into top-to-bottom visual order. Row spacing is 14 PDF
        // units — tight enough that they stay in one paragraph.
        var blocks = PdfLayoutAnalyzer.Analyze(new[]
        {
            R(72, 686, "third"),
            R(72, 714, "first"),
            R(72, 700, "second"),
        });
        var para = Assert.IsType<LayoutParagraph>(Assert.Single(blocks));
        Assert.Equal(new[] { "first", "second", "third" }, para.Lines);
    }

    [Fact]
    public void Four_row_grid_with_aligned_columns_becomes_a_table()
    {
        // Four rows, each with three cells at x ≈ 72, 200, 330.
        // Cell text is substantial ("Alpha", "Beta", etc.) so
        // the content-density filter accepts.
        var runs = new List<PdfTextRun>();
        string[] headers = { "Name", "Year", "Count" };
        string[][] data =
        {
            new[] { "Alpha", "2021", "123" },
            new[] { "Beta",  "2022", "456" },
            new[] { "Gamma", "2023", "789" },
        };
        double y = 700;
        foreach (var h in headers) runs.Add(R(72 + Array.IndexOf(headers, h) * 130, y, h));
        y -= 14;
        foreach (var row in data)
        {
            for (int c = 0; c < row.Length; c++)
                runs.Add(R(72 + c * 130, y, row[c]));
            y -= 14;
        }
        var blocks = PdfLayoutAnalyzer.Analyze(runs);
        var table = Assert.IsType<LayoutTable>(Assert.Single(blocks));
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(3, table.Rows[0].Count);
        Assert.Equal("Name", table.Rows[0][0]);
        Assert.Equal("Year", table.Rows[0][1]);
        Assert.Equal("Count", table.Rows[0][2]);
        Assert.Equal("Beta", table.Rows[2][0]);
        Assert.Equal("456", table.Rows[2][2]);
    }

    [Fact]
    public void Short_punctuation_grid_is_not_a_table()
    {
        // Page-header garbage: three rows of single-character
        // tokens that happen to align. Content-density filter
        // must reject this.
        var blocks = PdfLayoutAnalyzer.Analyze(new[]
        {
            R(72, 700, "."), R(200, 700, ","),
            R(72, 690, ":"), R(200, 690, ";"),
            R(72, 680, "?"), R(200, 680, "!"),
        });
        Assert.All(blocks, b => Assert.IsType<LayoutParagraph>(b));
    }

    [Fact]
    public void Two_row_grid_does_not_meet_MinTableRows()
    {
        var blocks = PdfLayoutAnalyzer.Analyze(new[]
        {
            R(72, 700, "Alpha"), R(200, 700, "2021"),
            R(72, 686, "Beta"),  R(200, 686, "2022"),
        });
        Assert.All(blocks, b => Assert.IsType<LayoutParagraph>(b));
    }

    [Fact]
    public void Runs_in_same_row_with_similar_x_merge_into_one_cell()
    {
        // Real PDFs emit "Column header (TH)" as two runs ~25
        // units apart. After column clustering with tolerance
        // 30, they should snap to the same column and join with
        // a space.
        var runs = new List<PdfTextRun>();
        double y = 700;
        for (int i = 0; i < 3; i++)
        {
            runs.Add(R(72, y, "Column"));
            runs.Add(R(97, y, "header"));
            runs.Add(R(200, y, "(TH)" + i));
            y -= 14;
        }
        var blocks = PdfLayoutAnalyzer.Analyze(runs);
        var table = Assert.IsType<LayoutTable>(Assert.Single(blocks));
        Assert.Equal(3, table.Rows.Count);
        foreach (var row in table.Rows)
        {
            Assert.Equal(2, row.Count);
            Assert.Contains("Column", row[0]);
            Assert.Contains("header", row[0]);
        }
    }

    [Fact]
    public void Multi_line_header_fragments_collapse_into_one_row()
    {
        // Simulates a 3-column accessibility-tagged header written
        // across 3 baselines: line 1 fills col 0, line 2 fills col
        // 1, line 3 fills col 2. The data rows below are full
        // (every column populated). Consolidation should merge
        // the 3 fragment rows into a single header row.
        var runs = new List<PdfTextRun>();
        double y = 700;
        // Header fragments (one cell per row, rotating columns)
        runs.Add(R(72, y, "Part"));
        runs.Add(R(200, y, "Count"));
        y -= 12;
        runs.Add(R(72, y, "Name"));
        runs.Add(R(300, y, "Total"));
        y -= 12;
        runs.Add(R(200, y, "Extra"));
        runs.Add(R(300, y, "Sum"));
        y -= 14;
        // Data rows (full)
        for (int i = 0; i < 3; i++)
        {
            runs.Add(R(72, y, $"D{i}-A"));
            runs.Add(R(200, y, $"D{i}-B"));
            runs.Add(R(300, y, $"D{i}-C"));
            y -= 14;
        }
        var blocks = PdfLayoutAnalyzer.Analyze(runs);
        var table = Assert.IsType<LayoutTable>(Assert.Single(blocks));
        // Header rows + 3 data rows = 4 total (headers merged).
        Assert.Equal(4, table.Rows.Count);
        // The merged header row should contain text from all three
        // fragment rows joined per column.
        Assert.Contains("Part", table.Rows[0][0]);
        Assert.Contains("Name", table.Rows[0][0]);
        Assert.Contains("Count", table.Rows[0][1]);
        Assert.Contains("Extra", table.Rows[0][1]);
        Assert.Contains("Total", table.Rows[0][2]);
        Assert.Contains("Sum", table.Rows[0][2]);
        // Data rows intact.
        Assert.Equal("D0-A", table.Rows[1][0]);
        Assert.Equal("D2-C", table.Rows[3][2]);
    }

    [Fact]
    public void Leading_caption_row_splits_off_as_paragraph()
    {
        // Classic table caption: a sparse row ("Table 1") at the
        // top, separated from the header row below by more than a
        // line's height. Expected output: one paragraph followed
        // by a 4-row (header + 3 data) table, not a 5-row table
        // that includes the caption as its first row.
        var runs = new List<PdfTextRun>();
        runs.Add(R(72, 700, "Table 1"));
        double y = 670; // deliberate gap > ParagraphLineGap
        // Header row (3 cells)
        runs.Add(R(72, y, "Name"));
        runs.Add(R(200, y, "Year"));
        runs.Add(R(330, y, "Count"));
        y -= 14;
        for (int i = 0; i < 3; i++)
        {
            runs.Add(R(72, y, $"Alpha{i}"));
            runs.Add(R(200, y, $"202{i}"));
            runs.Add(R(330, y, $"{i * 10}"));
            y -= 14;
        }
        var blocks = PdfLayoutAnalyzer.Analyze(runs);
        Assert.Equal(2, blocks.Count);
        var para = Assert.IsType<LayoutParagraph>(blocks[0]);
        Assert.Contains("Table 1", para.Lines[0]);
        var table = Assert.IsType<LayoutTable>(blocks[1]);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Name", table.Rows[0][0]);
    }

    [Fact]
    public void Non_rectangular_content_falls_back_to_paragraphs()
    {
        // Runs in the same y-range but with completely different
        // x-patterns per row don't form a grid. Expect paragraph.
        var blocks = PdfLayoutAnalyzer.Analyze(new[]
        {
            R(72, 700, "Wide banner heading spanning the page"),
            R(72, 686, "Body text paragraph one"),
            R(72, 672, "Body text paragraph two"),
        });
        // Each row has one cell — not a multi-column region at all.
        Assert.All(blocks, b => Assert.IsType<LayoutParagraph>(b));
    }
}
