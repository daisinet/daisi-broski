using Daisi.Broski.Engine.Paint;

namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// Paints a line of text using a <see cref="TtfReader"/>'s
/// glyph outlines. Each character is converted to a list of
/// subpath polygons (via the TTF reader's Bézier flattener),
/// transformed into screen coordinates at the requested
/// pixel size, and filled with even-odd scanline fill — the
/// same rasterizer <c>SvgRenderer</c> uses for shape fills.
///
/// <para>
/// Deliberately minimal: no hinting, no subpixel positioning,
/// no kerning, no ligatures, no shaping. Just enough to get
/// real letterforms on screen in place of the bundled bitmap
/// font. Advance widths from <c>hmtx</c> drive per-character
/// X positioning.
/// </para>
/// </summary>
public static class GlyphRasterizer
{
    /// <summary>Measure a line of text in pixels at
    /// <paramref name="pixelSize"/>. Uses the font's
    /// <c>hmtx</c> advance widths so widths match what
    /// <see cref="DrawText"/> will emit.</summary>
    public static double MeasureText(TtfReader font, string text, double pixelSize)
    {
        if (string.IsNullOrEmpty(text) || font.UnitsPerEm <= 0) return 0;
        double scale = pixelSize / font.UnitsPerEm;
        double width = 0;
        foreach (var ch in text)
        {
            int gid = font.GlyphIndex(ch);
            width += font.AdvanceWidth(gid) * scale;
        }
        return width;
    }

    /// <summary>Draw <paramref name="text"/> at
    /// <paramref name="baselineY"/> (the baseline in screen
    /// coordinates — typical CSS line boxes place baselines
    /// about 80% of the line height down from the top). Glyph
    /// bounds extending above or below the baseline are drawn
    /// naturally; buffer bounds clip, the box doesn't.</summary>
    public static void DrawText(
        RasterBuffer buffer, TtfReader font, double startX, double baselineY,
        string text, double pixelSize, PaintColor color) =>
        DrawText(buffer, font, startX, baselineY, text, pixelSize, color, synthesizeBold: false);

    /// <summary>Draw text, optionally double-stamping each
    /// glyph to synthesize bold for fonts we rendered at a
    /// lighter weight (variable-font axes aren't honored by
    /// TtfReader yet, so <c>font-weight: 700</c> otherwise
    /// paints at the base weight). The offset is a fraction
    /// of em so bold looks proportional at all sizes.</summary>
    public static void DrawText(
        RasterBuffer buffer, TtfReader font, double startX, double baselineY,
        string text, double pixelSize, PaintColor color, bool synthesizeBold)
    {
        if (string.IsNullOrEmpty(text) || color.IsTransparent) return;
        if (font.UnitsPerEm <= 0) return;
        double scale = pixelSize / font.UnitsPerEm;
        double cursorX = startX;
        // Bold offset proportional to em — about 1/14th gives
        // a stroke that reads bold without blurring. Stamp
        // three times (-offset, 0, +offset) so the visual
        // stroke thickens symmetrically rather than only
        // growing rightward.
        double boldOffset = synthesizeBold ? Math.Max(0.8, pixelSize / 14.0) : 0;
        foreach (var ch in text)
        {
            int gid = font.GlyphIndex(ch);
            var outlines = font.GlyphOutline(gid);
            if (outlines.Count > 0)
            {
                FillGlyph(buffer, outlines, cursorX, baselineY, scale, color);
                if (synthesizeBold)
                {
                    FillGlyph(buffer, outlines, cursorX + boldOffset, baselineY,
                        scale, color);
                    FillGlyph(buffer, outlines, cursorX - boldOffset * 0.5, baselineY,
                        scale, color);
                }
            }
            cursorX += font.AdvanceWidth(gid) * scale;
        }
    }

    /// <summary>Pixel width including synthesized bold's
    /// horizontal offset, so layout measurement matches what
    /// the rasterizer will emit.</summary>
    public static double MeasureText(
        TtfReader font, string text, double pixelSize, bool synthesizeBold)
    {
        double w = MeasureText(font, text, pixelSize);
        if (synthesizeBold) w += Math.Max(0.8, pixelSize / 14.0);
        return w;
    }

    /// <summary>Even-odd scanline fill of the glyph's
    /// subpaths, transformed by (<paramref name="originX"/>,
    /// <paramref name="baselineY"/>) with y flipped — TTF
    /// outlines are authored in +Y-up, our buffer is +Y-down.</summary>
    private static void FillGlyph(
        RasterBuffer buffer, List<List<(double X, double Y)>> outlines,
        double originX, double baselineY, double scale, PaintColor color)
    {
        // Transform each subpath and record y-extent so the
        // scanline loop only iterates the glyph's bounding
        // rows (not the whole buffer).
        var transformed = new List<List<(double, double)>>(outlines.Count);
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        foreach (var sub in outlines)
        {
            if (sub.Count < 2) continue;
            var t = new List<(double, double)>(sub.Count);
            foreach (var (sx, sy) in sub)
            {
                double px = originX + sx * scale;
                double py = baselineY - sy * scale; // flip Y
                t.Add((px, py));
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }
            transformed.Add(t);
        }
        if (transformed.Count == 0) return;

        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int y1 = Math.Min(buffer.Height - 1, (int)Math.Ceiling(maxY));
        var crossings = new List<double>(16);
        for (int y = y0; y <= y1; y++)
        {
            crossings.Clear();
            double scanY = y + 0.5;
            foreach (var sub in transformed)
            {
                for (int i = 0; i < sub.Count - 1; i++)
                {
                    double ax = sub[i].Item1, ay = sub[i].Item2;
                    double bx = sub[i + 1].Item1, by = sub[i + 1].Item2;
                    if (ay == by) continue;
                    double yMin = Math.Min(ay, by);
                    double yMax = Math.Max(ay, by);
                    if (scanY < yMin || scanY >= yMax) continue;
                    double t = (scanY - ay) / (by - ay);
                    crossings.Add(ax + t * (bx - ax));
                }
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int xStart = Math.Max(0, (int)Math.Round(crossings[i]));
                int xEnd = Math.Min(buffer.Width - 1, (int)Math.Round(crossings[i + 1]) - 1);
                if (xEnd < xStart) continue;
                BlitSpan(buffer, xStart, xEnd, y, color);
            }
        }
    }

    private static void BlitSpan(RasterBuffer buf, int x0, int x1, int y, PaintColor color)
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
}
