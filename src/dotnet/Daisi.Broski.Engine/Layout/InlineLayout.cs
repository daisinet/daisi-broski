using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Paint;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Phase-6i inline-flow layout: when a block container's
/// element children are all inline-display, lay them out
/// horizontally with line wrapping at the container's
/// content edge instead of stacking each one as a full-
/// width block.
///
/// <para>
/// Each inline child gets a shrink-to-fit width:
/// <list type="bullet">
/// <item>If <c>width</c> is declared, that wins.</item>
/// <item>Otherwise, sum of the element's descendant text
///   widths (using <see cref="BitmapFont.CellWidth"/>) plus
///   the element's own left/right padding + border + margin.</item>
/// </list>
/// </para>
///
/// <para>
/// Deliberately deferred:
/// <list type="bullet">
/// <item>Mixed inline + block siblings (the spec's
///   anonymous-block wrapping rules). Today's check is
///   "all element children inline" → inline flow,
///   otherwise block flow.</item>
/// <item>Vertical alignment within a line — items align to
///   the top edge of the line box, no baseline math.</item>
/// <item>Splitting an inline element across lines mid-text
///   — each inline child stays atomic; if it doesn't fit
///   on the current line it moves to a fresh one.</item>
/// </list>
/// </para>
/// </summary>
internal static class InlineLayout
{
    /// <summary>True when every element child of
    /// <paramref name="element"/> resolves to an inline-
    /// flavored display, making the container eligible for
    /// inline-flow layout. Empty containers (no element
    /// children) return false so block flow handles them.</summary>
    public static bool ShouldUseInlineFlow(Element element, LayoutStyleResolver resolver)
    {
        bool sawElement = false;
        foreach (var c in element.ChildNodes)
        {
            if (c is not Element childEl) continue;
            sawElement = true;
            var style = resolver.Resolve(childEl);
            var display = style.GetPropertyValue("display");
            // Explicit display wins. Empty display falls back
            // to the tag-default list — span / a / em / strong
            // / etc. are inline; div / section / etc. are
            // block.
            if (display == "inline" || display == "inline-block") continue;
            if (string.IsNullOrEmpty(display) && IsDefaultInline(childEl.TagName)) continue;
            return false;
        }
        return sawElement;
    }

    public static void LayoutChildren(
        LayoutBox container, Element element,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize)
    {
        double availWidth = container.Width;
        if (availWidth < BitmapFont.CellWidth) return;

        double cursorX = 0;
        double cursorY = 0;
        double lineHeight = BitmapFont.LineHeight;

        foreach (var child in element.ChildNodes)
        {
            if (child is not Element childEl) continue;
            var prepared = LayoutTree.PrepareBox(container, childEl, resolver, viewport);
            if (prepared is null) continue;
            var (box, itemFs, itemRs, itemDeclaredHeight) = prepared.Value;

            // Override width: shrink-to-fit unless the
            // cascade declared one. PrepareBox set
            // box.Width = parent.Width when auto, which is
            // wrong for inline; recompute.
            var childStyle = resolver.Resolve(childEl);
            var declaredWidth = Length.Parse(childStyle.GetPropertyValue("width"));
            double width = !declaredWidth.IsNone && !declaredWidth.IsAuto
                ? declaredWidth.Resolve(availWidth, itemFs, itemRs)
                : MeasureIntrinsicWidth(childEl, BitmapFont.CellWidth);
            // Cap width to the line so it never overflows;
            // text inside will wrap separately when painted.
            width = Math.Min(width, availWidth
                - box.Margin.Left - box.Margin.Right
                - box.Padding.Left - box.Padding.Right
                - box.Border.Left - box.Border.Right);
            if (width < 0) width = 0;
            box.Width = width;

            // Outer width drives line packing.
            double outer = LayoutTree.OuterWidth(box);

            // Wrap to a fresh line if this item won't fit
            // alongside whatever is already on the current
            // line. The cursor tracks position relative to
            // the container's content origin (0,0).
            if (cursorX > 0 && cursorX + outer > availWidth)
            {
                cursorX = 0;
                cursorY += lineHeight;
            }

            container.Children.Add(box);
            box.X = container.X + cursorX
                + box.Margin.Left + box.Border.Left + box.Padding.Left;
            box.Y = container.Y + cursorY
                + box.Margin.Top + box.Border.Top + box.Padding.Top;

            // Lay out the child's own descendants. They may
            // themselves be inline (nested span inside a),
            // in which case we recurse via the dispatch in
            // LayChildrenAndResolveHeight.
            LayoutTree.LayChildrenAndResolveHeight(
                box, childEl, resolver, viewport,
                itemFs, itemRs, itemDeclaredHeight, container.Height);

            // Use the natural line height when the child's
            // own height didn't grow (no nested children,
            // just text painted at render time).
            if (box.Height < BitmapFont.GlyphHeight && box.Display != BoxDisplay.Block)
            {
                // Direct text content of this inline child
                // wraps to as many lines as needed; estimate
                // from the text it owns. The painter does the
                // actual word-wrap so this is just a height
                // hint.
                int charCount = CountDirectTextChars(childEl);
                int charsPerLine = Math.Max(1, (int)(box.Width / BitmapFont.CellWidth));
                int lines = Math.Max(1, (int)Math.Ceiling(charCount / (double)charsPerLine));
                box.Height = lines * BitmapFont.LineHeight;
            }

            cursorX += outer;
        }

        // Container's content height: the cursorY plus the
        // last line's height. If nothing was placed,
        // collapse to zero.
        if (container.Children.Count > 0)
        {
            container.Height = Math.Max(container.Height, cursorY + lineHeight);
        }
    }

    /// <summary>Walk the element + its descendants, summing
    /// the pixel width of every <see cref="Text"/> node's
    /// data (whitespace-collapsed). Includes left/right
    /// padding + border + margin contributed by descendant
    /// elements so a button with `padding: 8px 16px` shrinks
    /// to its label width plus 32px.</summary>
    private static double MeasureIntrinsicWidth(Element element, int cellWidth)
    {
        double total = 0;
        foreach (var child in element.ChildNodes)
        {
            switch (child)
            {
                case Text t:
                    {
                        var s = t.Data;
                        // Collapse whitespace runs so the
                        // measure matches what the painter
                        // will actually draw.
                        int chars = 0;
                        bool prevWasWs = false;
                        foreach (var c in s)
                        {
                            bool ws = char.IsWhiteSpace(c);
                            if (ws && prevWasWs) continue;
                            chars++;
                            prevWasWs = ws;
                        }
                        total += chars * cellWidth;
                        break;
                    }
                case Element e:
                    total += MeasureIntrinsicWidth(e, cellWidth);
                    break;
            }
        }
        return total;
    }

    private static int CountDirectTextChars(Element element)
    {
        int count = 0;
        foreach (var child in element.ChildNodes)
        {
            if (child is Text t)
            {
                count += t.Data.Length;
            }
        }
        return count;
    }

    /// <summary>Mirrors the inline default-display tag list
    /// in <see cref="LayoutTree.ParseDisplay"/>. Kept as a
    /// duplicate so InlineLayout can run without first
    /// resolving every child's full computed style.</summary>
    private static bool IsDefaultInline(string tagName) => tagName switch
    {
        "html" or "body" or "div" or "section" or "article"
            or "header" or "footer" or "nav" or "main"
            or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "ul" or "ol" or "li" or "blockquote" or "pre"
            => false,
        "head" or "script" or "style" or "link" or "meta" or "title"
            => false,
        _ => true,
    };
}
