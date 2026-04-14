using System.Globalization;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Paint.Svg;

/// <summary>
/// Minimum-viable inline SVG rasterizer. Walks the <c>&lt;svg&gt;</c>
/// subtree, flattens each shape to a polygon, and fills it
/// via even-odd scanline fill. Honors <c>viewBox</c> scaling,
/// <c>fill</c> / <c>fill-opacity</c>, and <c>stroke</c> /
/// <c>stroke-width</c> (via thick line segments between polygon
/// vertices).
///
/// <para>
/// Intentionally skipped:
/// <list type="bullet">
/// <item><c>A</c> elliptical arc command in path data — rare
///   outside of circle/ellipse approximations we already cover.</item>
/// <item>Gradient / pattern fills — solid color only. A
///   linear-gradient <c>fill="url(#g)"</c> renders as
///   transparent (invisible) rather than a best-effort sample.</item>
/// <item>Filters, clipPath, mask, animation, text elements —
///   each is a sizable spec slice and uncommon in the hero
///   illustrations that motivate inline SVG support.</item>
/// <item>Fill-rule: nonzero is the SVG default but even-odd is
///   simpler and visually identical for non-self-intersecting
///   paths — which is most logos and icons.</item>
/// <item>Anti-aliasing: edges are snapped to pixel centers, so
///   curves sampled at low step counts look blocky. Good
///   enough for screenshots; a supersampler can layer on top
///   without touching the geometry code.</item>
/// </list>
/// </para>
/// </summary>
public static class SvgRenderer
{
    /// <summary>Render the <paramref name="svg"/> element into
    /// the target rectangle <c>(x, y, w, h)</c> of
    /// <paramref name="buffer"/>. The viewBox (or, absent one,
    /// the element's natural width/height) maps onto that
    /// rectangle via uniform scaling.</summary>
    public static void Render(
        Element svg, int x, int y, int w, int h, RasterBuffer buffer)
    {
        if (w <= 0 || h <= 0) return;
        var transform = ResolveTransform(svg, x, y, w, h);
        var ctx = new RenderCtx(buffer, transform);
        RenderSubtree(svg, ctx, new ShapeStyle());
    }

    private static void RenderSubtree(Element element, RenderCtx ctx, ShapeStyle inherited)
    {
        // Merge inherited style with any attributes declared on
        // this element so children pick up group-level fill /
        // stroke overrides. SVG's presentation attributes
        // cascade through <g> the same way CSS does through
        // regular DOM nodes.
        var style = MergeStyle(inherited, element);
        foreach (var child in element.Children)
        {
            switch (child.TagName)
            {
                case "g":
                case "svg": // nested <svg> — treat as group (no viewBox nesting)
                    RenderSubtree(child, ctx, style);
                    break;
                case "rect":
                    RenderRect(child, ctx, MergeStyle(style, child));
                    break;
                case "circle":
                    RenderCircle(child, ctx, MergeStyle(style, child));
                    break;
                case "ellipse":
                    RenderEllipse(child, ctx, MergeStyle(style, child));
                    break;
                case "line":
                    RenderLine(child, ctx, MergeStyle(style, child));
                    break;
                case "polygon":
                    RenderPolygon(child, ctx, MergeStyle(style, child), closed: true);
                    break;
                case "polyline":
                    RenderPolygon(child, ctx, MergeStyle(style, child), closed: false);
                    break;
                case "path":
                    RenderPath(child, ctx, MergeStyle(style, child));
                    break;
                // defs / title / desc / metadata — silently
                // skipped; their contents aren't rendered.
                case "defs":
                case "title":
                case "desc":
                case "metadata":
                case "style":
                    break;
                default:
                    // Unknown element — walk children in case
                    // there's renderable content nested inside
                    // (e.g. inside <symbol> or <a>).
                    RenderSubtree(child, ctx, style);
                    break;
            }
        }
    }

    private static void RenderRect(Element el, RenderCtx ctx, ShapeStyle style)
    {
        double rx = AttrDouble(el, "x", 0);
        double ry = AttrDouble(el, "y", 0);
        double rw = AttrDouble(el, "width", 0);
        double rh = AttrDouble(el, "height", 0);
        if (rw <= 0 || rh <= 0) return;
        var poly = new List<(double X, double Y)>
        {
            (rx, ry),
            (rx + rw, ry),
            (rx + rw, ry + rh),
            (rx, ry + rh),
            (rx, ry),
        };
        FillAndStroke(poly, ctx, style);
    }

    private static void RenderCircle(Element el, RenderCtx ctx, ShapeStyle style)
    {
        double cx = AttrDouble(el, "cx", 0);
        double cy = AttrDouble(el, "cy", 0);
        double r = AttrDouble(el, "r", 0);
        if (r <= 0) return;
        FillAndStroke(EllipseToPolygon(cx, cy, r, r), ctx, style);
    }

    private static void RenderEllipse(Element el, RenderCtx ctx, ShapeStyle style)
    {
        double cx = AttrDouble(el, "cx", 0);
        double cy = AttrDouble(el, "cy", 0);
        double rx = AttrDouble(el, "rx", 0);
        double ry = AttrDouble(el, "ry", 0);
        if (rx <= 0 || ry <= 0) return;
        FillAndStroke(EllipseToPolygon(cx, cy, rx, ry), ctx, style);
    }

    private static List<(double X, double Y)> EllipseToPolygon(
        double cx, double cy, double rx, double ry)
    {
        // 48 segments is a visually smooth circle at hero-graphic
        // sizes without being expensive to fill — doubling only
        // gains a pixel of fidelity at 200px diameters.
        const int steps = 48;
        var poly = new List<(double, double)>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = i * (2 * Math.PI / steps);
            poly.Add((cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return poly;
    }

    private static void RenderLine(Element el, RenderCtx ctx, ShapeStyle style)
    {
        double x1 = AttrDouble(el, "x1", 0);
        double y1 = AttrDouble(el, "y1", 0);
        double x2 = AttrDouble(el, "x2", 0);
        double y2 = AttrDouble(el, "y2", 0);
        // A line has no fill — only stroke renders. Bail when
        // no stroke color resolved either so we don't spin the
        // rasterizer for nothing.
        if (style.StrokeColor.IsTransparent) return;
        StrokePolyline(ctx, new List<(double, double)> { (x1, y1), (x2, y2) }, style);
    }

    private static void RenderPolygon(
        Element el, RenderCtx ctx, ShapeStyle style, bool closed)
    {
        var poly = ParsePoints(el.GetAttribute("points") ?? "");
        if (poly.Count < 2) return;
        if (closed && (poly[0].X != poly[^1].X || poly[0].Y != poly[^1].Y))
        {
            poly.Add(poly[0]);
        }
        if (closed)
        {
            FillAndStroke(poly, ctx, style);
        }
        else
        {
            // polyline is stroke-only by default (fill="none"
            // implied for a valid polyline in most usage); still,
            // SVG spec says fill defaults to black and polylines
            // are auto-closed for filling. Honor it if the
            // author asked for a fill explicitly.
            if (!style.FillColor.IsTransparent)
            {
                var closedPoly = new List<(double, double)>(poly) { poly[0] };
                FillPolygon(closedPoly, ctx, style.FillColor);
            }
            StrokePolyline(ctx, poly, style);
        }
    }

    private static void RenderPath(Element el, RenderCtx ctx, ShapeStyle style)
    {
        var d = el.GetAttribute("d");
        if (string.IsNullOrEmpty(d)) return;
        var subpaths = SvgPath.Parse(d);
        if (subpaths.Count == 0) return;

        // Fill as a single compound path so holes (e.g. the
        // center of an 'O') render correctly under even-odd.
        if (!style.FillColor.IsTransparent)
        {
            FillCompound(subpaths, ctx, style.FillColor);
        }
        if (!style.StrokeColor.IsTransparent && style.StrokeWidth > 0)
        {
            foreach (var sub in subpaths)
            {
                if (sub.Count >= 2) StrokePolyline(ctx, sub, style);
            }
        }
    }

    private static void FillAndStroke(
        List<(double X, double Y)> poly, RenderCtx ctx, ShapeStyle style)
    {
        if (!style.FillColor.IsTransparent)
        {
            FillPolygon(poly, ctx, style.FillColor);
        }
        if (!style.StrokeColor.IsTransparent && style.StrokeWidth > 0)
        {
            StrokePolyline(ctx, poly, style);
        }
    }

    /// <summary>Even-odd scanline fill. For each horizontal
    /// pixel row, collect the x-coordinates where the polygon
    /// edges cross that row, sort them, and fill every other
    /// span. Classic algorithm — handles concave shapes and
    /// holes without per-edge bookkeeping because a point is
    /// inside iff an odd number of edges lie to its left.</summary>
    private static void FillPolygon(
        List<(double X, double Y)> poly, RenderCtx ctx, PaintColor color)
    {
        FillCompound(new List<List<(double, double)>> { poly }, ctx, color);
    }

    /// <summary>Multi-subpath scanline fill: collect crossings
    /// from every edge of every subpath and fill under
    /// even-odd. Lets path data with holes (a glyph 'O', a
    /// donut) render correctly as one compound shape.</summary>
    private static void FillCompound(
        List<List<(double X, double Y)>> subpaths, RenderCtx ctx, PaintColor color)
    {
        if (color.IsTransparent) return;
        // Transform each subpath through the viewBox once, then
        // rasterize in screen coordinates. Transforming per
        // edge during the scan would re-do the same work for
        // every scanline.
        var transformed = new List<List<(double, double)>>(subpaths.Count);
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var sub in subpaths)
        {
            if (sub.Count < 2) continue;
            var t = new List<(double, double)>(sub.Count);
            foreach (var (sx, sy) in sub)
            {
                var (px, py) = ctx.Transform.Apply(sx, sy);
                t.Add((px, py));
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }
            transformed.Add(t);
        }
        if (transformed.Count == 0) return;

        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int y1 = Math.Min(ctx.Buffer.Height - 1, (int)Math.Ceiling(maxY));
        var crossings = new List<double>(16);

        for (int y = y0; y <= y1; y++)
        {
            crossings.Clear();
            // Sample edges at the pixel row center — gives
            // us the same "is this pixel inside?" result regardless
            // of whether an edge passes exactly through an
            // integer y, which matters when rectangles align
            // to pixel boundaries.
            double scanY = y + 0.5;
            foreach (var sub in transformed)
            {
                for (int i = 0; i < sub.Count - 1; i++)
                {
                    double ax = sub[i].Item1, ay = sub[i].Item2;
                    double bx = sub[i + 1].Item1, by = sub[i + 1].Item2;
                    if (ay == by) continue; // horizontal edge contributes nothing
                    // Half-open interval [min, max) — prevents
                    // a vertex shared by two edges from being
                    // counted twice.
                    double yMin = Math.Min(ay, by);
                    double yMax = Math.Max(ay, by);
                    if (scanY < yMin || scanY >= yMax) continue;
                    double t = (scanY - ay) / (by - ay);
                    double xCross = ax + t * (bx - ax);
                    crossings.Add(xCross);
                }
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int xStart = Math.Max(0, (int)Math.Round(crossings[i]));
                int xEnd = Math.Min(ctx.Buffer.Width - 1, (int)Math.Round(crossings[i + 1]) - 1);
                if (xEnd < xStart) continue;
                BlitRowSpan(ctx.Buffer, xStart, xEnd, y, color);
            }
        }
    }

    private static void BlitRowSpan(RasterBuffer buf, int x0, int x1, int y, PaintColor color)
    {
        if (color.IsOpaque)
        {
            int idx = (y * buf.Width + x0) * 4;
            for (int x = x0; x <= x1; x++, idx += 4)
            {
                buf.Pixels[idx] = color.R;
                buf.Pixels[idx + 1] = color.G;
                buf.Pixels[idx + 2] = color.B;
                buf.Pixels[idx + 3] = 255;
            }
            return;
        }
        double a = color.A / 255.0;
        double oneMinus = 1.0 - a;
        int i2 = (y * buf.Width + x0) * 4;
        for (int x = x0; x <= x1; x++, i2 += 4)
        {
            buf.Pixels[i2] = (byte)(color.R * a + buf.Pixels[i2] * oneMinus);
            buf.Pixels[i2 + 1] = (byte)(color.G * a + buf.Pixels[i2 + 1] * oneMinus);
            buf.Pixels[i2 + 2] = (byte)(color.B * a + buf.Pixels[i2 + 2] * oneMinus);
            buf.Pixels[i2 + 3] = (byte)Math.Min(255, buf.Pixels[i2 + 3] + color.A * oneMinus);
        }
    }

    /// <summary>Stroke each segment of <paramref name="points"/>
    /// using the configured stroke color / width. Thickness is
    /// emulated by drawing a filled circle's worth of pixels
    /// along each step of a Bresenham traversal — cheap and
    /// visually adequate at typical stroke widths of 1..4 px.</summary>
    private static void StrokePolyline(
        RenderCtx ctx, List<(double X, double Y)> points, ShapeStyle style)
    {
        if (points.Count < 2) return;
        double thickness = Math.Max(1, style.StrokeWidth * ctx.Transform.UniformScale);
        int radius = (int)Math.Max(0, Math.Round(thickness / 2) - 0);
        for (int i = 0; i < points.Count - 1; i++)
        {
            var (ax, ay) = ctx.Transform.Apply(points[i].X, points[i].Y);
            var (bx, by) = ctx.Transform.Apply(points[i + 1].X, points[i + 1].Y);
            DrawThickLine(ctx.Buffer,
                (int)Math.Round(ax), (int)Math.Round(ay),
                (int)Math.Round(bx), (int)Math.Round(by),
                radius, style.StrokeColor);
        }
    }

    private static void DrawThickLine(
        RasterBuffer buf, int x0, int y0, int x1, int y1, int radius, PaintColor color)
    {
        // Bresenham with a filled-disc brush at each step. The
        // disc re-stamps every pixel for radius > 1, so strokes
        // have overdraw proportional to line length × radius²;
        // at radius ≤ 3 that's fine. Thicker strokes should
        // rasterize as a polygon (stroke-to-path); we'll add
        // that when a page needs it.
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int x = x0, y = y0;
        int safety = (dx + dy) * 4 + 4; // defensive termination
        while (safety-- > 0)
        {
            StampBrush(buf, x, y, radius, color);
            if (x == x1 && y == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
    }

    private static void StampBrush(RasterBuffer buf, int cx, int cy, int radius, PaintColor color)
    {
        if (radius <= 0)
        {
            PutPixel(buf, cx, cy, color);
            return;
        }
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                PutPixel(buf, cx + dx, cy + dy, color);
            }
        }
    }

    private static void PutPixel(RasterBuffer buf, int x, int y, PaintColor color)
    {
        if ((uint)x >= (uint)buf.Width || (uint)y >= (uint)buf.Height) return;
        int idx = (y * buf.Width + x) * 4;
        if (color.IsOpaque)
        {
            buf.Pixels[idx] = color.R;
            buf.Pixels[idx + 1] = color.G;
            buf.Pixels[idx + 2] = color.B;
            buf.Pixels[idx + 3] = 255;
            return;
        }
        double a = color.A / 255.0;
        double oneMinus = 1.0 - a;
        buf.Pixels[idx] = (byte)(color.R * a + buf.Pixels[idx] * oneMinus);
        buf.Pixels[idx + 1] = (byte)(color.G * a + buf.Pixels[idx + 1] * oneMinus);
        buf.Pixels[idx + 2] = (byte)(color.B * a + buf.Pixels[idx + 2] * oneMinus);
        buf.Pixels[idx + 3] = (byte)Math.Min(255, buf.Pixels[idx + 3] + color.A * oneMinus);
    }

    private static List<(double X, double Y)> ParsePoints(string s)
    {
        var result = new List<(double, double)>();
        if (string.IsNullOrWhiteSpace(s)) return result;
        // Accept comma or whitespace separators; pair adjacent
        // numbers into (x, y) tuples.
        var nums = new List<double>();
        int i = 0;
        var buf = new System.Text.StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            if (double.TryParse(buf.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var v)) nums.Add(v);
            buf.Clear();
        }
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { Flush(); i++; continue; }
            buf.Append(c); i++;
        }
        Flush();
        for (int j = 0; j + 1 < nums.Count; j += 2)
        {
            result.Add((nums[j], nums[j + 1]));
        }
        return result;
    }

    private static ShapeStyle MergeStyle(ShapeStyle inherited, Element element)
    {
        // Presentation attributes win over the inherited value
        // but lose to inline <c>style</c> (we don't parse the
        // style attribute here — adding it is a follow-up if
        // pages start using it heavily for SVG).
        var fillAttr = element.GetAttribute("fill");
        var fillOpacityAttr = element.GetAttribute("fill-opacity");
        var strokeAttr = element.GetAttribute("stroke");
        var strokeOpacityAttr = element.GetAttribute("stroke-opacity");
        var strokeWidthAttr = element.GetAttribute("stroke-width");

        var fill = inherited.FillColor;
        bool fillSet = inherited.FillExplicit;
        if (fillAttr is not null)
        {
            fillSet = true;
            fill = fillAttr == "none" ? PaintColor.Transparent : CssColor.Parse(fillAttr);
        }
        if (fillOpacityAttr is not null
            && double.TryParse(fillOpacityAttr, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var fo))
        {
            fill = ApplyAlpha(fill, fo);
        }

        var stroke = inherited.StrokeColor;
        if (strokeAttr is not null)
        {
            stroke = strokeAttr == "none" ? PaintColor.Transparent : CssColor.Parse(strokeAttr);
        }
        if (strokeOpacityAttr is not null
            && double.TryParse(strokeOpacityAttr, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var so))
        {
            stroke = ApplyAlpha(stroke, so);
        }

        double strokeWidth = inherited.StrokeWidth;
        if (strokeWidthAttr is not null
            && TryParseLength(strokeWidthAttr, out var sw))
        {
            strokeWidth = sw;
        }

        return new ShapeStyle
        {
            FillColor = fill,
            FillExplicit = fillSet,
            StrokeColor = stroke,
            StrokeWidth = strokeWidth,
        };
    }

    private static PaintColor ApplyAlpha(PaintColor c, double opacity)
    {
        if (opacity <= 0) return PaintColor.Transparent;
        if (opacity >= 1) return c;
        return new PaintColor(c.R, c.G, c.B, (byte)Math.Clamp(c.A * opacity, 0, 255));
    }

    private static bool TryParseLength(string s, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        // Strip trailing unit (px / em / pt / % / ...). Treat
        // every unit as a bare number — SVG uses user units
        // by default and stroke-width is almost always unit-
        // less anyway.
        int end = t.Length;
        while (end > 0 && !char.IsDigit(t[end - 1]) && t[end - 1] != '.')
        {
            end--;
        }
        if (end == 0) return false;
        return double.TryParse(t.AsSpan(0, end), NumberStyles.Float,
            CultureInfo.InvariantCulture, out v);
    }

    private static double AttrDouble(Element el, string name, double fallback)
    {
        var v = el.GetAttribute(name);
        if (v is null) return fallback;
        return TryParseLength(v, out var r) ? r : fallback;
    }

    private static SvgTransform ResolveTransform(Element svg, int x, int y, int w, int h)
    {
        // viewBox = "minX minY width height" — the coordinate
        // space authored geometry is in. Maps onto the target
        // (x, y, w, h) rectangle. Without a viewBox, the svg's
        // own width/height attributes define the coord space
        // (identity scale inside that rect).
        var viewBoxAttr = svg.GetAttribute("viewBox") ?? svg.GetAttribute("viewbox");
        if (!string.IsNullOrEmpty(viewBoxAttr))
        {
            var parts = viewBoxAttr.Split(new[] { ' ', ',' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbX)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbY)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbW)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbH)
                && vbW > 0 && vbH > 0)
            {
                return new SvgTransform(
                    scaleX: w / vbW,
                    scaleY: h / vbH,
                    translateX: x - vbX * (w / vbW),
                    translateY: y - vbY * (h / vbH));
            }
        }
        // No viewBox: authored coords are raw pixels inside
        // the target rectangle.
        return new SvgTransform(1, 1, x, y);
    }

    private readonly struct RenderCtx(RasterBuffer buffer, SvgTransform transform)
    {
        public RasterBuffer Buffer { get; } = buffer;
        public SvgTransform Transform { get; } = transform;
    }

    private readonly struct SvgTransform(double scaleX, double scaleY, double translateX, double translateY)
    {
        public double ScaleX { get; } = scaleX;
        public double ScaleY { get; } = scaleY;
        public double TranslateX { get; } = translateX;
        public double TranslateY { get; } = translateY;
        public double UniformScale => 0.5 * (Math.Abs(ScaleX) + Math.Abs(ScaleY));
        public (double X, double Y) Apply(double x, double y) =>
            (x * ScaleX + TranslateX, y * ScaleY + TranslateY);
    }

    /// <summary>Resolved paint state for a shape. Defaults
    /// follow the SVG painting model: <c>fill</c> is black,
    /// <c>stroke</c> is none, <c>stroke-width</c> is 1.
    /// <see cref="FillExplicit"/> tracks whether an ancestor
    /// actually declared a fill — some consumers (like
    /// <c>polyline</c>) treat the default fill as "none" even
    /// though the spec says black.</summary>
    private struct ShapeStyle
    {
        public PaintColor FillColor { get; set; } = PaintColor.Black;
        public bool FillExplicit { get; set; } = false;
        public PaintColor StrokeColor { get; set; } = PaintColor.Transparent;
        public double StrokeWidth { get; set; } = 1;
        public ShapeStyle() { }
    }
}
