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
        bool wrap = ParseFlexWrap(style.GetPropertyValue("flex-wrap"));
        double gap = Length.Parse(style.GetPropertyValue("gap"))
            .Resolve(container.Width, fontSize, rootFontSize);
        double rowGap = Length.Parse(style.GetPropertyValue("row-gap"))
            .Resolve(container.Height, fontSize, rootFontSize);
        if (rowGap <= 0) rowGap = gap;
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

            var itemStyle = resolver.Resolve(childEl);
            var position = itemStyle.GetPropertyValue("position");
            if (position is "absolute" or "fixed")
            {
                // Absolutely-positioned flex children are taken
                // out of flex flow and resolved against the
                // container the same way BuildAndLay handles
                // absolute block children. Hero overlays and
                // decoration spheres land in the right spot
                // this way, instead of being treated as a row
                // item that shoves siblings sideways.
                container.Children.Add(box);
                LayoutTree.ResolveAbsolutePositionInternal(
                    box, container, itemStyle,
                    itemFontSize, itemRootFs, viewport, position == "fixed");
                LayoutTree.LayChildrenAndResolveHeight(
                    box, childEl, resolver, viewport,
                    itemFontSize, itemRootFs, itemDeclaredHeight, container.Height);
                continue;
            }

            // Lay out the child's own descendants now so its
            // intrinsic content height is known. Width is
            // already set by PrepareBox; for column flex it
            // may shrink/grow later, but row direction keeps
            // the resolved width.
            container.Children.Add(box);
            LayoutTree.LayChildrenAndResolveHeight(
                box, childEl, resolver, viewport,
                itemFontSize, itemRootFs, itemDeclaredHeight, container.Height);

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
            double basis = ResolveBasis(
                basisLen, isRow, box, container, itemFontSize, itemRootFs,
                itemStyle, childEl, grow);

            items.Add(new FlexItem
            {
                Box = box,
                Element = childEl,
                FontSize = itemFontSize,
                RootFontSize = itemRootFs,
                DeclaredHeight = itemDeclaredHeight,
                OriginalMain = isRow ? box.Width : box.Height,
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
        // Descendants were laid out in step 1 with the box's
        // stale (parent-fill) width — if the flex algorithm
        // shrunk or grew the main dimension, the subtree has
        // to be rebuilt so internal percentage widths, inline
        // wrapping, and block-flow heights see the real width.
        // Without this, a flex-grow item that ended up smaller
        // than its parent ends up with descendants overflowing
        // its bounds (and a whole "Bootstrap .row wider than
        // its .container" chain of bugs on real pages).
        foreach (var it in items)
        {
            double newMain = it.MainSize;
            double oldMain = isRow ? it.Box.Width : it.Box.Height;
            if (isRow) it.Box.Width = newMain;
            else it.Box.Height = newMain;
            if (isRow && Math.Abs(newMain - it.OriginalMain) > 0.5)
            {
                it.Box.Children.Clear();
                it.Box.Height = 0;
                LayoutTree.LayChildrenAndResolveHeight(
                    it.Box, it.Element, resolver, viewport,
                    it.FontSize, it.RootFontSize, it.DeclaredHeight,
                    container.Height);
            }
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

        // Flex auto-margins: margin-left/right: auto on a
        // flex item absorbs free main-axis space per spec §8.1.
        // `<ul class="navbar-nav mx-auto">` is the canonical
        // "center me within the flex row" pattern; we need
        // both margins to be auto and at least one item to
        // have them.
        var autoStart = new bool[items.Count];
        var autoEnd = new bool[items.Count];
        int totalAutoMargins = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var itStyle = resolver.Resolve(items[i].Element);
            bool s = IsMarginAuto(itStyle, isRow, startSide: true);
            bool e = IsMarginAuto(itStyle, isRow, startSide: false);
            autoStart[i] = s;
            autoEnd[i] = e;
            if (s) totalAutoMargins++;
            if (e) totalAutoMargins++;
        }
        double perAutoMargin = 0;
        if (totalAutoMargins > 0 && remaining > 0)
        {
            perAutoMargin = remaining / totalAutoMargins;
            remaining = 0;
        }
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

        // With flex-wrap: wrap, items that would exceed the
        // container's main size move to a new line. We keep a
        // running cross-axis offset (startMainCross) that
        // advances by the tallest item on each line so the
        // next line sits below. Without wrap the code path
        // collapses to a single line and matches the prior
        // behavior.
        double cursor = startMain;
        double lineCrossOffset = 0;     // y (row) / x (column) top of current line
        double lineMaxCross = 0;        // max outer cross size in current line
        int lineStartIndex = 0;
        double[] itemCrossOffsets = new double[items.Count];
        int[] itemLineIndex = new int[items.Count];
        var lineCrossHeights = new List<double> { 0 };
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            double itemOuter = OuterMainOf(it.Box, isRow);
            double itemCross = OuterCrossOf(it.Box, isRow);

            // Wrap check: if this item won't fit on the
            // current line AND wrap is enabled AND the line
            // already has at least one item, start a new line.
            if (wrap && i > lineStartIndex
                && cursor + itemOuter > startMain + containerMain + 0.5)
            {
                // Finalize current line: advance cross offset.
                lineCrossHeights[^1] = lineMaxCross;
                lineCrossOffset += lineMaxCross + rowGap;
                lineMaxCross = 0;
                cursor = startMain;
                lineStartIndex = i;
                lineCrossHeights.Add(0);
            }

            if (autoStart[i]) cursor += perAutoMargin;
            double mainPos = isRow
                ? container.X + cursor + it.Box.Margin.Left + it.Box.Border.Left + it.Box.Padding.Left
                : container.Y + cursor + it.Box.Margin.Top + it.Box.Border.Top + it.Box.Padding.Top;
            if (isRow) it.Box.X = mainPos;
            else it.Box.Y = mainPos;
            itemCrossOffsets[i] = lineCrossOffset;
            itemLineIndex[i] = lineCrossHeights.Count - 1;
            cursor += itemOuter + mainBetween;
            if (autoEnd[i]) cursor += perAutoMargin;
            if (itemCross > lineMaxCross) lineMaxCross = itemCross;
        }
        // Last line's max.
        lineCrossHeights[^1] = lineMaxCross;

        // 5) Position items along the cross axis. With
        // flex-wrap, each line has its own effective cross
        // size and origin; single-line flex collapses to the
        // previous behavior (lineCrossHeights has 1 entry).
        // For single-line auto-cross containers (common for
        // Bootstrap .row with height:auto), we clamp to the
        // max outer-cross of all items so align-items:center
        // doesn't push items above the viewport.
        double singleLineEffectiveCross = containerCross;
        if (lineCrossHeights.Count == 1 && singleLineEffectiveCross <= 0)
        {
            singleLineEffectiveCross = lineCrossHeights[0];
        }
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            double itemCross = OuterCrossOf(it.Box, isRow);
            int lineIdx = itemLineIndex[i];
            double lineHeight = lineCrossHeights[lineIdx];
            double lineOrigin = itemCrossOffsets[i];
            double effCross = wrap ? lineHeight : singleLineEffectiveCross;
            double crossOffset = alignItems switch
            {
                FlexAlign.Center => (effCross - itemCross) / 2,
                FlexAlign.End => effCross - itemCross,
                _ => 0,
            };
            if (isRow)
            {
                it.Box.Y = container.Y + lineOrigin + crossOffset
                    + it.Box.Margin.Top + it.Box.Border.Top + it.Box.Padding.Top;
            }
            else
            {
                it.Box.X = container.X + lineOrigin + crossOffset
                    + it.Box.Margin.Left + it.Box.Border.Left + it.Box.Padding.Left;
            }
        }

        // 6) Container's own auto-height (row) or auto-width
        // (column). With wrap, that's the sum of all line
        // heights plus row-gaps; without wrap, just the max.
        double totalCross;
        if (wrap)
        {
            totalCross = 0;
            for (int li = 0; li < lineCrossHeights.Count; li++)
            {
                totalCross += lineCrossHeights[li];
                if (li > 0) totalCross += rowGap;
            }
        }
        else
        {
            totalCross = 0;
            foreach (var it in items)
            {
                totalCross = Math.Max(totalCross, OuterCrossOf(it.Box, isRow));
            }
        }
        if (isRow)
        {
            container.Height = Math.Max(container.Height, totalCross);
        }
        else
        {
            container.Width = Math.Max(container.Width, totalCross);
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
    /// <summary>Inspect the element's computed style for
    /// an auto value on the requested main-axis edge
    /// (<paramref name="startSide"/> = true → left for row /
    /// top for column; false → right / bottom). Reads the
    /// per-side longhand first, falling back to the
    /// <c>margin</c> shorthand tokens.</summary>
    private static bool IsMarginAuto(ComputedStyle style, bool isRow, bool startSide)
    {
        string prop = (isRow, startSide) switch
        {
            (true, true) => "margin-left",
            (true, false) => "margin-right",
            (false, true) => "margin-top",
            (false, false) => "margin-bottom",
        };
        var v = style.GetPropertyValue(prop);
        if (!string.IsNullOrWhiteSpace(v))
        {
            return v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        }
        // Fall back to the shorthand — 1-4 token list;
        // resolve which token applies to our side.
        var shorthand = style.GetPropertyValue("margin");
        if (string.IsNullOrWhiteSpace(shorthand)) return false;
        var tokens = shorthand.Trim().Split(' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        string pick = (isRow, startSide, tokens.Length) switch
        {
            (true, true, 1) => tokens[0],
            (true, true, 2) => tokens[1],
            (true, true, 3) => tokens[1],
            (true, true, _) => tokens[3],
            (true, false, 1) => tokens[0],
            (true, false, _) => tokens[1],
            (false, true, _) => tokens[0],
            (false, false, 1) => tokens[0],
            (false, false, 2) => tokens[0],
            (false, false, _) => tokens[2],
        };
        return pick.Equals("auto", StringComparison.OrdinalIgnoreCase);
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
    /// back to the item's declared main-axis length, then to
    /// the item's content size (approximated by the intrinsic
    /// text/child width we can measure). We used to return 0
    /// for the content-size case, which worked for
    /// <c>flex-grow: 1</c> items (they grow to fill) but
    /// collapsed every default <c>flex: 0 1 auto</c> item to
    /// zero — so navbar links, logos, and inline-block
    /// children of flex containers vanished on real pages.</summary>
    private static double ResolveBasis(
        Length basis, bool isRow, LayoutBox box, LayoutBox container,
        double fontSize, double rootFontSize, ComputedStyle itemStyle,
        Element itemElement, double grow)
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
        // Items explicitly asking to grow (e.g. `flex: 1`)
        // want a 0 basis so grow weights carve up the container
        // evenly — matching every flex-grow test we already
        // have. Non-growing items (the common default
        // `flex: 0 1 auto` nav links) need a real content
        // size so they don't collapse.
        if (grow > 0) return 0;

        if (isRow)
        {
            // Use intrinsic text/image width when the item has
            // laid-out content. Honor webfont metrics +
            // text-transform + bold-synthesis so the measured
            // size matches what the painter renders (prevents
            // buttons with 'text-uppercase' shrinking below
            // their actual rendered text width).
            int cellW = Daisi.Broski.Engine.Paint.BitmapFont.CellWidth
                * Daisi.Broski.Engine.Paint.BitmapFont.ScaleFor(fontSize);
            var doc = itemElement.OwnerDocument;
            var webFont = doc is not null
                ? Daisi.Broski.Engine.Fonts.FontResolver.Resolve(
                    doc, itemStyle.GetPropertyValue("font-family") ?? "",
                    ParseWeightValue(itemStyle.GetPropertyValue("font-weight")),
                    itemStyle.GetPropertyValue("font-style") ?? "normal", 'A')
                : null;
            var tt = (itemStyle.GetPropertyValue("text-transform") ?? "none")
                .Trim().ToLowerInvariant();
            bool bold = ParseWeightValue(itemStyle.GetPropertyValue("font-weight")) >= 600;
            double intrinsic = MeasureIntrinsicMainSize(
                itemElement, cellW, webFont, fontSize, tt, bold);
            if (intrinsic > 0) return Math.Min(intrinsic, container.Width);
        }
        return 0;
    }

    private static int ParseWeightValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 400;
        var t = value.Trim().ToLowerInvariant();
        return t switch
        {
            "normal" => 400,
            "bold" => 700,
            "lighter" => 300,
            "bolder" => 700,
            _ => int.TryParse(t, out var n) ? n : 400,
        };
    }

    /// <summary>Best-effort intrinsic width: sum of descendant
    /// text cell widths plus any explicit <c>width</c> on
    /// descendants. Good enough to keep "navbar with a few
    /// text links" from collapsing; doesn't need the full
    /// min/max-content machinery the spec describes.</summary>
    private static double MeasureIntrinsicMainSize(Element element, int cellWidth) =>
        MeasureIntrinsicMainSize(element, cellWidth, null, 0, "none", false);

    private static double MeasureIntrinsicMainSize(
        Element element, int cellWidth,
        Daisi.Broski.Engine.Fonts.TtfReader? font, double fontSize,
        string textTransform, bool bold)
    {
        double total = 0;
        foreach (var child in element.ChildNodes)
        {
            switch (child)
            {
                case Daisi.Broski.Engine.Dom.Text t:
                    {
                        var sb = new System.Text.StringBuilder();
                        bool prevWs = true;
                        foreach (var c in t.Data)
                        {
                            bool ws = char.IsWhiteSpace(c);
                            if (ws && prevWs) continue;
                            sb.Append(ws ? ' ' : c);
                            prevWs = ws;
                        }
                        var txt = sb.ToString();
                        txt = textTransform switch
                        {
                            "uppercase" => txt.ToUpperInvariant(),
                            "lowercase" => txt.ToLowerInvariant(),
                            _ => txt,
                        };
                        if (font is not null && fontSize > 0)
                        {
                            total += Daisi.Broski.Engine.Fonts.GlyphRasterizer
                                .MeasureText(font, txt, fontSize, bold);
                        }
                        else
                        {
                            total += txt.Length * cellWidth;
                        }
                        break;
                    }
                case Element e:
                    {
                        var w = e.GetAttribute("width");
                        if (!string.IsNullOrEmpty(w)
                            && double.TryParse(w,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var wp))
                        {
                            total += wp;
                        }
                        else
                        {
                            total += MeasureIntrinsicMainSize(
                                e, cellWidth, font, fontSize, textTransform, bold);
                        }
                        break;
                    }
            }
        }
        return total;
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

    /// <summary>True when <c>flex-wrap</c> allows items to
    /// wrap to multiple lines. <c>nowrap</c> (the default)
    /// means items overflow the container's main axis as a
    /// single line; <c>wrap</c> / <c>wrap-reverse</c> break
    /// to new lines when the main size is exceeded.</summary>
    private static bool ParseFlexWrap(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "wrap" or "wrap-reverse" => true,
        _ => false,
    };

    private sealed class FlexItem
    {
        public LayoutBox Box = null!;
        public Element Element = null!;
        public double FontSize;
        public double RootFontSize;
        public Length DeclaredHeight;
        /// <summary>Main-axis size assigned by PrepareBox before
        /// flex distribution. Tracked so step 2 can detect when
        /// the final resolved size differs and re-run the child
        /// layout with the correct parent width.</summary>
        public double OriginalMain;
        public double Grow;
        public double Shrink;
        public double Basis;
        public double MainSize;
    }
}

internal enum FlexDirection { Row, RowReverse, Column, ColumnReverse }
internal enum FlexJustify { Start, End, Center, SpaceBetween, SpaceAround, SpaceEvenly }
internal enum FlexAlign { Start, End, Center, Stretch }
