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
        // Container-level font metrics — used for shrink-to-
        // fit width measurement and as the minimum line
        // advance when no child forces something taller.
        int containerScale = BitmapFont.ScaleFor(fontSize);
        int containerCellW = BitmapFont.CellWidth * containerScale;
        double containerLineH = ResolveCssLineHeight(
            resolver.Resolve(element), fontSize,
            BitmapFont.GlyphHeight * containerScale);

        double availWidth = container.Width;
        if (availWidth < containerCellW) return;

        double cursorX = 0;
        double cursorY = 0;
        double lineHeight = containerLineH;
        int layoutStartIndex = container.Children.Count;

        var containerStyle = resolver.Resolve(element);
        foreach (var child in element.ChildNodes)
        {
            // Anonymous text runs — a Text node mixed in with
            // inline elements. We split the fragment at word
            // boundaries and emit one layout box per line
            // (greedy word wrap) so long paragraphs break
            // properly instead of extending past the container
            // edge as a single atomic run.
            if (child is Daisi.Broski.Engine.Dom.Text tn)
            {
                var normalized = NormalizeTextFragment(tn.Data);
                if (normalized.Length == 0) continue;
                // Measure with the actual font that the
                // painter will use so layout's wrap decisions
                // match what ends up in pixels. Without this
                // the layout sees bitmap-width estimates
                // (6 × scale per char) but the painter emits
                // real font advance widths — narrow words
                // like "it" overestimate, wide words like
                // "Intelligence." underestimate, and either
                // way the line break falls in the wrong spot.
                var webFont = container.Element?.OwnerDocument is { } doc
                    ? ResolveWebFontForMeasure(doc, containerStyle, normalized)
                    : null;
                EmitWrappedTextRuns(container, normalized, containerStyle,
                    containerCellW, containerLineH, availWidth,
                    webFont, fontSize,
                    ref cursorX, ref cursorY, ref lineHeight);
                continue;
            }
            if (child is not Element childEl) continue;

            // <br> forces a line break — advance the cursor
            // to the start of a new line. Skipping the
            // element-box path entirely (no PrepareBox /
            // positioning / recursion) because br has no
            // visible content, just a layout effect.
            // Without this, consecutive br tags stack as
            // zero-width inlines on the same line and the
            // text they're meant to separate collides.
            if (childEl.TagName == "br")
            {
                cursorX = 0;
                cursorY += lineHeight;
                continue;
            }

            var prepared = LayoutTree.PrepareBox(container, childEl, resolver, viewport);
            if (prepared is null) continue;
            var (box, itemFs, itemRs, itemDeclaredHeight) = prepared.Value;

            var childStyle = resolver.Resolve(childEl);
            int childScale = BitmapFont.ScaleFor(itemFs);
            int childCellW = BitmapFont.CellWidth * childScale;
            int childGlyphH = BitmapFont.GlyphHeight * childScale;
            double childLineH = ResolveCssLineHeight(childStyle, itemFs, childGlyphH);
            // img / svg / input carry their own intrinsic
            // sizing via HTML attributes or the decoded image's
            // natural dimensions — PrepareBox already resolved
            // those. The generic inline shrink-to-fit path
            // would count child text characters (0 for a void
            // element) and overwrite the good width with zero,
            // so logos vanish. Keep PrepareBox's answer for
            // these tags.
            bool keepPreparedSize = childEl.TagName is "img" or "svg" or "input";
            if (!keepPreparedSize)
            {
                // Override width: shrink-to-fit unless the
                // cascade declared one. PrepareBox set
                // box.Width = parent.Width when auto, which is
                // wrong for inline; recompute.
                var declaredWidth = Length.Parse(childStyle.GetPropertyValue("width"));
                double width = !declaredWidth.IsNone && !declaredWidth.IsAuto
                    ? declaredWidth.Resolve(availWidth, itemFs, itemRs)
                    : MeasureIntrinsicWidth(childEl, childCellW);
                // Cap width to the line so it never overflows;
                // text inside will wrap separately when painted.
                width = Math.Min(width, availWidth
                    - box.Margin.Left - box.Margin.Right
                    - box.Padding.Left - box.Padding.Right
                    - box.Border.Left - box.Border.Right);
                if (width < 0) width = 0;
                box.Width = width;
            }

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
            // just text painted at render time). img / svg
            // / input keep the height PrepareBox resolved
            // from their attrs — same reason we kept the
            // width above.
            if (!keepPreparedSize
                && box.Height < childGlyphH
                && box.Display != BoxDisplay.Block)
            {
                // Direct text content of this inline child
                // wraps to as many lines as needed; estimate
                // from the text it owns. The painter does the
                // actual word-wrap so this is just a height
                // hint.
                int charCount = CountDirectTextChars(childEl);
                int charsPerLine = Math.Max(1, (int)(box.Width / childCellW));
                int lines = Math.Max(1, (int)Math.Ceiling(charCount / (double)charsPerLine));
                box.Height = lines * childLineH;
            }

            // Grow the current line to fit this child's
            // outer height — a 48px heading on the same line
            // as a 16px span stretches the line to the heading.
            double childOuter = LayoutTree.OuterHeight(box);
            if (childOuter > lineHeight) lineHeight = childOuter;

            cursorX += outer;
        }

        // Container's content height: the cursorY plus the
        // last line's height. If nothing was placed,
        // collapse to zero.
        if (container.Children.Count > 0)
        {
            container.Height = Math.Max(container.Height, cursorY + lineHeight);
        }

        // Apply text-align across whole lines: group inline
        // children by their Y bucket, compute the line's
        // width, shift every child on the line by the align
        // offset. Per-run shifting (done earlier) only worked
        // for single-run lines; mixed inline content
        // (h2>Alpha<span>Phase</span>) needs per-line grouping
        // so the whole line pair centers together.
        ApplyTextAlignToChildren(container, containerStyle, layoutStartIndex);
    }

    private static void ApplyTextAlignToChildren(
        LayoutBox container, ComputedStyle style, int startIndex)
    {
        var align = (style.GetPropertyValue("text-align") ?? "").Trim().ToLowerInvariant();
        if (align != "center" && align != "right" && align != "end") return;
        int count = container.Children.Count;
        if (count <= startIndex) return;

        // Group children by their Y coord (rounded to integer
        // so float noise doesn't split adjacent runs). For
        // each group, find minX + maxX and shift each child
        // by the slack-based offset.
        var byLine = new Dictionary<int, List<LayoutBox>>();
        for (int i = startIndex; i < count; i++)
        {
            var ch = container.Children[i];
            int key = (int)Math.Round(ch.Y);
            if (!byLine.TryGetValue(key, out var list))
            {
                list = new List<LayoutBox>();
                byLine[key] = list;
            }
            list.Add(ch);
        }
        double contentWidth = container.Width;
        foreach (var list in byLine.Values)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            foreach (var ch in list)
            {
                if (ch.X < minX) minX = ch.X;
                double right = ch.X + ch.Width;
                if (right > maxX) maxX = right;
            }
            double lineW = maxX - minX;
            // Undo the old cursor-relative offset (minX is
            // container.X for left-aligned); the shift here
            // produces the final alignment.
            double containerLeft = container.X;
            double slack = contentWidth - lineW;
            if (slack <= 0) continue;
            double shift = align switch
            {
                "center" => slack / 2,
                _ => slack,
            };
            // Remove any per-run offset I applied in CommitRun —
            // subtract its effect by resetting all boxes to
            // start from containerLeft then adding the group
            // shift. We detect the per-run shift by comparing
            // minX vs containerLeft. If minX > containerLeft
            // the per-run shift is already baked in, so we
            // only apply the difference.
            double existingShift = minX - containerLeft;
            double delta = shift - existingShift;
            if (Math.Abs(delta) < 0.5) continue;
            foreach (var ch in list)
            {
                ch.X += delta;
                ShiftDescendants(ch, delta, 0);
            }
        }
    }

    private static void ShiftDescendants(LayoutBox box, double dx, double dy)
    {
        foreach (var child in box.Children)
        {
            child.X += dx;
            child.Y += dy;
            ShiftDescendants(child, dx, dy);
        }
    }

    /// <summary>Greedy word-wrap a text fragment into one or
    /// more anonymous text-run layout boxes. The cursor is
    /// advanced as if each line were placed inline; lines
    /// after the first sit at cursorX=0 of the next line
    /// (just like a real inline text run that overflows).
    /// Measures word widths via the resolved web font when
    /// one is available so the wrap matches what the painter
    /// will actually draw.</summary>
    private static void EmitWrappedTextRuns(
        LayoutBox container, string text, ComputedStyle containerStyle,
        int cellWidth, double lineHeight, double availWidth,
        Daisi.Broski.Engine.Fonts.TtfReader? font, double fontSize,
        ref double cursorX, ref double cursorY, ref double lineHeightVar)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        double spaceW = font is not null
            ? Daisi.Broski.Engine.Fonts.GlyphRasterizer.MeasureText(font, " ", fontSize)
            : cellWidth;
        if (spaceW <= 0) spaceW = cellWidth;

        double Measure(string w) => font is not null
            ? Daisi.Broski.Engine.Fonts.GlyphRasterizer.MeasureText(font, w, fontSize)
            : w.Length * cellWidth;

        var current = new System.Text.StringBuilder();
        double currentW = 0;

        foreach (var word in words)
        {
            double wordW = Measure(word);
            // Starting a fresh line? Emit the word even if it
            // overflows — hard-break within a word would need
            // character-level slicing.
            if (current.Length == 0 && cursorX == 0)
            {
                current.Append(word);
                currentW = wordW;
                continue;
            }
            // Would appending this word (preceded by a space)
            // overflow the container's content width?
            double needed = currentW + spaceW + wordW;
            if (cursorX + needed <= availWidth)
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                    currentW += spaceW;
                }
                current.Append(word);
                currentW += wordW;
            }
            else
            {
                // Commit the current run and wrap to a new line.
                CommitRun(container, current, currentW, containerStyle,
                    lineHeight, ref cursorX, ref cursorY);
                current.Clear();
                currentW = 0;
                cursorX = 0;
                cursorY += lineHeight;
                current.Append(word);
                currentW = wordW;
            }
        }
        CommitRun(container, current, currentW, containerStyle,
            lineHeight, ref cursorX, ref cursorY);
        if (lineHeight > lineHeightVar) lineHeightVar = lineHeight;
    }

    private static void CommitRun(
        LayoutBox container, System.Text.StringBuilder current, double currentW,
        ComputedStyle containerStyle, double lineHeight,
        ref double cursorX, ref double cursorY)
    {
        if (current.Length == 0) return;
        var runText = current.ToString();
        var tb = new LayoutBox
        {
            TextRun = runText,
            InheritedStyle = containerStyle,
            Display = BoxDisplay.Inline,
            Width = currentW,
            Height = lineHeight,
        };
        container.Children.Add(tb);
        tb.X = container.X + cursorX;
        tb.Y = container.Y + cursorY;
        cursorX += currentW;
    }

    /// <summary>Resolve the web font InlineLayout should
    /// measure with. Picks the font that covers the first
    /// char of the text so Latin / non-Latin fonts don't get
    /// mixed up.</summary>
    private static Daisi.Broski.Engine.Fonts.TtfReader? ResolveWebFontForMeasure(
        Daisi.Broski.Engine.Dom.Document doc, ComputedStyle style, string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var family = style.GetPropertyValue("font-family");
        if (string.IsNullOrWhiteSpace(family)) return null;
        int weight = 400;
        var wRaw = style.GetPropertyValue("font-weight");
        if (!string.IsNullOrWhiteSpace(wRaw))
        {
            var t = wRaw.Trim().ToLowerInvariant();
            weight = t switch
            {
                "bold" => 700, "lighter" => 300, "bolder" => 700, "normal" => 400,
                _ => int.TryParse(t, out var n) ? n : 400,
            };
        }
        var sKw = style.GetPropertyValue("font-style");
        if (string.IsNullOrEmpty(sKw)) sKw = "normal";
        return Daisi.Broski.Engine.Fonts.FontResolver.Resolve(
            doc, family, weight, sKw, text[0]);
    }

    private static string NormalizeTextFragment(string data)
    {
        // Collapse runs of whitespace to a single space, per
        // CSS `white-space: normal`. Empty-after-collapse
        // fragments are dropped so they don't leave a bare
        // space between two inline elements that were already
        // positioned flush.
        var sb = new System.Text.StringBuilder(data.Length);
        bool lastWs = true;
        foreach (var c in data)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWs) sb.Append(' ');
                lastWs = true;
            }
            else
            {
                sb.Append(c);
                lastWs = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static double MeasureTextFragment(string text, int cellWidth) =>
        text.Length * cellWidth;

    /// <summary>Resolve the computed <c>line-height</c> for
    /// an element: unit-less multipliers of font-size,
    /// percentages, pixel lengths, and the default "normal"
    /// keyword (≈1.2). Falls back to <paramref name="glyphHeight"/>
    /// so lines are at least the bitmap font's cell height.</summary>
    private static double ResolveCssLineHeight(ComputedStyle style, double fontSize, int glyphHeight)
    {
        var raw = style.GetPropertyValue("line-height");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var trimmed = raw.Trim();
            if (!trimmed.EndsWith('%')
                && double.TryParse(trimmed,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var mult))
            {
                return fontSize * mult;
            }
            if (trimmed.EndsWith('%') && double.TryParse(
                trimmed.AsSpan(0, trimmed.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pct))
            {
                return fontSize * pct / 100.0;
            }
            var len = Length.Parse(trimmed);
            if (!len.IsNone && !len.IsAuto)
            {
                return len.Resolve(fontSize, fontSize, 16, fontSize * 1.2);
            }
        }
        return Math.Max(glyphHeight, fontSize * 1.2);
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
