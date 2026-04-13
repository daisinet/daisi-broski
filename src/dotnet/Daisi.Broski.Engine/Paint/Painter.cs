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
    /// browser does.</summary>
    public static RasterBuffer Paint(LayoutBox root, Document document, Viewport viewport)
    {
        ArgumentNullException.ThrowIfNull(root);
        var buffer = new RasterBuffer(viewport.Width, viewport.Height,
            ResolveCanvasBackground(document, viewport));
        PaintBox(root, document, viewport, buffer);
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

    private static void PaintBox(LayoutBox box, Document document, Viewport viewport, RasterBuffer buffer)
    {
        // The root box has no Element (it wraps the
        // viewport). Paint its descendants but skip its own
        // background — we already filled the canvas.
        if (box.Element is not null)
        {
            var style = StyleResolver.Resolve(box.Element, viewport);
            PaintBackground(box, style, buffer);
            PaintBorders(box, style, buffer);
        }
        foreach (var child in box.Children)
        {
            PaintBox(child, document, viewport, buffer);
        }
    }

    private static void PaintBackground(LayoutBox box, ComputedStyle style, RasterBuffer buffer)
    {
        var color = CssColor.Parse(style.GetPropertyValue("background-color"));
        if (color.IsTransparent) return;
        var rect = box.BorderBoxRect;
        buffer.FillRect(
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width),
            (int)Math.Round(rect.Height),
            color);
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
