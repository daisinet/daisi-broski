using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Phase-6e minimum-viable CSS Grid layout. Implements the
/// grid patterns real pages reach for first: explicit
/// <c>grid-template-columns</c> / <c>grid-template-rows</c>
/// with <c>fr</c> + pixel + percentage tracks, <c>repeat()</c>,
/// <c>row-gap</c> / <c>column-gap</c> / <c>gap</c>, plus
/// row-major auto-placement of children into the resulting
/// cell grid.
///
/// <para>
/// Deliberately deferred:
/// <list type="bullet">
/// <item>Explicit placement (<c>grid-row</c> / <c>grid-column</c>
///   / <c>grid-area</c>) — every child takes one auto-placed
///   cell.</item>
/// <item>Named lines / template areas.</item>
/// <item>Subgrid.</item>
/// <item>Auto-flow:dense, auto-flow:column.</item>
/// <item>Span (<c>grid-column: span 2</c>).</item>
/// <item>Implicit grid expansion past the declared template
///   — overflow children land in extra rows of the SAME
///   column count, which matches what most layouts want.</item>
/// <item>Min-content / max-content / minmax() track sizing —
///   only fixed lengths, percentages, and <c>fr</c>
///   participate in the resolution.</item>
/// </list>
/// </para>
/// </summary>
internal static class GridLayout
{
    public static void LayoutChildren(
        LayoutBox container, Element element, ComputedStyle style,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize)
    {
        var columnTemplate = ParseTemplate(
            style.GetPropertyValue("grid-template-columns"));
        var rowTemplate = ParseTemplate(
            style.GetPropertyValue("grid-template-rows"));

        // Empty column template defaults to one fr column;
        // empty row template means "auto rows everywhere"
        // (their height comes from the cells they hold).
        if (columnTemplate.Count == 0) columnTemplate.Add(TrackSize.Fr(1));
        bool autoRows = rowTemplate.Count == 0;

        var (rowGap, columnGap) = ParseGap(style, container.Width, fontSize, rootFontSize);

        // 1) Resolve column track sizes against container.Width.
        var columnSizes = ResolveTracks(
            columnTemplate, container.Width, columnGap, fontSize, rootFontSize);
        int columnCount = columnSizes.Count;

        // 2) Build child boxes WITHOUT positioning and lay
        // out their own descendants — we need their
        // intrinsic heights to size auto rows.
        var pending = new List<PendingChild>();
        int placement = 0;
        foreach (var c in element.ChildNodes)
        {
            if (c is not Element childEl) continue;

            int row = placement / columnCount;
            int col = placement % columnCount;
            double cellWidth = columnSizes[col];

            double saveContainerWidth = container.Width;
            double saveContainerHeight = container.Height;
            container.Width = cellWidth;
            // Container.Height for the child's percentage
            // resolution: leave the container's actual value
            // (zero or declared).
            var prepared = LayoutTree.PrepareBox(container, childEl, resolver, viewport);
            container.Width = saveContainerWidth;
            container.Height = saveContainerHeight;
            if (prepared is null) continue;

            var (box, itemFontSize, itemRootFs, itemDeclaredHeight) = prepared.Value;
            container.Children.Add(box);

            // Lay the child's own descendants in a temporary
            // (0, 0) position so the box.Height accumulates
            // from content. We re-position into the cell once
            // row sizes are known.
            box.X = 0; box.Y = 0;
            LayoutTree.LayChildrenAndResolveHeight(
                box, childEl, resolver, viewport,
                itemFontSize, itemRootFs, itemDeclaredHeight,
                container.Height);

            pending.Add(new PendingChild
            {
                Element = childEl, Box = box, Row = row, Column = col,
                IntrinsicHeight = box.Height,
                DeclaredHeight = itemDeclaredHeight,
                FontSize = itemFontSize, RootFontSize = itemRootFs,
            });
            placement++;
        }

        // 3) Determine number of rows actually used and
        // expand the row template accordingly.
        int rowsNeeded = pending.Count > 0
            ? pending[^1].Row + 1
            : 1;
        var effectiveRowTemplate = autoRows
            ? Enumerable.Repeat(TrackSize.Auto, rowsNeeded).ToList()
            : ExpandRowTemplate(rowTemplate, rowsNeeded);

        // 4) Resolve row track sizes. Auto tracks pull their
        // height from the max child outer height in that row
        // — measured against the intrinsic heights we just
        // computed.
        var rowSizes = ResolveRowTracks(
            effectiveRowTemplate, pending, container.Height, rowGap,
            fontSize, rootFontSize);

        // 5) Position each child into its now-known cell.
        // Descendants were laid out against a zero-origin
        // parent back in step 2, so after placing the child we
        // also translate the child's whole subtree by the same
        // delta — otherwise grandchildren stay anchored at
        // (0, 0) of the document.
        foreach (var pc in pending)
        {
            double cellX = container.X + SumPrefix(columnSizes, pc.Column) + columnGap * pc.Column;
            double cellY = container.Y + SumPrefix(rowSizes, pc.Row) + rowGap * pc.Row;
            double cellWidth = columnSizes[pc.Column];
            double cellHeight = rowSizes[pc.Row];

            double finalX = cellX
                + pc.Box.Margin.Left + pc.Box.Border.Left + pc.Box.Padding.Left;
            double finalY = cellY
                + pc.Box.Margin.Top + pc.Box.Border.Top + pc.Box.Padding.Top;
            double dx = finalX - pc.Box.X;
            double dy = finalY - pc.Box.Y;
            pc.Box.X = finalX;
            pc.Box.Y = finalY;
            if (dx != 0 || dy != 0)
            {
                TranslateDescendants(pc.Box, dx, dy);
            }

            // Stretch the child to the cell when it didn't
            // declare its own dimensions (default align /
            // justify-items in grid is stretch).
            if (pc.DeclaredHeight.IsNone || pc.DeclaredHeight.IsAuto)
            {
                if (pc.Box.Height < cellHeight)
                {
                    pc.Box.Height = Math.Max(0, cellHeight
                        - pc.Box.Margin.Top - pc.Box.Margin.Bottom
                        - pc.Box.Padding.Top - pc.Box.Padding.Bottom
                        - pc.Box.Border.Top - pc.Box.Border.Bottom);
                }
            }
        }

        // 6) Container's auto-height: sum of row sizes + gaps.
        if (container.Height == 0)
        {
            double total = 0;
            for (int r = 0; r < rowSizes.Count; r++)
            {
                total += rowSizes[r];
                if (r > 0) total += rowGap;
            }
            container.Height = total;
        }
    }

    private static void TranslateDescendants(LayoutBox box, double dx, double dy)
    {
        foreach (var child in box.Children)
        {
            child.X += dx;
            child.Y += dy;
            TranslateDescendants(child, dx, dy);
        }
    }

    private sealed class PendingChild
    {
        public Element Element = null!;
        public LayoutBox Box = null!;
        public int Row;
        public int Column;
        public double IntrinsicHeight;
        public Length DeclaredHeight;
        public double FontSize;
        public double RootFontSize;
    }

    /// <summary>Like <see cref="ResolveTracks"/> for rows but
    /// auto-sized tracks pull their height from the max
    /// intrinsic outer-height of children placed in that
    /// row. Then fr-tracks distribute remaining space (which
    /// is zero unless the container has a declared height
    /// that exceeds the auto-sum).</summary>
    private static List<double> ResolveRowTracks(
        List<TrackSize> template, List<PendingChild> children,
        double available, double gap,
        double fontSize, double rootFontSize)
    {
        if (template.Count == 0) return new List<double>();
        var sizes = new List<double>(template.Count);
        for (int r = 0; r < template.Count; r++)
        {
            var t = template[r];
            switch (t.Kind)
            {
                case TrackKind.Length:
                    sizes.Add(Math.Max(0,
                        t.Length.Resolve(available, fontSize, rootFontSize)));
                    break;
                case TrackKind.Fr:
                    sizes.Add(0); // resolved in pass 2
                    break;
                case TrackKind.Auto:
                    {
                        double max = 0;
                        foreach (var c in children)
                        {
                            if (c.Row != r) continue;
                            double outer = c.Box.Height
                                + c.Box.Margin.Top + c.Box.Margin.Bottom
                                + c.Box.Padding.Top + c.Box.Padding.Bottom
                                + c.Box.Border.Top + c.Box.Border.Bottom;
                            if (outer > max) max = outer;
                        }
                        sizes.Add(max);
                        break;
                    }
            }
        }
        // Pass 2: distribute remaining (only when declared
        // available > sum of fixed + auto + gaps).
        double totalGap = gap * (template.Count - 1);
        double consumed = sizes.Sum() + totalGap;
        double remaining = available - consumed;
        double frTotal = 0;
        for (int i = 0; i < template.Count; i++)
            if (template[i].Kind == TrackKind.Fr) frTotal += template[i].Value;
        if (frTotal > 0 && remaining > 0)
        {
            for (int i = 0; i < template.Count; i++)
            {
                if (template[i].Kind == TrackKind.Fr)
                {
                    sizes[i] = remaining * (template[i].Value / frTotal);
                }
            }
        }
        return sizes;
    }

    /// <summary>Resolve a track template into pixel widths
    /// against an available size. Fixed tracks (px / em / %)
    /// go first; remaining space distributes among
    /// <c>fr</c> tracks proportional to their flex factor
    /// (CSS Grid §11). Auto-tracks behave like 0 in v1 (real
    /// browsers measure max-content size; we don't have
    /// content sizes yet).</summary>
    private static List<double> ResolveTracks(
        List<TrackSize> template, double available, double gap,
        double fontSize, double rootFontSize)
    {
        if (template.Count == 0) return new List<double>();
        double totalGap = gap * (template.Count - 1);
        double remaining = available - totalGap;
        double frTotal = 0;
        var sizes = new List<double>(template.Count);
        // Pass 1: fixed-size tracks consume their share.
        foreach (var t in template)
        {
            if (t.Kind == TrackKind.Fr)
            {
                sizes.Add(0);
                frTotal += t.Value;
            }
            else if (t.Kind == TrackKind.Auto)
            {
                sizes.Add(0); // treated as 0 in v1
            }
            else
            {
                double px = t.Length.Resolve(available, fontSize, rootFontSize);
                sizes.Add(Math.Max(0, px));
                remaining -= px;
            }
        }
        // Pass 2: distribute remaining to fr tracks.
        if (frTotal > 0 && remaining > 0)
        {
            for (int i = 0; i < template.Count; i++)
            {
                if (template[i].Kind == TrackKind.Fr)
                {
                    sizes[i] = remaining * (template[i].Value / frTotal);
                }
            }
        }
        return sizes;
    }

    /// <summary>Parse <c>grid-template-{columns,rows}</c>
    /// into a flat list of <see cref="TrackSize"/>. Handles
    /// <c>repeat(N, ...)</c> by expansion. Tokens we don't
    /// understand collapse to <see cref="TrackSize.Auto"/>
    /// rather than throwing — keeps malformed CSS from
    /// breaking the page.</summary>
    internal static List<TrackSize> ParseTemplate(string source)
    {
        var result = new List<TrackSize>();
        if (string.IsNullOrWhiteSpace(source)) return result;

        var tokens = TokenizeTemplate(source);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) && t.EndsWith(")"))
            {
                var inner = t.Substring(7, t.Length - 8);
                int firstComma = inner.IndexOf(',');
                if (firstComma < 0) continue;
                if (!int.TryParse(inner.AsSpan(0, firstComma).Trim(), out var times)) continue;
                if (times <= 0) continue;
                var repeatTracks = ParseTemplate(inner.Substring(firstComma + 1));
                for (int n = 0; n < times; n++)
                {
                    result.AddRange(repeatTracks);
                }
                continue;
            }
            result.Add(ParseTrack(t));
        }
        return result;
    }

    /// <summary>Split a template-tracks value into top-level
    /// tokens, preserving balanced parentheses (so
    /// <c>repeat(3, 1fr)</c> is one token, not three).</summary>
    private static List<string> TokenizeTemplate(string source)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var c in source)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static TrackSize ParseTrack(string token)
    {
        if (string.IsNullOrEmpty(token)) return TrackSize.Auto;
        var lower = token.ToLowerInvariant();
        if (lower == "auto") return TrackSize.Auto;
        if (lower.EndsWith("fr"))
        {
            if (double.TryParse(lower.AsSpan(0, lower.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fr))
            {
                return TrackSize.Fr(fr);
            }
            return TrackSize.Auto;
        }
        var len = Length.Parse(token);
        return len.IsNone ? TrackSize.Auto : new TrackSize(TrackKind.Length, len, 0);
    }

    private static (double RowGap, double ColumnGap) ParseGap(
        ComputedStyle style, double containerWidth,
        double fontSize, double rootFontSize)
    {
        // `gap: <row> <column>?` shorthand — when only one
        // value is given, both axes use it. Per-axis longhands
        // (`row-gap` / `column-gap`) win when present.
        var gapRaw = style.GetPropertyValue("gap");
        double rowGap = 0, columnGap = 0;
        if (!string.IsNullOrEmpty(gapRaw))
        {
            var parts = gapRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rowGap = Length.Parse(parts[0]).Resolve(containerWidth, fontSize, rootFontSize);
            columnGap = parts.Length > 1
                ? Length.Parse(parts[1]).Resolve(containerWidth, fontSize, rootFontSize)
                : rowGap;
        }
        var rowG = Length.Parse(style.GetPropertyValue("row-gap"));
        if (!rowG.IsNone) rowGap = rowG.Resolve(containerWidth, fontSize, rootFontSize);
        var colG = Length.Parse(style.GetPropertyValue("column-gap"));
        if (!colG.IsNone) columnGap = colG.Resolve(containerWidth, fontSize, rootFontSize);
        return (rowGap, columnGap);
    }

    /// <summary>Extend a row template to cover at least
    /// <paramref name="rowsNeeded"/> rows, repeating the last
    /// declared row size as the implicit-track size. Real CSS
    /// uses <c>grid-auto-rows</c> to control this; we use
    /// "last declared track" as a sensible fallback.</summary>
    private static List<TrackSize> ExpandRowTemplate(List<TrackSize> rows, int rowsNeeded)
    {
        if (rows.Count >= rowsNeeded) return rows;
        var result = new List<TrackSize>(rowsNeeded);
        result.AddRange(rows);
        var fill = rows.Count > 0 ? rows[^1] : TrackSize.Auto;
        while (result.Count < rowsNeeded) result.Add(fill);
        return result;
    }

    private static double SumPrefix(List<double> sizes, int upTo)
    {
        double sum = 0;
        for (int i = 0; i < upTo && i < sizes.Count; i++) sum += sizes[i];
        return sum;
    }
}

/// <summary>One track in a grid template.
/// <see cref="Kind"/> selects the interpretation:
/// <c>Length</c> uses the <see cref="Length"/> field;
/// <c>Fr</c> uses the <see cref="Value"/> field as the
/// flex factor; <c>Auto</c> means content-sized (treated as
/// 0 in v1).</summary>
internal readonly struct TrackSize
{
    public TrackKind Kind { get; }
    public Length Length { get; }
    public double Value { get; }

    public TrackSize(TrackKind kind, Length length, double value)
    {
        Kind = kind;
        Length = length;
        Value = value;
    }

    public static readonly TrackSize Auto = new(TrackKind.Auto, Length.Auto, 0);
    public static TrackSize Fr(double v) => new(TrackKind.Fr, Length.None, v);
}

internal enum TrackKind { Length, Fr, Auto }
