using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Layout;

namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Walks a layout tree, paints each box's background +
/// borders into a <see cref="RasterBuffer"/>. Skips text
/// rendering (fonts deferred), gradients, shadows,
/// transforms, opacity, border-radius. Good enough for
/// "what does the layout look like" screenshots and pages
/// dominated by colored regions.
///
/// <para>
/// Paint order follows CSS painting order (CSS 2.1 §E):
/// <list type="number">
/// <item>Background of the box being painted.</item>
/// <item>Borders.</item>
/// <item>Descendants, in tree order, recursing.</item>
/// </list>
/// Z-index ordering is deferred — stacking contexts paint
/// in document order.
/// </para>
/// </summary>
public static class Painter
{
    /// <summary>Walk <paramref name="root"/> and emit a
    /// pixel buffer sized to the viewport. The root box's
    /// canvas defaults to white when no explicit body /
    /// html background-color is set, matching what every
    /// browser does. When <paramref name="wireframe"/> is
    /// true, every layout box is outlined in a light gray
    /// 1px border — useful when text rendering is deferred
    /// and you want to see the structural layout regardless
    /// of whether author CSS set a background.</summary>
    public static RasterBuffer Paint(LayoutBox root, Document document, Viewport viewport,
        bool wireframe = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        var buffer = new RasterBuffer(viewport.Width, viewport.Height,
            ResolveCanvasBackground(document, viewport));
        PaintBox(root, document, viewport, buffer, wireframe);
        return buffer;
    }

    private static PaintColor ResolveCanvasBackground(Document document, Viewport viewport)
    {
        // Per CSS 2.1 §14.2: if html has no background, use
        // body's; if neither sets one, default white. We
        // resolve through the cascade so user-agent defaults
        // (transparent) and author rules combine naturally.
        var html = document.DocumentElement;
        if (html is null) return PaintColor.White;
        var htmlStyle = StyleResolver.Resolve(html, viewport);
        var htmlBg = CssColor.Parse(htmlStyle.GetPropertyValue("background-color"));
        if (!htmlBg.IsTransparent) return htmlBg;
        var body = document.Body;
        if (body is null) return PaintColor.White;
        var bodyStyle = StyleResolver.Resolve(body, viewport);
        var bodyBg = CssColor.Parse(bodyStyle.GetPropertyValue("background-color"));
        return bodyBg.IsTransparent ? PaintColor.White : bodyBg;
    }

    private static void PaintBox(
        LayoutBox box, Document document, Viewport viewport,
        RasterBuffer buffer, bool wireframe)
    {
        // The root box has no Element (it wraps the
        // viewport). Paint its descendants but skip its own
        // background — we already filled the canvas.
        if (box.Element is not null)
        {
            var style = StyleResolver.Resolve(box.Element, viewport);
            PaintBackground(box, style, buffer);
            PaintBorders(box, style, buffer);
            PaintTextContent(box, style, buffer);
            if (wireframe) PaintWireframe(box, buffer);
        }
        foreach (var child in box.Children)
        {
            PaintBox(child, document, viewport, buffer, wireframe);
        }
    }

    /// <summary>If this element directly contains text node
    /// children (no element children — leaf-ish content), word-
    /// wrap and render them via <see cref="BitmapFont"/>. We
    /// only render direct text content to avoid double-painting
    /// nested elements; an inline-flow algorithm that splits
    /// text around inline children would be a much bigger
    /// slice.</summary>
    private static void PaintTextContent(LayoutBox box, ComputedStyle style, RasterBuffer buffer)
    {
        if (box.Element is null) return;
        var color = CssColor.Parse(style.GetPropertyValue("color"));
        if (color.IsTransparent) color = PaintColor.Black;

        // Concatenate direct Text children only — ignores
        // child elements (their text is painted when we
        // recurse into them). Newlines collapse to spaces
        // since white-space defaults to `normal`.
        string text = "";
        foreach (var child in box.Element.ChildNodes)
        {
            if (child is Daisi.Broski.Engine.Dom.Text t)
            {
                text += t.Data;
            }
        }
        text = NormalizeWhitespace(text);
        if (text.Length == 0) return;

        // Word-wrap to the box content width. Each line is
        // BitmapFont.LineHeight tall. Lines that don't fit
        // vertically are dropped (we don't grow the box at
        // paint time).
        int contentWidth = (int)Math.Round(box.Width);
        if (contentWidth < BitmapFont.CellWidth) return;
        int maxCharsPerLine = contentWidth / BitmapFont.CellWidth;
        if (maxCharsPerLine <= 0) return;

        var lines = WrapText(text, maxCharsPerLine);
        int x = (int)Math.Round(box.X);
        int yBase = (int)Math.Round(box.Y);
        int maxY = (int)Math.Round(box.Y + box.Height);

        for (int i = 0; i < lines.Count; i++)
        {
            int y = yBase + i * BitmapFont.LineHeight;
            if (y + BitmapFont.GlyphHeight > maxY && box.Height > 0) break;
            BitmapFont.DrawText(buffer, x, y, lines[i], color);
        }
    }

    /// <summary>Collapse runs of whitespace to a single space
    /// and trim. Matches CSS <c>white-space: normal</c>
    /// behavior — the default for every element type that
    /// contains text.</summary>
    private static string NormalizeWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = true;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Greedy word-wrap: walk the text accumulating
    /// words separated by spaces, break to a new line when
    /// adding the next word would exceed
    /// <paramref name="maxChars"/>. Words longer than the
    /// line are split mid-character (better than truncation
    /// when a single URL is wider than the box).</summary>
    private static List<string> WrapText(string text, int maxChars)
    {
        var lines = new List<string>();
        if (maxChars <= 0) return lines;
        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length == 0)
            {
                if (word.Length <= maxChars)
                {
                    current.Append(word);
                }
                else
                {
                    // Hard-break the over-long word.
                    int p = 0;
                    while (p + maxChars < word.Length)
                    {
                        lines.Add(word.Substring(p, maxChars));
                        p += maxChars;
                    }
                    current.Append(word.AsSpan(p));
                }
                continue;
            }
            // Adding a space + word — does it fit?
            if (current.Length + 1 + word.Length <= maxChars)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                if (word.Length <= maxChars)
                {
                    current.Append(word);
                }
                else
                {
                    int p = 0;
                    while (p + maxChars < word.Length)
                    {
                        lines.Add(word.Substring(p, maxChars));
                        p += maxChars;
                    }
                    current.Append(word.AsSpan(p));
                }
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>Draw a 1px translucent outline around the
    /// box's border rect. Lets layout structure show
    /// through in screenshots even when the page uses text
    /// for all its content and fonts aren't rendered.</summary>
    private static void PaintWireframe(LayoutBox box, RasterBuffer buffer)
    {
        var rect = box.BorderBoxRect;
        if (rect.Width < 1 || rect.Height < 1) return;
        var stroke = new PaintColor(80, 80, 80, 120);
        buffer.StrokeRect(
            (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
            1, 1, 1, 1,
            stroke, stroke, stroke, stroke);
    }

    private static void PaintBackground(LayoutBox box, ComputedStyle style, RasterBuffer buffer)
    {
        // Try the longhand first; if absent, scan the
        // `background` shorthand for a color token. Real
        // Bootstrap / Tailwind-emitted CSS often uses the
        // shorthand for `background: linear-gradient(...)` or
        // `background: rgba(...)` without the longhand
        // counterpart, so we'd miss those without this
        // fallback.
        var color = CssColor.Parse(style.GetPropertyValue("background-color"));
        if (color.IsTransparent)
        {
            color = ExtractColorFromShorthand(style.GetPropertyValue("background"));
        }
        if (color.IsTransparent) return;
        var rect = box.BorderBoxRect;
        buffer.FillRect(
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width),
            (int)Math.Round(rect.Height),
            color);
    }

    /// <summary>Pick the first color-shaped token out of a
    /// <c>background</c> shorthand. We don't render
    /// gradients yet (linear-gradient / radial-gradient),
    /// but a stop color inside the gradient is a sensible
    /// solid fallback — better than transparent. Skips the
    /// CSS function name if any so <c>linear-gradient(red, blue)</c>
    /// gives us <c>red</c>.</summary>
    private static PaintColor ExtractColorFromShorthand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return PaintColor.Transparent;
        // Strip leading function name (e.g. "linear-gradient")
        // and the surrounding parens so we can scan the inner
        // tokens.
        var working = value;
        int parenStart = working.IndexOf('(');
        if (parenStart > 0)
        {
            int parenEnd = working.LastIndexOf(')');
            if (parenEnd > parenStart)
            {
                working = working.Substring(parenStart + 1, parenEnd - parenStart - 1);
            }
        }
        // Tokens at top level — a comma split gets the
        // gradient stops; each stop's first token is the
        // color (or "to right" / "45deg" / etc., which we
        // skip).
        foreach (var stop in working.Split(','))
        {
            foreach (var token in stop.Trim().Split(' ',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var c = CssColor.Parse(token);
                if (!c.IsTransparent) return c;
            }
        }
        // Last resort: try the whole string (rgba(...) on
        // its own without a function-name wrapper).
        return CssColor.Parse(value);
    }

    private static void PaintBorders(LayoutBox box, ComputedStyle style, RasterBuffer buffer)
    {
        // Resolve per-side colors with the standard CSS
        // fallback chain: side longhand → border-color
        // shorthand → currentColor (which we substitute for
        // the element's color property).
        var fallback = ResolveBorderFallback(style);
        var topColor = CssColor.Parse(style.GetPropertyValue("border-top-color"));
        var rightColor = CssColor.Parse(style.GetPropertyValue("border-right-color"));
        var bottomColor = CssColor.Parse(style.GetPropertyValue("border-bottom-color"));
        var leftColor = CssColor.Parse(style.GetPropertyValue("border-left-color"));
        if (topColor.IsTransparent) topColor = fallback;
        if (rightColor.IsTransparent) rightColor = fallback;
        if (bottomColor.IsTransparent) bottomColor = fallback;
        if (leftColor.IsTransparent) leftColor = fallback;

        var rect = box.BorderBoxRect;
        buffer.StrokeRect(
            (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
            (int)Math.Round(box.Border.Top),
            (int)Math.Round(box.Border.Right),
            (int)Math.Round(box.Border.Bottom),
            (int)Math.Round(box.Border.Left),
            topColor, rightColor, bottomColor, leftColor);
    }

    /// <summary>Compute the fallback border color from the
    /// shorthand or from <c>currentColor</c>. The
    /// <c>border</c> shorthand can include a color token
    /// (<c>border: 1px solid red</c>); we look for one when
    /// present.</summary>
    private static PaintColor ResolveBorderFallback(ComputedStyle style)
    {
        // Try border-color (a 1-4 token shorthand of its own).
        var borderColorRaw = style.GetPropertyValue("border-color");
        if (!string.IsNullOrEmpty(borderColorRaw))
        {
            var firstToken = borderColorRaw.Split(' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (firstToken.Length > 0)
            {
                var c = CssColor.Parse(firstToken[0]);
                if (!c.IsTransparent) return c;
            }
        }
        // Try the `border` shorthand — last-resort scan for
        // a color-shaped token.
        var border = style.GetPropertyValue("border");
        if (!string.IsNullOrEmpty(border))
        {
            foreach (var token in border.Split(' ',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var c = CssColor.Parse(token);
                if (!c.IsTransparent) return c;
            }
        }
        // Final fallback: currentColor (the `color`
        // property).
        return CssColor.Parse(style.GetPropertyValue("color"));
    }
}
