using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Phase-6d single-line flex layout. Implements the most-used
/// subset of CSS Flexible Box Layout — enough for the
/// "navbar with flex-1 spacer" / "horizontally centered
/// content" / "stretch siblings to equal width" patterns
/// real pages depend on. Multi-line wrap, per-item
/// <c>align-self</c>, <c>order</c>, and the
/// min/max-content sizing pipeline are deliberately deferred.
///
/// <para>
/// Algorithm:
/// <list type="number">
/// <item>Read the container's <c>flex-direction</c> /
///   <c>justify-content</c> / <c>align-items</c> / <c>gap</c>.</item>
/// <item>Build a <see cref="LayoutBox"/> per non-display-none
///   child via <see cref="LayoutTree.PrepareBox"/> — gives
///   us margin / padding / border / declared width-or-height
///   without positioning.</item>
/// <item>Compute each item's main-size hypothesis from
///   <c>flex-basis</c> (or the declared main-axis length, or
///   0 if both are auto).</item>
/// <item>Sum hypotheses + gaps; compare to container main
///   size; distribute free space proportional to
///   <c>flex-grow</c> (positive free space) or shrink
///   weights (negative).</item>
/// <item>Position items along the main axis per
///   <c>justify-content</c>.</item>
/// <item>For each item, recurse to lay out its own
///   children, then size the cross axis (declared or
///   stretched to match the container's cross size when
///   <c>align-items: stretch</c>).</item>
/// <item>Position items along the cross axis per
///   <c>align-items</c>.</item>
/// </list>
/// </para>
/// </summary>
internal static class FlexLayout
{
    public static void LayoutChildren(
        LayoutBox container, Element element, ComputedStyle style,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize)
    {
        var direction = ParseDirection(style.GetPropertyValue("flex-direction"));
        var justify = ParseJustify(style.GetPropertyValue("justify-content"));
        var alignItems = ParseAlign(style.GetPropertyValue("align-items"));
        double gap = Length.Parse(style.GetPropertyValue("gap"))
            .Resolve(container.Width, fontSize, rootFontSize);
        bool isRow = direction is FlexDirection.Row or FlexDirection.RowReverse;
        bool reverse = direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

        // 1) Build child items without positioning.
        var items = new List<FlexItem>();
        foreach (var child in element.ChildNodes)
        {
            if (child is not Element childEl) continue;
            var prepared = LayoutTree.PrepareBox(container, childEl, resolver, viewport);
            if (prepared is null) continue;
            var (box, itemFontSize, itemRootFs, itemDeclaredHeight) = prepared.Value;

            // Lay out the child's own descendants now so its
            // intrinsic content height is known. Width is
            // already set by PrepareBox; for column flex it
            // may shrink/grow later, but row direction keeps
            // the resolved width.
            container.Children.Add(box);
            LayoutTree.LayChildrenAndResolveHeight(
                box, childEl, resolver, viewport,
                itemFontSize, itemRootFs, itemDeclaredHeight, container.Height);

            var itemStyle = resolver.Resolve(childEl);

            // CSS expands `flex: <grow> <shrink>? <basis>?` into
            // the three longhands per Cascade. Our cascade
            // doesn't expand shorthands, so do it here as a
            // pre-step: read the longhands first, fall back to
            // the shorthand-derived values.
            var (shorthandGrow, shorthandShrink, shorthandBasis) =
                ParseFlexShorthand(itemStyle.GetPropertyValue("flex"));

            double grow = ParseDouble(itemStyle.GetPropertyValue("flex-grow"),
                shorthandGrow);
            double shrink = ParseDouble(itemStyle.GetPropertyValue("flex-shrink"),
                shorthandShrink);
            var basisLen = Length.Parse(itemStyle.GetPropertyValue("flex-basis"));
            if (basisLen.IsNone) basisLen = shorthandBasis;
            double basis = ResolveBasis(basisLen, isRow, box, container, itemFontSize, itemRootFs, itemStyle);

            items.Add(new FlexItem
            {
                Box = box,
                Grow = grow,
                Shrink = shrink,
                Basis = basis,
                MainSize = basis,
            });
        }

        if (items.Count == 0) return;

        double containerMain = isRow ? container.Width : container.Height;
        double containerCross = isRow ? container.Height : container.Width;

        // 2) Distribute free space along main axis.
        double totalGap = gap * (items.Count - 1);
        double usedMain = totalGap;
        foreach (var it in items) usedMain += it.MainSize + OuterMainExtras(it.Box, isRow);

        double free = containerMain - usedMain;
        if (free > 0)
        {
            double totalGrow = items.Sum(i => i.Grow);
            if (totalGrow > 0)
            {
                foreach (var it in items)
                {
                    if (it.Grow > 0) it.MainSize += free * (it.Grow / totalGrow);
                }
            }
        }
        else if (free < 0)
        {
            double weighted = items.Sum(i => i.Shrink * i.Basis);
            if (weighted > 0)
            {
                foreach (var it in items)
                {
                    if (it.Shrink > 0)
                    {
                        double share = (it.Shrink * it.Basis) / weighted;
                        it.MainSize = Math.Max(0, it.MainSize + free * share);
                    }
                }
            }
        }

        // Apply the resolved main size back onto each box.
        foreach (var it in items)
        {
            if (isRow) it.Box.Width = it.MainSize;
            else it.Box.Height = it.MainSize;
        }

        // 3) Cross-axis sizing: align-items: stretch enlarges
        // each item's cross dimension to fill the container
        // (when the item didn't declare a cross size). Other
        // align modes leave the item's intrinsic size alone.
        if (alignItems == FlexAlign.Stretch)
        {
            foreach (var it in items)
            {
                if (isRow)
                {
                    if (it.Box.Height == 0)
                    {
                        it.Box.Height = Math.Max(0, containerCross
                            - it.Box.Margin.Top - it.Box.Margin.Bottom
                            - it.Box.Padding.Top - it.Box.Padding.Bottom
                            - it.Box.Border.Top - it.Box.Border.Bottom);
                    }
                }
                else
                {
                    if (it.Box.Width == 0)
                    {
                        it.Box.Width = Math.Max(0, containerCross
                            - it.Box.Margin.Left - it.Box.Margin.Right
                            - it.Box.Padding.Left - it.Box.Padding.Right
                            - it.Box.Border.Left - it.Box.Border.Right);
                    }
                }
            }
        }

        // 4) Position items along the main axis. Reverse
        // direction reverses the visual order AND treats the
        // far edge as the start (justify-content: flex-start
        // packs items from the right in row-reverse, etc.).
        // We model that by reversing the items list and then
        // flipping start vs. end so the cursor starts from
        // the far side and walks back.
        if (reverse) items.Reverse();
        double startMain = 0;
        double mainBetween = gap;
        double remaining = containerMain - SumOuterMain(items, isRow) - totalGap;
        var effectiveJustify = reverse
            ? justify switch
            {
                FlexJustify.Start => FlexJustify.End,
                FlexJustify.End => FlexJustify.Start,
                _ => justify,
            }
            : justify;
        switch (effectiveJustify)
        {
            case FlexJustify.End:
                startMain = remaining;
                break;
            case FlexJustify.Center:
                startMain = remaining / 2;
                break;
            case FlexJustify.SpaceBetween:
                if (items.Count > 1) mainBetween = gap + remaining / (items.Count - 1);
                break;
            case FlexJustify.SpaceAround:
                {
                    double slot = remaining / items.Count;
                    startMain = slot / 2;
                    mainBetween = gap + slot;
                    break;
                }
            case FlexJustify.SpaceEvenly:
                {
                    double slot = remaining / (items.Count + 1);
                    startMain = slot;
                    mainBetween = gap + slot;
                    break;
                }
        }

        double cursor = startMain;
        foreach (var it in items)
        {
            double mainPos = isRow
                ? container.X + cursor + it.Box.Margin.Left + it.Box.Border.Left + it.Box.Padding.Left
                : container.Y + cursor + it.Box.Margin.Top + it.Box.Border.Top + it.Box.Padding.Top;
            if (isRow) it.Box.X = mainPos;
            else it.Box.Y = mainPos;
            cursor += OuterMainOf(it.Box, isRow) + mainBetween;
        }

        // 5) Position items along the cross axis.
        foreach (var it in items)
        {
            double itemCross = OuterCrossOf(it.Box, isRow);
            double crossOffset = alignItems switch
            {
                FlexAlign.Center => (containerCross - itemCross) / 2,
                FlexAlign.End => containerCross - itemCross,
                _ => 0,
            };
            if (isRow)
            {
                it.Box.Y = container.Y + crossOffset
                    + it.Box.Margin.Top + it.Box.Border.Top + it.Box.Padding.Top;
            }
            else
            {
                it.Box.X = container.X + crossOffset
                    + it.Box.Margin.Left + it.Box.Border.Left + it.Box.Padding.Left;
            }
        }

        // 6) Container's own auto-height (in row mode) or
        // auto-width (column) — fits the tallest / widest
        // item in the cross direction.
        double maxCross = 0;
        foreach (var it in items)
        {
            maxCross = Math.Max(maxCross, OuterCrossOf(it.Box, isRow));
        }
        if (isRow)
        {
            container.Height = Math.Max(container.Height, maxCross);
        }
        else
        {
            container.Width = Math.Max(container.Width, maxCross);
        }

        // 7) Translate each item's descendants to follow the
        // item's final position. Descendants were laid out in
        // step 1 against a zero-origin parent because flex
        // positioning hadn't happened yet — a nested block
        // layout that does `box.X = parent.X + margin` ended
        // up anchoring everything at absolute (0, 0).
        // Without this shift every grandchild would be painted
        // at the top-left of the document regardless of which
        // flex item contains it.
        foreach (var it in items)
        {
            double dx = it.Box.X;
            double dy = it.Box.Y;
            if (dx != 0 || dy != 0)
            {
                TranslateDescendants(it.Box, dx, dy);
            }
        }
    }

    /// <summary>Recursively shift every descendant of
    /// <paramref name="box"/> by the given delta. Leaves the
    /// box itself unchanged — its own position was already set
    /// by the caller.</summary>
    private static void TranslateDescendants(LayoutBox box, double dx, double dy)
    {
        foreach (var child in box.Children)
        {
            child.X += dx;
            child.Y += dy;
            TranslateDescendants(child, dx, dy);
        }
    }

    private static double SumOuterMain(List<FlexItem> items, bool isRow) =>
        items.Sum(i => OuterMainOf(i.Box, isRow));

    private static double OuterMainOf(LayoutBox box, bool isRow) =>
        isRow ? LayoutTree.OuterWidth(box) : LayoutTree.OuterHeight(box);

    private static double OuterCrossOf(LayoutBox box, bool isRow) =>
        isRow ? LayoutTree.OuterHeight(box) : LayoutTree.OuterWidth(box);

    private static double OuterMainExtras(LayoutBox box, bool isRow) =>
        isRow
            ? box.Margin.Left + box.Margin.Right
              + box.Padding.Left + box.Padding.Right
              + box.Border.Left + box.Border.Right
            : box.Margin.Top + box.Margin.Bottom
              + box.Padding.Top + box.Padding.Bottom
              + box.Border.Top + box.Border.Bottom;

    /// <summary>Resolve the spec's <c>flex-basis</c> per-item:
    /// an explicit length wins; <c>auto</c> / <c>none</c> falls
    /// back to the item's declared main-axis length, then to 0
    /// (the spec's "max-content size of an empty box"). Without
    /// this fallback, <c>flex: auto</c> items would use the
    /// block-layout fill-parent width as their basis and any
    /// flex-grow distribution would collapse to even shrinking.</summary>
    private static double ResolveBasis(
        Length basis, bool isRow, LayoutBox box, LayoutBox container,
        double fontSize, double rootFontSize, ComputedStyle itemStyle)
    {
        if (!basis.IsNone && !basis.IsAuto)
        {
            return basis.Resolve(isRow ? container.Width : container.Height,
                fontSize, rootFontSize);
        }
        // Look at the declared (pre-resolution) main-axis size.
        var declared = Length.Parse(itemStyle.GetPropertyValue(isRow ? "width" : "height"));
        if (!declared.IsNone && !declared.IsAuto)
        {
            return declared.Resolve(isRow ? container.Width : container.Height,
                fontSize, rootFontSize);
        }
        return 0;
    }

    private static double ParseDouble(string s, double fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>Pull (grow, shrink, basis) out of the
    /// <c>flex</c> shorthand. Recognized forms:
    /// <list type="bullet">
    /// <item><c>none</c> → <c>0 0 auto</c>.</item>
    /// <item><c>auto</c> → <c>1 1 auto</c>.</item>
    /// <item><c>initial</c> → <c>0 1 auto</c>.</item>
    /// <item>One number (<c>1</c>) → <c>1 1 0%</c>.</item>
    /// <item>One length (<c>200px</c>) → <c>1 1 200px</c>.</item>
    /// <item>Two numbers (<c>2 1</c>) → <c>2 1 0%</c>.</item>
    /// <item>Number + length (<c>1 200px</c>) → <c>1 1 200px</c>.</item>
    /// <item>Three values → as-given.</item>
    /// </list>
    /// Returns spec-default values <c>(0, 1, none)</c> when the
    /// shorthand is empty so the longhand reads can win.</summary>
    internal static (double Grow, double Shrink, Length Basis) ParseFlexShorthand(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (0, 1, Length.None);
        }
        var trimmed = value.Trim();
        switch (trimmed)
        {
            case "none": return (0, 0, Length.Auto);
            case "auto": return (1, 1, Length.Auto);
            case "initial": return (0, 1, Length.Auto);
        }
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double grow = 0, shrink = 1;
        Length basis = Length.Px(0);
        int seenNumbers = 0;
        bool seenBasis = false;
        foreach (var t in tokens)
        {
            if (TryParseUnitless(t, out var n))
            {
                if (seenNumbers == 0) grow = n;
                else if (seenNumbers == 1) shrink = n;
                seenNumbers++;
            }
            else
            {
                basis = Length.Parse(t);
                seenBasis = true;
            }
        }
        if (!seenBasis && seenNumbers > 0) basis = Length.Px(0);
        return (grow, shrink, basis);
    }

    private static bool TryParseUnitless(string s, out double value)
    {
        // A bare number (no unit suffix) — used by grow/shrink.
        // We can't just check IsDigit because "1.5" needs the
        // dot. The simplest discriminator: parses as double AND
        // doesn't end in a letter or '%'.
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        char last = s[^1];
        if (char.IsLetter(last) || last == '%') return false;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static FlexDirection ParseDirection(string s) => s switch
    {
        "column" => FlexDirection.Column,
        "row-reverse" => FlexDirection.RowReverse,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Row,
    };

    private static FlexJustify ParseJustify(string s) => s switch
    {
        "flex-end" or "end" or "right" => FlexJustify.End,
        "center" => FlexJustify.Center,
        "space-between" => FlexJustify.SpaceBetween,
        "space-around" => FlexJustify.SpaceAround,
        "space-evenly" => FlexJustify.SpaceEvenly,
        _ => FlexJustify.Start,
    };

    private static FlexAlign ParseAlign(string s) => s switch
    {
        "flex-end" or "end" => FlexAlign.End,
        "center" => FlexAlign.Center,
        "stretch" => FlexAlign.Stretch,
        "" => FlexAlign.Stretch, // CSS default
        _ => FlexAlign.Start,
    };

    private sealed class FlexItem
    {
        public LayoutBox Box = null!;
        public double Grow;
        public double Shrink;
        public double Basis;
        public double MainSize;
    }
}

internal enum FlexDirection { Row, RowReverse, Column, ColumnReverse }
internal enum FlexJustify { Start, End, Center, SpaceBetween, SpaceAround, SpaceEvenly }
internal enum FlexAlign { Start, End, Center, Stretch }
