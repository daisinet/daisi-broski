using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Layout;
using Daisi.Broski.Engine.Paint.Svg;

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

        // CSS 2.1 §E paint-order summary — within each
        // stacking context we paint in this order:
        //   1. Block-level, non-positioned descendants.
        //   2. Positioned descendants (absolute / fixed / relative
        //      with z-index), in tree order.
        //   3. z-index > 0 descendants, sorted ascending.
        // We implement pass (1) as a tree walk that skips
        // positioned boxes, then pass (2) as a second walk that
        // paints only the positioned subtrees collected in a
        // list — in order, after all in-flow content. Without
        // this, a fixed-position header gets painted first and
        // later in-flow siblings (a hero section with a solid
        // background) overwrite the header's content.
        var positioned = new List<LayoutBox>();
        PaintBox(root, document, viewport, buffer, wireframe, positioned);
        foreach (var p in positioned)
        {
            // Paint the positioned subtree fully — descendants
            // of a positioned box paint in their own stacking
            // context, so we walk them normally from here.
            PaintBox(p, document, viewport, buffer, wireframe, positioned: null);
        }
        return buffer;
    }

    /// <summary>True when the box's element is out-of-flow
    /// (<c>position: absolute</c> or <c>fixed</c>). Those
    /// paint in a deferred pass after in-flow content so
    /// fixed headers and absolute overlays sit on top of
    /// their content siblings rather than underneath — the
    /// CSS painting-order spec's separation of in-flow vs
    /// positioned descendants.</summary>
    private static bool IsOutOfFlow(LayoutBox box, Viewport viewport)
    {
        if (box.Element is null) return false;
        var style = StyleResolver.Resolve(box.Element, viewport);
        var pos = style.GetPropertyValue("position");
        return pos is "absolute" or "fixed";
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
        RasterBuffer buffer, bool wireframe,
        List<LayoutBox>? positioned)
    {
        // If we're in the in-flow pass (positioned != null)
        // and this box is out-of-flow, defer the whole subtree
        // to pass 2 so it paints on top of in-flow siblings.
        // In pass 2 (positioned == null) we paint normally.
        if (positioned is not null && IsOutOfFlow(box, viewport))
        {
            positioned.Add(box);
            return;
        }

        // The root box has no Element (it wraps the
        // viewport). Paint its descendants but skip its own
        // background — we already filled the canvas.
        // Anonymous text-run boxes emitted by InlineLayout
        // for Text nodes inside mixed-content parents. These
        // carry inherited style + a literal string and paint
        // only the text — no background, borders, recursion.
        if (box.TextRun is not null)
        {
            PaintAnonymousTextRun(box, document, buffer);
            return;
        }

        if (box.Element is not null)
        {
            var style = StyleResolver.Resolve(box.Element, viewport);
            double opacity = ResolveOpacity(style);
            PaintBackground(box, style, buffer, opacity);
            PaintImage(box, document, buffer, opacity);
            PaintBorders(box, style, buffer, opacity);
            // Skip direct-text painting when the element has
            // any element children — InlineLayout has already
            // emitted anonymous text-run boxes at the correct
            // positions. Still paint direct text when there
            // are no element children (the "pure text-content"
            // case hasn't been routed through inline flow).
            bool hasElementChild = HasAnyElementChild(box.Element);
            if (!hasElementChild)
            {
                PaintTextContent(box, style, buffer, opacity);
            }
            if (wireframe) PaintWireframe(box, buffer);

            // Inline <svg> owns its subtree — rasterize its
            // child shapes directly and skip the normal
            // descendant walk (SVG children don't have their
            // own layout boxes in this engine).
            if (box.Element.TagName == "svg")
            {
                PaintSvg(box, buffer);
                return;
            }
        }
        foreach (var child in box.Children)
        {
            PaintBox(child, document, viewport, buffer, wireframe, positioned);
        }
    }

    private static bool HasAnyElementChild(Element element)
    {
        foreach (var c in element.ChildNodes)
        {
            if (c is Element) return true;
        }
        return false;
    }

    /// <summary>Paint an anonymous text run generated by
    /// <see cref="InlineLayout"/> for a Text node inside a
    /// mixed inline+text parent. Uses the inherited style to
    /// resolve font-size / color / line-height so the run
    /// matches what the surrounding element would render.</summary>
    private static void PaintAnonymousTextRun(
        LayoutBox box, Document document, RasterBuffer buffer)
    {
        if (string.IsNullOrEmpty(box.TextRun)) return;
        var style = box.InheritedStyle;
        if (style is null) return;
        var color = CssColor.Parse(style.GetPropertyValue("color"));
        if (color.IsTransparent) color = PaintColor.Black;
        double fontSize = ResolveFontSizePx(style);

        // Same dispatch as PaintTextContent — prefer web font
        // when available, fall back to the bitmap. Pass the
        // first char as a coverage hint so the resolver can
        // skip subset files that don't cover Latin.
        int sampleCharText = box.TextRun!.Length > 0 ? box.TextRun[0] : 'A';
        var webFont = ResolveWebFont(document, style, sampleCharText);
        if (webFont is not null)
        {
            int lineStep = ResolveLineHeightPx(style, fontSize, (int)Math.Round(fontSize));
            double baseline = box.Y + lineStep * 0.8;
            Daisi.Broski.Engine.Fonts.GlyphRasterizer.DrawText(
                buffer, webFont, box.X, baseline, box.TextRun, fontSize, color);
            return;
        }

        int scale = BitmapFont.ScaleFor(fontSize);
        BitmapFont.DrawText(buffer,
            (int)Math.Round(box.X), (int)Math.Round(box.Y),
            box.TextRun, color, scale);
    }

    /// <summary>Rasterize an inline <c>&lt;svg&gt;</c>
    /// element into the paint buffer. The target rect is the
    /// SVG box's content area; <see cref="SvgRenderer"/> maps
    /// the viewBox (or natural coords) onto it and fills each
    /// child shape.</summary>
    private static void PaintSvg(LayoutBox box, RasterBuffer buffer)
    {
        if (box.Element is null) return;
        int x = (int)Math.Round(box.X);
        int y = (int)Math.Round(box.Y);
        int w = (int)Math.Round(box.Width);
        int h = (int)Math.Round(box.Height);
        if (w <= 0 || h <= 0) return;
        SvgRenderer.Render(box.Element, x, y, w, h, buffer);
    }

    /// <summary>If this element directly contains text node
    /// children (no element children — leaf-ish content), word-
    /// wrap and render them via <see cref="BitmapFont"/>. We
    /// only render direct text content to avoid double-painting
    /// nested elements; an inline-flow algorithm that splits
    /// text around inline children would be a much bigger
    /// slice.</summary>
    private static void PaintTextContent(
        LayoutBox box, ComputedStyle style, RasterBuffer buffer, double opacity)
    {
        if (box.Element is null) return;
        var color = CssColor.Parse(style.GetPropertyValue("color"));
        if (color.IsTransparent) color = PaintColor.Black;
        color = ApplyAlpha(color, opacity);

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

        // Resolve CSS font-size and line-height so the bitmap
        // font scales up for headings and tight/loose line
        // spacing actually applies. Without this every chunk
        // of text renders at the same 7px glyph regardless of
        // `font-size: 48px` or similar.
        double fontSize = ResolveFontSizePx(style);
        int lineStep;


        // Prefer a loaded web-font when the element's
        // font-family resolves to one the document fetched.
        // Falls through to the bitmap font when no match or
        // the font can't be parsed. Pass the first char as a
        // unicode-range hint so Google-Fonts-style split
        // subsets resolve to the right Latin file.
        int sampleChar = text.Length > 0 ? text[0] : 'A';
        var webFont = ResolveWebFont(box.Element.OwnerDocument, style, sampleChar);
        if (webFont is not null)
        {
            lineStep = ResolveLineHeightPx(style, fontSize, (int)Math.Round(fontSize));
            int contentWidth = (int)Math.Round(box.Width);
            if (contentWidth < 1) return;
            var linesVector = WrapTextByMeasure(text, contentWidth, webFont, fontSize);
            double xBase = box.X;
            double yBase = box.Y;
            double maxY = box.Y + box.Height;
            for (int i = 0; i < linesVector.Count; i++)
            {
                // Baseline sits ~80% of the line height down
                // from the line top — a reasonable default
                // when the font's ascender metric isn't read.
                double y = yBase + i * lineStep + lineStep * 0.8;
                if (y + fontSize * 0.25 > maxY && box.Height > 0) break;
                Daisi.Broski.Engine.Fonts.GlyphRasterizer.DrawText(
                    buffer, webFont, xBase, y, linesVector[i], fontSize, color);
            }
            return;
        }

        int scale = BitmapFont.ScaleFor(fontSize);
        int glyphCellW = BitmapFont.CellWidth * scale;
        int glyphH = BitmapFont.GlyphHeight * scale;
        lineStep = ResolveLineHeightPx(style, fontSize, glyphH);

        int contentWidthBmp = (int)Math.Round(box.Width);
        if (contentWidthBmp < glyphCellW) return;
        int maxCharsPerLine = contentWidthBmp / glyphCellW;
        if (maxCharsPerLine <= 0) return;

        var lines = WrapText(text, maxCharsPerLine);
        int x = (int)Math.Round(box.X);
        int yBaseInt = (int)Math.Round(box.Y);
        int maxYInt = (int)Math.Round(box.Y + box.Height);

        for (int i = 0; i < lines.Count; i++)
        {
            int y = yBaseInt + i * lineStep;
            if (y + glyphH > maxYInt && box.Height > 0) break;
            BitmapFont.DrawText(buffer, x, y, lines[i], color, scale);
        }
    }

    private static Daisi.Broski.Engine.Fonts.TtfReader? ResolveWebFont(
        Document? doc, ComputedStyle? style, int sampleChar = 'A')
    {
        if (doc is null || style is null || doc.Fonts.Count == 0) return null;
        var family = style.GetPropertyValue("font-family");
        if (string.IsNullOrWhiteSpace(family)) return null;
        int weight = ParseWeight(style.GetPropertyValue("font-weight"));
        var styleKw = style.GetPropertyValue("font-style");
        if (string.IsNullOrEmpty(styleKw)) styleKw = "normal";
        return Daisi.Broski.Engine.Fonts.FontResolver.Resolve(
            doc, family, weight, styleKw, sampleChar);
    }

    private static int ParseWeight(string? value)
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

    /// <summary>Greedy word-wrap that measures each word's
    /// pixel width via the font's advance-width table. Slower
    /// than the bitmap-font char-count wrap but matches what
    /// the rasterizer will actually emit. Hard-breaks words
    /// longer than the line (same behavior as WrapText).</summary>
    private static List<string> WrapTextByMeasure(
        string text, int maxPixels, Daisi.Broski.Engine.Fonts.TtfReader font, double pixelSize)
    {
        var lines = new List<string>();
        if (maxPixels <= 0) return lines;
        var current = new System.Text.StringBuilder();
        double currentW = 0;
        double spaceW = Daisi.Broski.Engine.Fonts.GlyphRasterizer.MeasureText(font, " ", pixelSize);
        foreach (var word in text.Split(' '))
        {
            double wordW = Daisi.Broski.Engine.Fonts.GlyphRasterizer.MeasureText(font, word, pixelSize);
            if (current.Length == 0)
            {
                current.Append(word);
                currentW = wordW;
                continue;
            }
            if (currentW + spaceW + wordW <= maxPixels)
            {
                current.Append(' ').Append(word);
                currentW += spaceW + wordW;
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
                currentW = wordW;
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>Resolve the computed <c>font-size</c> to
    /// pixels. Inheritance has already happened in the
    /// cascade; a missing value falls back to 16px (the
    /// root default).</summary>
    private static double ResolveFontSizePx(ComputedStyle style)
    {
        var raw = Daisi.Broski.Engine.Layout.Length.Parse(style.GetPropertyValue("font-size"));
        if (raw.IsNone || raw.IsAuto) return 16;
        return raw.Resolve(containingSize: 16, fontSize: 16, rootFontSize: 16, fallback: 16);
    }

    /// <summary>Compute the per-line advance in pixels given
    /// the computed <c>line-height</c> + <c>font-size</c>.
    /// Honors unit-less multipliers (<c>line-height: 1.5</c>),
    /// px lengths, percentages, and the default "normal"
    /// keyword (≈1.2em). Falls back to the glyph height when
    /// the value is missing or unparseable.</summary>
    private static int ResolveLineHeightPx(ComputedStyle style, double fontSize, int glyphHeight)
    {
        var raw = style.GetPropertyValue("line-height");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var trimmed = raw.Trim();
            // Unit-less number → multiplier of font-size.
            if (double.TryParse(trimmed,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var mult) && !trimmed.EndsWith('%'))
            {
                return (int)Math.Round(fontSize * mult);
            }
            if (trimmed.EndsWith('%') && double.TryParse(
                trimmed.AsSpan(0, trimmed.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pct))
            {
                return (int)Math.Round(fontSize * pct / 100.0);
            }
            var len = Daisi.Broski.Engine.Layout.Length.Parse(trimmed);
            if (!len.IsNone && !len.IsAuto)
            {
                return (int)Math.Round(len.Resolve(
                    containingSize: fontSize, fontSize: fontSize,
                    rootFontSize: 16, fallback: fontSize * 1.2));
            }
        }
        // CSS default line-height is "normal" (~1.2) — falls
        // through to this when the cascade didn't set it.
        double normal = fontSize * 1.2;
        return Math.Max(glyphHeight, (int)Math.Round(normal));
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

    private static void PaintBackground(
        LayoutBox box, ComputedStyle style, RasterBuffer buffer, double opacity)
    {
        var rect = box.BorderBoxRect;
        int rx = (int)Math.Round(rect.X);
        int ry = (int)Math.Round(rect.Y);
        int rw = (int)Math.Round(rect.Width);
        int rh = (int)Math.Round(rect.Height);


        // Try background-image first (linear-gradient,
        // url(...), etc.) — when present it stacks over the
        // background-color, but for v1 we treat them as
        // alternatives: gradient OR solid, never both.
        var bgImage = style.GetPropertyValue("background-image");
        var bgShorthand = style.GetPropertyValue("background");
        var gradientSrc = !string.IsNullOrEmpty(bgImage) ? bgImage : bgShorthand;
        if (Gradient.IsLinearGradient(gradientSrc))
        {
            var gradient = Gradient.TryParseLinear(gradientSrc);
            if (gradient is not null)
            {
                // Minimum-viable opacity pass for gradients:
                // when the element has opacity < 1, pre-scale
                // every stop's alpha. Not quite spec-correct
                // (real CSS opacity is a group composite) but
                // avoids painting a full-alpha gradient over
                // the existing canvas.
                var paintedGrad = opacity < 1 ? gradient with
                {
                    Stops = gradient.Stops
                        .Select(s => new GradientStop(ApplyAlpha(s.Color, opacity), s.Position))
                        .ToList()
                } : gradient;
                Gradient.Paint(buffer, rx, ry, rw, rh, paintedGrad);
                return;
            }
        }

        // Fall through to flat color: longhand first, then
        // first color-shaped token in the shorthand.
        var color = CssColor.Parse(style.GetPropertyValue("background-color"));
        if (color.IsTransparent)
        {
            color = ExtractColorFromShorthand(bgShorthand);
        }
        if (color.IsTransparent) return;
        buffer.FillRect(rx, ry, rw, rh, ApplyAlpha(color, opacity));
    }

    /// <summary>Resolve the computed <c>opacity</c> to a
    /// number in [0, 1]. Missing or unparseable value returns
    /// 1 (fully opaque), matching the CSS initial value.</summary>
    private static double ResolveOpacity(ComputedStyle style)
    {
        var raw = style.GetPropertyValue("opacity");
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        if (!double.TryParse(raw.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) return 1;
        return Math.Clamp(v, 0, 1);
    }

    /// <summary>Multiply a color's alpha channel by
    /// <paramref name="opacity"/>. Pre-multiplies at paint time
    /// so the existing FillRect blend path handles the rest.</summary>
    private static PaintColor ApplyAlpha(PaintColor c, double opacity)
    {
        if (opacity >= 1) return c;
        if (opacity <= 0) return PaintColor.Transparent;
        return new PaintColor(c.R, c.G, c.B, (byte)Math.Clamp(c.A * opacity, 0, 255));
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

    /// <summary>If the element is an <c>&lt;img&gt;</c> and
    /// the page loader successfully decoded its src, blit
    /// the decoded pixels into the box's content area.
    /// Scales to the box dimensions via nearest-neighbor —
    /// a real browser would do bilinear or better, but
    /// nearest is fast and visually fine for screenshot-
    /// scale images on a non-fonts-aware engine.</summary>
    private static void PaintImage(
        LayoutBox box, Document document, RasterBuffer buffer, double opacity)
    {
        if (box.Element is null) return;
        if (box.Element.TagName != "img") return;
        if (document.Images is null) return;
        if (!document.Images.TryGetValue(box.Element, out var raw)) return;

        int dstX = (int)Math.Round(box.X);
        int dstY = (int)Math.Round(box.Y);
        int dstW = (int)Math.Round(box.Width);
        int dstH = (int)Math.Round(box.Height);

        // SVG <img src="*.svg">: rasterize on demand at the
        // box's computed size via the same renderer inline
        // <svg> uses. No cached raster — the SVG re-rasterizes
        // each paint, which is fine because screenshots are
        // one-shot.
        if (raw is Element svgRoot && svgRoot.TagName == "svg")
        {
            if (dstW <= 0 || dstH <= 0) return;
            SvgRenderer.Render(svgRoot, dstX, dstY, dstW, dstH, buffer);
            return;
        }

        if (raw is not RasterBuffer src) return;
        if (dstW <= 0 || dstH <= 0)
        {
            // Layout didn't size the image — fall back to
            // the source's natural dimensions.
            dstW = src.Width;
            dstH = src.Height;
        }
        BlitNearestNeighbor(src, buffer, dstX, dstY, dstW, dstH);
    }

    /// <summary>Copy pixels from <paramref name="src"/> into
    /// <paramref name="dst"/> at <paramref name="dstX"/> /
    /// <paramref name="dstY"/>, scaled to <paramref name="dstW"/>
    /// × <paramref name="dstH"/> using nearest-neighbor
    /// sampling. Pixels outside the destination buffer are
    /// clipped. Source alpha is honored via simple
    /// over-blending.</summary>
    private static void BlitNearestNeighbor(
        RasterBuffer src, RasterBuffer dst, int dstX, int dstY, int dstW, int dstH)
    {
        if (dstW <= 0 || dstH <= 0) return;
        int x0 = Math.Max(0, dstX);
        int y0 = Math.Max(0, dstY);
        int x1 = Math.Min(dst.Width, dstX + dstW);
        int y1 = Math.Min(dst.Height, dstY + dstH);
        if (x0 >= x1 || y0 >= y1) return;

        for (int dy = y0; dy < y1; dy++)
        {
            int sy = (int)((dy - dstY) * (long)src.Height / dstH);
            if (sy < 0 || sy >= src.Height) continue;
            for (int dx = x0; dx < x1; dx++)
            {
                int sx = (int)((dx - dstX) * (long)src.Width / dstW);
                if (sx < 0 || sx >= src.Width) continue;
                int srcIdx = (sy * src.Width + sx) * 4;
                int dstIdx = (dy * dst.Width + dx) * 4;
                byte sR = src.Pixels[srcIdx];
                byte sG = src.Pixels[srcIdx + 1];
                byte sB = src.Pixels[srcIdx + 2];
                byte sA = src.Pixels[srcIdx + 3];
                if (sA == 0) continue;
                if (sA == 255)
                {
                    dst.Pixels[dstIdx] = sR;
                    dst.Pixels[dstIdx + 1] = sG;
                    dst.Pixels[dstIdx + 2] = sB;
                    dst.Pixels[dstIdx + 3] = 255;
                }
                else
                {
                    double a = sA / 255.0;
                    double oneMinus = 1.0 - a;
                    dst.Pixels[dstIdx] = (byte)(sR * a + dst.Pixels[dstIdx] * oneMinus);
                    dst.Pixels[dstIdx + 1] = (byte)(sG * a + dst.Pixels[dstIdx + 1] * oneMinus);
                    dst.Pixels[dstIdx + 2] = (byte)(sB * a + dst.Pixels[dstIdx + 2] * oneMinus);
                    dst.Pixels[dstIdx + 3] = (byte)Math.Min(255, dst.Pixels[dstIdx + 3] + sA * oneMinus);
                }
            }
        }
    }

    private static void PaintBorders(
        LayoutBox box, ComputedStyle style, RasterBuffer buffer, double opacity)
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

        if (opacity < 1)
        {
            topColor = ApplyAlpha(topColor, opacity);
            rightColor = ApplyAlpha(rightColor, opacity);
            bottomColor = ApplyAlpha(bottomColor, opacity);
            leftColor = ApplyAlpha(leftColor, opacity);
        }

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
