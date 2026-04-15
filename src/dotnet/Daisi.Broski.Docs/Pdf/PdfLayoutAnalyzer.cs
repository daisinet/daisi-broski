using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>A page's reconstructed structure, ready for the
/// converter to render. Each block is either a paragraph
/// (a flowing run of lines) or a table (a rectangular grid of
/// cells). Order matches the visual top-to-bottom reading order
/// (descending y in PDF user-space).</summary>
internal abstract record LayoutBlock;

internal sealed record LayoutParagraph(IReadOnlyList<string> Lines) : LayoutBlock;

internal sealed record LayoutTable(
    IReadOnlyList<IReadOnlyList<string>> Rows) : LayoutBlock;

/// <summary>
/// Clusters positioned text runs from
/// <see cref="PdfTextExtractor.ExtractRuns"/> into a sequence of
/// <see cref="LayoutBlock"/>s.
///
/// <para>The strategy works in two passes. First, runs are
/// bucketed into rows by y-coordinate. Second, the analyzer
/// scans for table candidates: contiguous spans of rows that
/// have ≥2 cells each and stay close in y. Inside such a span,
/// every run's x-position is fed through a column-clustering
/// pass that merges nearby x's into shared anchors — so a
/// header reading <c>"Column header (TH)"</c> emitted as two
/// runs at x=72 and x=140 doesn't fragment into two columns
/// when subsequent rows show one cell at x=72 and another at
/// x=240. Each row's runs then snap to those clusters and
/// runs that snap to the same cluster inside the same row are
/// joined with a space.</para>
///
/// <para>Heuristics here are tuned for typical office-doc PDFs.
/// Multi-column body text can produce false-positive tables —
/// the trade-off for catching real ones. Thresholds live as
/// named constants and can be retuned as we hit failure cases.</para>
/// </summary>
internal static class PdfLayoutAnalyzer
{
    /// <summary>Two runs whose y values differ by less than this
    /// many PDF user-space units belong to the same row.</summary>
    private const double RowYTolerance = 2.0;

    /// <summary>Two x positions within this many units belong to
    /// the same column cluster. Set wide enough to absorb the
    /// per-letter advance inside a multi-word run, narrow enough
    /// to keep distinct cells separate.</summary>
    private const double ColumnClusterTolerance = 30.0;

    /// <summary>Rows whose y centers are within this many units
    /// of each other count as part of the same flowing text /
    /// the same table region.</summary>
    private const double ParagraphLineGap = 18.0;

    /// <summary>Minimum number of rows in a region for it to be
    /// considered a table.</summary>
    private const int MinTableRows = 3;

    /// <summary>Minimum number of distinct column clusters a
    /// candidate region needs.</summary>
    private const int MinTableColumns = 2;

    public static IReadOnlyList<LayoutBlock> Analyze(
        IReadOnlyList<PdfTextRun> runs)
    {
        if (runs.Count == 0) return Array.Empty<LayoutBlock>();
        var rows = ClusterIntoRows(runs);
        SortRowsInReadingOrder(rows, runs);
        return BuildBlocks(rows);
    }

    // ---------- row clustering ----------

    private static List<Row> ClusterIntoRows(IReadOnlyList<PdfTextRun> runs)
    {
        // First pass: assign every run to a row keyed on y. Two
        // runs whose y's are within tolerance share a row even
        // when they appear far apart in the content stream
        // (multi-column PDFs write column 1 top-to-bottom, then
        // column 2 — so the same visual line spans two distant
        // points in the stream).
        var rows = new List<Row>();
        foreach (var run in runs)
        {
            var existing = rows.FindIndex(r => Math.Abs(r.Y - run.Y) <= RowYTolerance);
            if (existing < 0)
            {
                var row = new Row(run.Y);
                row.Cells.Add(run);
                rows.Add(row);
            }
            else
            {
                rows[existing].Cells.Add(run);
            }
        }
        foreach (var row in rows) row.Cells.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    /// <summary>Sort rows into top-to-bottom reading order. In
    /// PDF user-space the origin is at the lower-left and y
    /// grows upward, so visually-higher = larger y, and reading
    /// order is descending y. We don't try to detect CTM flips —
    /// PDF producers that emit text via Tm/Td positioning use
    /// final user-space coordinates where this convention holds,
    /// regardless of any earlier transform.</summary>
    private static void SortRowsInReadingOrder(
        List<Row> rows, IReadOnlyList<PdfTextRun> runs)
    {
        rows.Sort((a, b) => b.Y.CompareTo(a.Y));
    }

    // ---------- main loop: emit blocks in document order ----------

    private static IReadOnlyList<LayoutBlock> BuildBlocks(List<Row> rows)
    {
        var blocks = new List<LayoutBlock>();
        int i = 0;
        while (i < rows.Count)
        {
            int candidateEnd = ScanCandidateRegion(rows, i);
            // Consider splitting a leading caption row off as its
            // own paragraph. A classic pattern:
            //   Table 3:  Some caption here
            //   Col A    Col B    Col C
            //   data …
            // Row 0 has 1-2 sparse cells, row 1 is a multi-cell
            // header starting from the leftmost anchor and is
            // separated from row 0 by more than a line's height.
            int tableStart = i;
            while (tableStart < candidateEnd - 1
                && IsCaptionRow(rows, tableStart, candidateEnd))
            {
                blocks.Add(new LayoutParagraph(new[]
                {
                    string.Join(" ",
                        rows[tableStart].Cells.Select(c => c.Text)),
                }));
                tableStart++;
            }

            int rowCount = candidateEnd - tableStart;
            if (rowCount >= MinTableRows
                && TryBuildTable(rows, tableStart, candidateEnd) is { } table)
            {
                blocks.Add(table);
                i = candidateEnd;
                continue;
            }
            // Not a table (or too few rows remaining after caption
            // split) — emit one paragraph block from i, growing
            // until the row gap is too large.
            int paraEnd = CollectParagraph(rows, i, blocks);
            i = paraEnd;
        }
        return blocks;
    }

    /// <summary>True when <c>rows[i]</c> is a classic table
    /// caption / title row that got swept into a candidate region
    /// — sparse (≤ 2 cells), separated from the next row by more
    /// than a line's height, and the next row has substantially
    /// more cells (looks like a real table header starting below
    /// the caption).</summary>
    private static bool IsCaptionRow(List<Row> rows, int i, int candidateEnd)
    {
        if (i + 1 >= candidateEnd) return false;
        if (rows[i].Cells.Count > 2) return false;
        if (rows[i + 1].Cells.Count < rows[i].Cells.Count + 2) return false;
        double gap = Math.Abs(rows[i].Y - rows[i + 1].Y);
        if (gap < ParagraphLineGap * 0.8) return false;
        return true;
    }

    /// <summary>Walk forward from <paramref name="start"/> as
    /// long as each row has at least 2 cells AND stays within a
    /// reasonable y-gap of the previous row. Returns the index
    /// past the last qualifying row. Empty/short rows interrupt
    /// the candidate so a header line above a table doesn't get
    /// pulled into the table region.</summary>
    private static int ScanCandidateRegion(List<Row> rows, int start)
    {
        if (rows[start].Cells.Count < MinTableColumns) return start;
        int end = start + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (row.Cells.Count < MinTableColumns) break;
            double gap = Math.Abs(rows[end - 1].Y - row.Y);
            if (gap > ParagraphLineGap * 1.8) break;
            end++;
        }
        return end;
    }

    /// <summary>Cluster x-positions across every row in the
    /// region and snap each row's runs to the resulting columns.
    /// Returns null when the clustering produces fewer than
    /// MinTableColumns columns — that means the region wasn't
    /// really a tabular grid.</summary>
    private static LayoutTable? TryBuildTable(List<Row> rows, int start, int end)
    {
        var allXs = new List<double>();
        for (int k = start; k < end; k++)
        {
            foreach (var cell in rows[k].Cells) allXs.Add(cell.X);
        }
        var clusters = ClusterXs(allXs);
        if (clusters.Count < MinTableColumns) return null;

        var grid = new List<IReadOnlyList<string>>(end - start);
        for (int k = start; k < end; k++)
        {
            var row = rows[k];
            var cells = new string[clusters.Count];
            for (int c = 0; c < cells.Length; c++) cells[c] = "";
            foreach (var cell in row.Cells)
            {
                int idx = NearestClusterIndex(cell.X, clusters);
                if (idx < 0) continue;
                string fragment = RenderRun(cell);
                cells[idx] = cells[idx].Length == 0
                    ? fragment
                    : cells[idx] + " " + fragment;
            }
            grid.Add(cells);
        }
        // Reject the table if every row has the same single non-
        // empty column — that means the runs all snapped to one
        // cluster and the others stayed empty (so it's not really
        // tabular).
        int nonEmptyColumns = 0;
        for (int c = 0; c < clusters.Count; c++)
        {
            for (int r = 0; r < grid.Count; r++)
            {
                if (!string.IsNullOrEmpty(grid[r][c]))
                {
                    nonEmptyColumns++;
                    break;
                }
            }
        }
        if (nonEmptyColumns < MinTableColumns) return null;

        // Reject "tables" whose cells are mostly tiny tokens —
        // punctuation that happens to align across lines (page
        // numbers, drop-cap leftovers, runs of footnote markers)
        // can mimic a grid. Real tables have substance. Count
        // only trimmed non-whitespace cells, and require the
        // average content length to clear a threshold.
        int totalChars = 0, totalCells = 0;
        foreach (var row in grid)
        {
            foreach (var cell in row)
            {
                var trimmed = cell.Trim();
                if (trimmed.Length == 0) continue;
                totalChars += trimmed.Length;
                totalCells++;
            }
        }
        // Need several substantive cells AND decent average length.
        // A 3-row table with one character per cell (the false-
        // positive we most often see) fails both checks.
        if (totalCells < MinTableRows * MinTableColumns) return null;
        double avgCellLen = (double)totalChars / totalCells;
        if (avgCellLen < 3.0) return null;
        // Require at least one row to be nearly "full" — max
        // populated cells ≥ columnCount - 1. A grid where every
        // row is half-empty is almost always title / banner text
        // whose tokens happen to fall at similar y values rather
        // than a real table. (True sparse tables with merged
        // cells still have at least one row that spans the grid.)
        int maxPopulatedPerRow = 0;
        foreach (var row in grid)
        {
            int p = 0;
            foreach (var cell in row)
                if (!string.IsNullOrWhiteSpace(cell)) p++;
            if (p > maxPopulatedPerRow) maxPopulatedPerRow = p;
        }
        if (maxPopulatedPerRow < clusters.Count - 1) return null;
        // Post-pass: collapse multi-line header fragment rows.
        // Accessibility-tagged PDFs (W3C WCAG test docs, Adobe's
        // tagging output) typeset a single logical header across
        // 2-6 baselines; each baseline becomes one of our rows,
        // each with only a subset of the column anchors populated.
        // Merge them before returning the grid so the caller gets
        // a visual table shape rather than a wall of sparse rows.
        var rowYs = new List<double>(end - start);
        for (int k = start; k < end; k++) rowYs.Add(rows[k].Y);
        grid = ConsolidateHeaderFragments(grid, rowYs, clusters.Count);
        return new LayoutTable(grid);
    }

    /// <summary>Collapse a run of "sparse" rows at the top of the
    /// grid into a single merged header row. A row is sparse when
    /// fewer than half its columns are populated. Consecutive
    /// sparse rows whose y-gaps stay within
    /// <see cref="ParagraphLineGap"/> get fused: for each column,
    /// the non-empty fragments are joined top-to-bottom with a
    /// space. Returns the grid unchanged when no consolidation
    /// applies.</summary>
    private static List<IReadOnlyList<string>> ConsolidateHeaderFragments(
        List<IReadOnlyList<string>> grid, List<double> rowYs, int columnCount)
    {
        if (grid.Count < 2) return grid;
        int headerEnd = 0;
        // A header fragment is a row that's missing at least one
        // cell. We merge the leading sparse rows until we hit a
        // fully-populated row (that's the last header line, or the
        // first data row) or a y-gap too wide to be part of the
        // header block. Cap the scan at 6 rows so a pathological
        // sparse data table never collapses entirely.
        for (int i = 0; i < grid.Count && i < 6; i++)
        {
            int populated = 0;
            foreach (var c in grid[i])
            {
                if (!string.IsNullOrWhiteSpace(c)) populated++;
            }
            if (populated >= columnCount && i > 0)
            {
                // Full row — not a header fragment. Stop here
                // without consuming the row.
                break;
            }
            if (i > 0)
            {
                double gap = Math.Abs(rowYs[i] - rowYs[i - 1]);
                if (gap > ParagraphLineGap) break;
            }
            headerEnd = i + 1;
            // A full row consumed as part of the header is the
            // natural end of the header — stop extending.
            if (populated >= columnCount) break;
        }
        // Need at least two sparse rows to be worth merging.
        if (headerEnd < 2) return grid;

        var merged = new string[columnCount];
        for (int c = 0; c < columnCount; c++) merged[c] = "";
        for (int r = 0; r < headerEnd; r++)
        {
            for (int c = 0; c < columnCount; c++)
            {
                var piece = grid[r][c].Trim();
                if (piece.Length == 0) continue;
                merged[c] = merged[c].Length == 0
                    ? piece : merged[c] + " " + piece;
            }
        }
        var result = new List<IReadOnlyList<string>>(grid.Count - headerEnd + 1)
        {
            merged
        };
        for (int r = headerEnd; r < grid.Count; r++) result.Add(grid[r]);
        return result;
    }

    /// <summary>Greedy 1-D linkage clustering: sort the values,
    /// open a new cluster every time the gap to the previous
    /// value exceeds <see cref="ColumnClusterTolerance"/>, return
    /// each cluster's centroid. Cluster count = column count.</summary>
    private static List<double> ClusterXs(List<double> xs)
    {
        if (xs.Count == 0) return new List<double>();
        xs.Sort();
        var clusters = new List<double>();
        double sum = xs[0];
        int count = 1;
        double last = xs[0];
        for (int i = 1; i < xs.Count; i++)
        {
            double x = xs[i];
            if (x - last > ColumnClusterTolerance)
            {
                clusters.Add(sum / count);
                sum = 0;
                count = 0;
            }
            sum += x;
            count++;
            last = x;
        }
        if (count > 0) clusters.Add(sum / count);
        return clusters;
    }

    private static int NearestClusterIndex(double x, IReadOnlyList<double> clusters)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < clusters.Count; i++)
        {
            double d = Math.Abs(clusters[i] - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private static int CollectParagraph(
        List<Row> rows, int start, List<LayoutBlock> blocks)
    {
        var lines = new List<string>();
        int i = start;
        while (i < rows.Count)
        {
            var row = rows[i];
            if (i > start)
            {
                double gap = Math.Abs(rows[i - 1].Y - row.Y);
                if (gap > ParagraphLineGap * 2) break;
                // If a table region starts at this row, stop.
                int tableEnd = ScanCandidateRegion(rows, i);
                if (tableEnd - i >= MinTableRows
                    && TryBuildTable(rows, i, tableEnd) is not null)
                {
                    break;
                }
            }
            lines.Add(string.Join(" ", row.Cells.Select(RenderRun)));
            i++;
        }
        if (lines.Count > 0) blocks.Add(new LayoutParagraph(lines));
        return i;
    }

    /// <summary>Render one text run as the pre-escaped HTML
    /// fragment that becomes part of a cell or paragraph line.
    /// Runs with an <c>Href</c> wrap in <c>&lt;a&gt;</c>;
    /// otherwise the text is just HTML-escaped. Callers join
    /// fragments with " " — the space is HTML-safe and doesn't
    /// need escaping.</summary>
    private static string RenderRun(PdfTextRun run)
    {
        string escapedText = HtmlWriter.EscapeText(run.Text);
        if (run.Href is null) return escapedText;
        return "<a href=\"" + HtmlWriter.EscapeAttr(run.Href) + "\">"
            + escapedText + "</a>";
    }

    private sealed class Row
    {
        public double Y { get; }
        public List<PdfTextRun> Cells { get; } = new();
        public Row(double y) => Y = y;
    }
}
