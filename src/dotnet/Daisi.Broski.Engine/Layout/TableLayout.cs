using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Paint;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Phase-6.x minimum-viable CSS 2.1 §17 table layout.
/// Implements what real pages actually reach for first:
/// <list type="bullet">
/// <item>Row groups (<c>&lt;thead&gt;</c> / <c>&lt;tbody&gt;</c>
///   / <c>&lt;tfoot&gt;</c>) flatten into a single row grid —
///   we ignore the group boundary for positioning, which
///   matches browser behavior.</item>
/// <item><c>colspan</c> and <c>rowspan</c> via a reservation
///   grid. Cells spanning multiple columns don't drive column
///   sizing (their content is distributed across already-sized
///   columns); rowspan cells add any excess height to the
///   last spanned row.</item>
/// <item>Two-pass column widths: measure each cell's
///   intrinsic max-width from its text content, then scale
///   down proportionally if the sum exceeds the container.
///   Explicit <c>width</c> attributes / CSS on a cell in
///   column <c>c</c> win for <c>c</c>.</item>
/// <item><c>border-spacing</c> between cells (defaults to
///   2px per the UA stylesheet), HTML <c>cellspacing</c> /
///   <c>cellpadding</c> attributes honored.</item>
/// </list>
///
/// <para>
/// Deliberately deferred (each its own future slice):
/// </para>
/// <list type="bullet">
/// <item><c>border-collapse</c> — today every cell owns its
///   border independently (spacing model).</item>
/// <item><c>&lt;caption&gt;</c> placement — the element is
///   parsed but not positioned above/below the table.</item>
/// <item><c>&lt;colgroup&gt;</c> / <c>&lt;col width="..."&gt;</c>
///   applying widths to whole columns.</item>
/// <item>Percentage column widths mixed with auto (we take
///   the declared width at face value and don't
///   redistribute).</item>
/// <item>Table captions, table-header-group / table-footer-group
///   semantic re-ordering (rendered in source order).</item>
/// </list>
/// </summary>
internal static class TableLayout
{
    public static void LayoutChildren(
        LayoutBox container, Element element, ComputedStyle style,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize)
    {
        // HTML legacy attributes: <table cellspacing="0"
        // cellpadding="6"> — map to CSS border-spacing on
        // the table and padding on every cell. Real browsers
        // honor these in quirks mode; we honor them always
        // because plenty of legacy sites still rely on them.
        double borderSpacing = ResolveBorderSpacing(
            style, element, container.Width, fontSize, rootFontSize);
        double cellPaddingAttr = ResolveCellPaddingAttr(element);

        // Step 1 — walk the table DOM, flattening row
        // groups into a single row list and building the
        // cell grid with colspan / rowspan reservations.
        var rows = CollectRows(element);
        if (rows.Count == 0) return;

        var grid = BuildCellGrid(rows);
        int rowCount = grid.Rows;
        int colCount = grid.Cols;
        if (colCount == 0) return;

        // Step 2 — measure each cell's intrinsic max width.
        // We lay each cell out at the container's full width
        // (a proxy for "as wide as you'd naturally be") so
        // text wraps only when it would exceed the whole
        // table. Min-width uses the longest word token as a
        // lower bound to prevent shrinking columns below
        // readability.
        var cellsByStart = grid.Cells
            .OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
        foreach (var cell in cellsByStart)
        {
            MeasureCell(cell, container, resolver, viewport,
                fontSize, rootFontSize, cellPaddingAttr);
        }

        // Step 3 — column max/min widths. Only cells with
        // colspan == 1 contribute to per-column sizing; wider
        // cells are satisfied after the fact by the sum of
        // their spanned columns. A cell with an explicit
        // declared width pins the column — the extra-space
        // distribution in step 4 must skip fixed columns,
        // otherwise a `width:100px` cell in a 1000px table
        // gets padded out to hundreds of pixels wide.
        var colMin = new double[colCount];
        var colMax = new double[colCount];
        var colFixed = new bool[colCount];
        foreach (var cell in cellsByStart)
        {
            if (cell.ColSpan != 1) continue;
            if (cell.MinWidth > colMin[cell.Col]) colMin[cell.Col] = cell.MinWidth;
            if (cell.MaxWidth > colMax[cell.Col]) colMax[cell.Col] = cell.MaxWidth;
            if (!cell.DeclaredWidth.IsNone && !cell.DeclaredWidth.IsAuto)
            {
                colFixed[cell.Col] = true;
            }
        }

        // Step 4 — pick final column widths. Available width
        // is the container's content width minus the spacing
        // between columns (and outer spacing on each edge).
        double avail = container.Width - borderSpacing * (colCount + 1);
        if (avail < 0) avail = 0;
        var colWidth = ResolveColumnWidths(colMin, colMax, colFixed, avail);

        // Step 5 — lay out each cell at its final width.
        // Column widths are known; cells spanning multiple
        // columns sum those up. Layout also finalizes
        // cell.Box.Height which feeds row sizing next.
        foreach (var cell in cellsByStart)
        {
            double cellContentWidth = 0;
            for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++)
            {
                cellContentWidth += colWidth[c];
            }
            // Add internal border-spacing "slots" that the
            // cell covers when it spans multiple columns —
            // the span absorbs the gutters between them.
            if (cell.ColSpan > 1)
            {
                cellContentWidth += borderSpacing * (cell.ColSpan - 1);
            }
            RelayoutCell(cell, container, cellContentWidth, resolver, viewport,
                fontSize, rootFontSize, cellPaddingAttr);
        }

        // Step 6 — row heights. Pass 1: single-row (rowspan
        // == 1) cells set per-row max height. Pass 2: rowspan
        // cells top up the last row they cover with any
        // excess.
        var rowHeight = new double[rowCount];
        foreach (var cell in cellsByStart)
        {
            if (cell.RowSpan != 1) continue;
            double h = LayoutTree.OuterHeight(cell.Box);
            if (h > rowHeight[cell.Row]) rowHeight[cell.Row] = h;
        }
        foreach (var cell in cellsByStart)
        {
            if (cell.RowSpan == 1) continue;
            double h = LayoutTree.OuterHeight(cell.Box);
            double covered = 0;
            int lastRow = cell.Row + cell.RowSpan - 1;
            for (int r = cell.Row; r <= lastRow; r++)
            {
                covered += rowHeight[r];
            }
            covered += borderSpacing * (cell.RowSpan - 1);
            if (h > covered)
            {
                // Bump the last spanned row so the tall
                // cell's content fits. Distributing equally
                // across rows is closer to spec but messes
                // up alignment of adjacent rows; last-row
                // absorption is the common browser heuristic.
                rowHeight[lastRow] += h - covered;
            }
        }

        // Step 7 — position every cell at its (colX, rowY)
        // origin. Descendants were laid out in Relayout with
        // a temporary box.X/Y so their absolute positions
        // need a translate by the delta between temporary and
        // final origins. Mirrors the technique GridLayout
        // uses for the same reason.
        for (int r = 0; r < rowCount; r++) { /* nothing here, placeholders */ }

        double[] colOffset = new double[colCount];
        double runX = borderSpacing;
        for (int c = 0; c < colCount; c++)
        {
            colOffset[c] = runX;
            runX += colWidth[c] + borderSpacing;
        }
        double[] rowOffset = new double[rowCount];
        double runY = borderSpacing;
        for (int r = 0; r < rowCount; r++)
        {
            rowOffset[r] = runY;
            runY += rowHeight[r] + borderSpacing;
        }

        foreach (var cell in cellsByStart)
        {
            double cellX = container.X + colOffset[cell.Col];
            double cellY = container.Y + rowOffset[cell.Row];

            // Stretch cell height to fill all spanned rows
            // so borders / backgrounds cover the full row
            // group — the default vertical-align for table
            // cells is baseline, but sizing them to the full
            // cell slot matches what real browsers paint.
            double targetHeight = 0;
            for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                targetHeight += rowHeight[r];
            }
            if (cell.RowSpan > 1)
            {
                targetHeight += borderSpacing * (cell.RowSpan - 1);
            }
            double targetContent = targetHeight
                - cell.Box.Margin.Top - cell.Box.Margin.Bottom
                - cell.Box.Padding.Top - cell.Box.Padding.Bottom
                - cell.Box.Border.Top - cell.Box.Border.Bottom;
            if (targetContent > cell.Box.Height)
            {
                cell.Box.Height = targetContent;
            }

            double finalX = cellX
                + cell.Box.Margin.Left + cell.Box.Border.Left + cell.Box.Padding.Left;
            double finalY = cellY
                + cell.Box.Margin.Top + cell.Box.Border.Top + cell.Box.Padding.Top;
            double dx = finalX - cell.Box.X;
            double dy = finalY - cell.Box.Y;
            cell.Box.X = finalX;
            cell.Box.Y = finalY;
            if (dx != 0 || dy != 0)
            {
                TranslateDescendants(cell.Box, dx, dy);
            }
            container.Children.Add(cell.Box);
        }

        // Step 8 — container height = last row bottom +
        // trailing border-spacing. If the author declared a
        // height, PrepareBox already set it; don't shrink.
        if (container.Height == 0)
        {
            double h = borderSpacing;
            for (int r = 0; r < rowCount; r++)
            {
                h += rowHeight[r] + borderSpacing;
            }
            container.Height = h;
        }
    }

    // ── Row collection ────────────────────────────────────

    /// <summary>Walk the table's child structure and
    /// flatten rows from <c>&lt;thead&gt;</c> / <c>&lt;tbody&gt;</c>
    /// / <c>&lt;tfoot&gt;</c> into a single ordered row list.
    /// Direct <c>&lt;tr&gt;</c> children of the table are
    /// accepted too — sites still write <c>&lt;table&gt;&lt;tr&gt;</c>
    /// without a surrounding tbody and the HTML parser doesn't
    /// always synthesize one for us.</summary>
    private static List<Element> CollectRows(Element table)
    {
        var rows = new List<Element>();
        foreach (var child in table.ChildNodes)
        {
            if (child is not Element el) continue;
            switch (el.TagName)
            {
                case "thead":
                case "tbody":
                case "tfoot":
                    foreach (var inner in el.ChildNodes)
                    {
                        if (inner is Element innerEl && innerEl.TagName == "tr")
                            rows.Add(innerEl);
                    }
                    break;
                case "tr":
                    rows.Add(el);
                    break;
                // caption / colgroup ignored in v1
            }
        }
        return rows;
    }

    // ── Grid building ────────────────────────────────────

    private sealed class CellInfo
    {
        public required Element Element;
        public required LayoutBox Box;
        public int Row;
        public int Col;
        public int ColSpan = 1;
        public int RowSpan = 1;
        public double MinWidth;
        public double MaxWidth;
        public double FontSize;
        public double RootFontSize;
        public Length DeclaredHeight;
        public Length DeclaredWidth;
    }

    private sealed class CellGrid
    {
        public required List<CellInfo> Cells;
        public int Rows;
        public int Cols;
    }

    private static CellGrid BuildCellGrid(List<Element> rows)
    {
        var cells = new List<CellInfo>();
        // Reservation map — true when a previous rowspan
        // cell has already claimed (r, c). Grows dynamically
        // because colspan/rowspan can push past the initial
        // row/col counts we guess.
        var reserved = new List<bool[]>();
        int colCount = 0;

        void EnsureRow(int r)
        {
            while (reserved.Count <= r)
            {
                reserved.Add(new bool[Math.Max(colCount, 1)]);
            }
        }
        void EnsureCol(int c)
        {
            if (c < colCount) return;
            int newCount = c + 1;
            for (int i = 0; i < reserved.Count; i++)
            {
                if (reserved[i].Length < newCount)
                {
                    var grown = new bool[newCount];
                    Array.Copy(reserved[i], grown, reserved[i].Length);
                    reserved[i] = grown;
                }
            }
            colCount = newCount;
        }

        for (int r = 0; r < rows.Count; r++)
        {
            EnsureRow(r);
            int col = 0;
            foreach (var child in rows[r].ChildNodes)
            {
                if (child is not Element cellEl) continue;
                if (cellEl.TagName != "td" && cellEl.TagName != "th") continue;

                // Skip forward past columns already reserved
                // by a prior row's rowspan.
                EnsureCol(col);
                while (col < reserved[r].Length && reserved[r][col])
                {
                    col++;
                    EnsureCol(col);
                }

                int colSpan = Math.Max(1, ParseSpanAttr(cellEl, "colspan"));
                int rowSpan = Math.Max(1, ParseSpanAttr(cellEl, "rowspan"));
                EnsureCol(col + colSpan - 1);
                for (int rr = r; rr < r + rowSpan; rr++)
                {
                    EnsureRow(rr);
                    EnsureCol(col + colSpan - 1);
                    for (int cc = col; cc < col + colSpan; cc++)
                    {
                        reserved[rr][cc] = true;
                    }
                }

                cells.Add(new CellInfo
                {
                    Element = cellEl,
                    Box = null!, // filled in during measurement
                    Row = r,
                    Col = col,
                    ColSpan = colSpan,
                    RowSpan = rowSpan,
                });
                col += colSpan;
            }
        }

        return new CellGrid
        {
            Cells = cells,
            Rows = reserved.Count,
            Cols = colCount,
        };
    }

    private static int ParseSpanAttr(Element el, string name)
    {
        var s = el.GetAttribute(name);
        if (string.IsNullOrEmpty(s)) return 1;
        return int.TryParse(s, out var n) && n > 0 ? n : 1;
    }

    // ── Measurement & layout ────────────────────────────

    private static void MeasureCell(
        CellInfo cell, LayoutBox container,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize, double cellPaddingAttr)
    {
        // Lay the cell out at the full container width to
        // reveal its natural max-width (text won't wrap until
        // it would have to wrap for the whole table). Min-
        // width is a floor — we use the widest single word
        // tokenized from the cell's text so columns don't
        // shrink below one-word width.
        var prepared = PrepareCellBox(
            cell.Element, container, resolver, viewport, cellPaddingAttr);
        if (prepared is null) return;
        var (box, fs, rfs, declH, declW) = prepared.Value;
        cell.Box = box;
        cell.FontSize = fs;
        cell.RootFontSize = rfs;
        cell.DeclaredHeight = declH;
        cell.DeclaredWidth = declW;

        // Explicit declared width overrides intrinsic
        // measurement — the cell is asking for a specific
        // column width. Mirrored across min + max so
        // ResolveColumnWidths picks it.
        if (!declW.IsNone && !declW.IsAuto)
        {
            double w = declW.Resolve(container.Width, fs, rfs);
            cell.MinWidth = w;
            cell.MaxWidth = w;
            return;
        }

        // Intrinsic max = natural text width (no wrap).
        double maxW = MeasureNaturalWidth(cell.Element, fs)
            + box.Padding.Left + box.Padding.Right
            + box.Border.Left + box.Border.Right
            + box.Margin.Left + box.Margin.Right;
        cell.MaxWidth = maxW;

        // Intrinsic min = longest single word.
        double minW = MeasureLongestWordWidth(cell.Element, fs)
            + box.Padding.Left + box.Padding.Right
            + box.Border.Left + box.Border.Right
            + box.Margin.Left + box.Margin.Right;
        cell.MinWidth = minW;
    }

    private static void RelayoutCell(
        CellInfo cell, LayoutBox container, double cellBorderBoxWidth,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize, double cellPaddingAttr)
    {
        if (cell.Box is null)
        {
            // MeasureCell failed (display:none, etc.) —
            // nothing to lay out.
            return;
        }

        // cellBorderBoxWidth is the sum of column widths we
        // allocated for this cell (the border-box width).
        // Back out padding / border to get the content area.
        double contentW = cellBorderBoxWidth
            - cell.Box.Margin.Left - cell.Box.Margin.Right
            - cell.Box.Padding.Left - cell.Box.Padding.Right
            - cell.Box.Border.Left - cell.Box.Border.Right;
        if (contentW < 0) contentW = 0;
        cell.Box.Width = contentW;

        // Reset position to zero so child layouts reference
        // cell-relative coords; we translate to final
        // location in step 7 after row heights are known.
        cell.Box.X = 0;
        cell.Box.Y = 0;
        cell.Box.Height = 0;
        cell.Box.Children.Clear();

        LayoutTree.LayChildrenAndResolveHeight(
            cell.Box, cell.Element, resolver, viewport,
            cell.FontSize, cell.RootFontSize, cell.DeclaredHeight, container.Height);
    }

    /// <summary>Build a cell's LayoutBox using the normal
    /// PrepareBox path so padding / border / margin come
    /// from the cascade. Then overlay the
    /// <c>cellpadding</c> attribute if the cascade left
    /// padding at zero — it's a legacy default.</summary>
    private static (LayoutBox Box, double FontSize, double RootFontSize,
        Length DeclaredHeight, Length DeclaredWidth)?
        PrepareCellBox(
            Element cellEl, LayoutBox container,
            LayoutStyleResolver resolver, Viewport viewport,
            double cellPaddingAttr)
    {
        // Save/restore container.Width — PrepareBox uses it
        // as the containing-block width for percentage
        // resolution inside the cell, which we want to be
        // the whole table content width during measurement.
        var prepared = LayoutTree.PrepareBox(container, cellEl, resolver, viewport);
        if (prepared is null) return null;
        var (box, fs, rfs, declH) = prepared.Value;

        var style = resolver.Resolve(cellEl);
        var declW = Length.Parse(style.GetPropertyValue("width"));

        // Apply cellpadding attribute if the cascade hasn't
        // already given this cell padding — the UA rule
        // `td, th { padding: 1px }` puts 1 everywhere, so
        // we only top up when the cellpadding attr is
        // larger and the cascade padding is ≤ 1.
        if (cellPaddingAttr > 0)
        {
            var cur = box.Padding;
            double need = cellPaddingAttr;
            if (cur.Top <= 1 && cur.Right <= 1 && cur.Bottom <= 1 && cur.Left <= 1)
            {
                box.Padding = new BoxEdges(need, need, need, need);
            }
        }

        return (box, fs, rfs, declH, declW);
    }

    // ── Column width resolution ─────────────────────────

    /// <summary>Pick a final width per column given each
    /// column's min / max requirement and the
    /// <paramref name="avail"/> space. When the max sum
    /// fits, columns get their max. When it doesn't, shrink
    /// proportionally between min and max. When even the min
    /// sum doesn't fit, we clamp to min (columns overflow
    /// the container).</summary>
    private static double[] ResolveColumnWidths(
        double[] colMin, double[] colMax, bool[] colFixed, double avail)
    {
        int n = colMin.Length;
        var result = new double[n];
        if (n == 0) return result;

        double sumMax = 0, sumMin = 0;
        int flexCount = 0;
        for (int i = 0; i < n; i++)
        {
            sumMax += colMax[i];
            sumMin += colMin[i];
            if (!colFixed[i]) flexCount++;
        }

        if (sumMax <= avail + 0.5)
        {
            // Max sum fits. Give every column its max; for
            // the leftover, distribute across flex (non-
            // fixed) columns only — explicit widths stay as
            // declared. If every column is fixed, we just
            // honor their declared widths and the table is
            // narrower than its container.
            double extra = flexCount > 0 ? (avail - sumMax) / flexCount : 0;
            for (int i = 0; i < n; i++)
            {
                result[i] = colFixed[i]
                    ? colMax[i]
                    : colMax[i] + Math.Max(0, extra);
            }
            return result;
        }

        if (sumMin >= avail)
        {
            // Table is narrower than needed even at min
            // widths. Return mins; content will overflow.
            Array.Copy(colMin, result, n);
            return result;
        }

        // Interpolate between min and max in proportion to
        // each column's growth room (max - min). Fixed
        // columns don't shrink — the overflow falls on the
        // flex columns.
        double totalRoom = 0;
        for (int i = 0; i < n; i++)
        {
            if (!colFixed[i]) totalRoom += colMax[i] - colMin[i];
        }
        double shortfall = sumMax - avail;
        for (int i = 0; i < n; i++)
        {
            if (colFixed[i])
            {
                result[i] = colMax[i];
                continue;
            }
            double room = colMax[i] - colMin[i];
            double shrink = totalRoom > 0 ? (room / totalRoom) * shortfall : 0;
            result[i] = colMax[i] - shrink;
            if (result[i] < colMin[i]) result[i] = colMin[i];
        }
        return result;
    }

    // ── Helpers ─────────────────────────────────────────

    private static void TranslateDescendants(LayoutBox box, double dx, double dy)
    {
        foreach (var child in box.Children)
        {
            child.X += dx;
            child.Y += dy;
            TranslateDescendants(child, dx, dy);
        }
    }

    private static double ResolveBorderSpacing(
        ComputedStyle style, Element table, double containingWidth,
        double fontSize, double rootFontSize)
    {
        var css = style.GetPropertyValue("border-spacing");
        if (!string.IsNullOrWhiteSpace(css))
        {
            // border-spacing can be one value (both axes) or
            // two (horizontal vertical). We only need
            // horizontal for column math; vertical gap uses
            // the same value since we use a single uniform
            // border-spacing throughout.
            var first = css.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            var len = Length.Parse(first);
            if (!len.IsNone)
            {
                return len.Resolve(containingWidth, fontSize, rootFontSize);
            }
        }
        var attr = table.GetAttribute("cellspacing");
        if (!string.IsNullOrEmpty(attr) && double.TryParse(attr, out var n))
        {
            return n;
        }
        // UA stylesheet sets 2px on <table>.
        return 2;
    }

    private static double ResolveCellPaddingAttr(Element table)
    {
        var attr = table.GetAttribute("cellpadding");
        if (string.IsNullOrEmpty(attr)) return 0;
        return double.TryParse(attr, out var n) ? n : 0;
    }

    /// <summary>Recursive bitmap-based text width. Deliberately
    /// does NOT route through the webfont dispatch that
    /// <see cref="InlineLayout"/>'s wrap math uses — column
    /// sizing only needs a "how much will this text take"
    /// estimate, and the bitmap cellWidth is a slight over-
    /// estimate which is the safe direction for column
    /// widths (too-wide columns fit content; too-narrow ones
    /// force wraps the author didn't intend).</summary>
    private static double MeasureNaturalWidth(Element element, double fontSize)
    {
        int scale = BitmapFont.ScaleFor(fontSize);
        int cellW = BitmapFont.CellWidth * scale;
        return MeasureTextChars(element) * cellW;
    }

    private static int MeasureTextChars(Element element)
    {
        int total = 0;
        foreach (var child in element.ChildNodes)
        {
            if (child is Text t)
            {
                total += NormalizeSpaces(t.Data).Length;
            }
            else if (child is Element e)
            {
                total += MeasureTextChars(e);
            }
        }
        return total;
    }

    private static double MeasureLongestWordWidth(Element element, double fontSize)
    {
        int scale = BitmapFont.ScaleFor(fontSize);
        int cellW = BitmapFont.CellWidth * scale;
        int best = 0;
        CollectWords(element, (word) =>
        {
            if (word.Length > best) best = word.Length;
        });
        return best * cellW;
    }

    private static void CollectWords(Element element, Action<string> visit)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is Text t)
            {
                foreach (var word in NormalizeSpaces(t.Data)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    visit(word);
                }
            }
            else if (child is Element e)
            {
                CollectWords(e, visit);
            }
        }
    }

    private static string NormalizeSpaces(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool prev = false;
        foreach (var c in s)
        {
            bool ws = char.IsWhiteSpace(c);
            if (ws && prev) continue;
            sb.Append(ws ? ' ' : c);
            prev = ws;
        }
        return sb.ToString();
    }

}
