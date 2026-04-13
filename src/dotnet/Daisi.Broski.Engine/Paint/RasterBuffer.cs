namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Tightly-packed RGBA pixel buffer used as the paint
/// target. 4 bytes per pixel, row-major, top-down.
/// Provides the two operations the painter needs:
/// <see cref="FillRect"/> for solid backgrounds and
/// <see cref="StrokeRect"/> for borders. Anti-aliasing,
/// alpha blending math, and gradients are deferred — every
/// fill is a flat color.
/// </summary>
public sealed class RasterBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RasterBuffer(int width, int height, PaintColor background = default)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Pixels = new byte[Width * Height * 4];
        if (background.A > 0) Clear(background);
    }

    /// <summary>Fill the entire buffer with the given color
    /// — used to set the canvas background before any
    /// drawing.</summary>
    public void Clear(PaintColor color)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = color.A;
        }
    }

    /// <summary>Fill the rectangle <paramref name="x"/>..
    /// (x+w)-1 × <paramref name="y"/>..(y+h)-1 with
    /// <paramref name="color"/>. Coordinates are clipped to
    /// the buffer bounds. Transparent / zero-area rects are
    /// skipped without drawing.</summary>
    public void FillRect(int x, int y, int w, int h, PaintColor color)
    {
        if (color.IsTransparent) return;
        if (w <= 0 || h <= 0) return;
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w);
        int y1 = Math.Min(Height, y + h);
        if (x0 >= x1 || y0 >= y1) return;

        if (color.IsOpaque)
        {
            for (int row = y0; row < y1; row++)
            {
                int idx = (row * Width + x0) * 4;
                for (int col = x0; col < x1; col++, idx += 4)
                {
                    Pixels[idx] = color.R;
                    Pixels[idx + 1] = color.G;
                    Pixels[idx + 2] = color.B;
                    Pixels[idx + 3] = 255;
                }
            }
            return;
        }

        // Alpha blend: src OVER dst, both pre-multiplied
        // implicitly. Cheap because we only walk pixels in
        // the clipped rect; no anti-aliasing pass.
        double a = color.A / 255.0;
        double oneMinusA = 1.0 - a;
        for (int row = y0; row < y1; row++)
        {
            int idx = (row * Width + x0) * 4;
            for (int col = x0; col < x1; col++, idx += 4)
            {
                Pixels[idx] = (byte)(color.R * a + Pixels[idx] * oneMinusA);
                Pixels[idx + 1] = (byte)(color.G * a + Pixels[idx + 1] * oneMinusA);
                Pixels[idx + 2] = (byte)(color.B * a + Pixels[idx + 2] * oneMinusA);
                Pixels[idx + 3] = (byte)Math.Min(255, Pixels[idx + 3] + color.A * oneMinusA);
            }
        }
    }

    /// <summary>Fill a rounded rectangle with per-corner
    /// radii. Each corner is clipped against a circle of
    /// its own radius so different CSS <c>border-radius</c>
    /// values per corner render correctly. Pixels outside
    /// the arcs aren't filled; pixels inside get the full
    /// color (no anti-aliasing — a sub-pixel coverage pass
    /// is a follow-up).</summary>
    public void FillRoundedRect(
        int x, int y, int w, int h, PaintColor color,
        int topLeft, int topRight, int bottomRight, int bottomLeft)
    {
        if (color.IsTransparent) return;
        if (w <= 0 || h <= 0) return;
        // Clamp radii to half the smaller dimension so they
        // can't overlap (spec §5.1). Integer math keeps the
        // inside-test cheap.
        int maxR = Math.Min(w, h) / 2;
        if (topLeft > maxR) topLeft = maxR;
        if (topRight > maxR) topRight = maxR;
        if (bottomRight > maxR) bottomRight = maxR;
        if (bottomLeft > maxR) bottomLeft = maxR;

        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w);
        int y1 = Math.Min(Height, y + h);
        if (x0 >= x1 || y0 >= y1) return;

        // Corner centers (relative to (x, y)).
        // Inside-arc test: (dx*dx + dy*dy) <= r*r, where
        // dx/dy are distances from the center to the pixel.
        double a = color.A / 255.0;
        double oneMinus = 1 - a;

        for (int py = y0; py < y1; py++)
        {
            int localY = py - y;
            for (int px = x0; px < x1; px++)
            {
                int localX = px - x;
                if (!PointInRoundedRect(localX, localY, w, h,
                    topLeft, topRight, bottomRight, bottomLeft)) continue;
                int idx = (py * Width + px) * 4;
                if (color.IsOpaque)
                {
                    Pixels[idx] = color.R;
                    Pixels[idx + 1] = color.G;
                    Pixels[idx + 2] = color.B;
                    Pixels[idx + 3] = 255;
                }
                else
                {
                    Pixels[idx] = (byte)(color.R * a + Pixels[idx] * oneMinus);
                    Pixels[idx + 1] = (byte)(color.G * a + Pixels[idx + 1] * oneMinus);
                    Pixels[idx + 2] = (byte)(color.B * a + Pixels[idx + 2] * oneMinus);
                    Pixels[idx + 3] = (byte)Math.Min(255, Pixels[idx + 3] + color.A * oneMinus);
                }
            }
        }
    }

    /// <summary>Is <c>(lx, ly)</c> inside a rounded rect of
    /// size <c>w × h</c> with the given corner radii? Clamps
    /// each radius to <c>min(w, h) / 2</c> so callers don't
    /// have to — a CSS pill often declares
    /// <c>border-radius: 9999px</c> which would otherwise
    /// test against a 9999px arc and reject most of the
    /// box. With the clamp the pill reads as the intended
    /// half-height curve.</summary>
    internal static bool PointInRoundedRect(
        int lx, int ly, int w, int h,
        int tl, int tr, int br, int bl)
    {
        int maxR = Math.Min(w, h) / 2;
        if (tl > maxR) tl = maxR;
        if (tr > maxR) tr = maxR;
        if (br > maxR) br = maxR;
        if (bl > maxR) bl = maxR;
        // Top-left corner region
        if (lx < tl && ly < tl)
        {
            int dx = tl - lx, dy = tl - ly;
            return dx * dx + dy * dy <= tl * tl;
        }
        // Top-right
        if (lx >= w - tr && ly < tr)
        {
            int dx = lx - (w - tr - 1), dy = tr - ly;
            return dx * dx + dy * dy <= tr * tr;
        }
        // Bottom-right
        if (lx >= w - br && ly >= h - br)
        {
            int dx = lx - (w - br - 1), dy = ly - (h - br - 1);
            return dx * dx + dy * dy <= br * br;
        }
        // Bottom-left
        if (lx < bl && ly >= h - bl)
        {
            int dx = bl - lx, dy = ly - (h - bl - 1);
            return dx * dx + dy * dy <= bl * bl;
        }
        return true;
    }

    /// <summary>Draw a 1-side-at-a-time outlined rectangle
    /// — top, right, bottom, left — using the supplied
    /// border widths. Each side fills the rect spanning
    /// from the outer edge inward by its width. Corners are
    /// painted by the top + bottom passes (so the left/
    /// right widths only paint the central section), giving
    /// a clean miter without per-pixel corner math.</summary>
    public void StrokeRect(
        int x, int y, int w, int h,
        int top, int right, int bottom, int left,
        PaintColor topColor, PaintColor rightColor,
        PaintColor bottomColor, PaintColor leftColor)
    {
        if (w <= 0 || h <= 0) return;
        if (top > 0) FillRect(x, y, w, top, topColor);
        if (bottom > 0) FillRect(x, y + h - bottom, w, bottom, bottomColor);
        if (left > 0) FillRect(x, y + top, left, h - top - bottom, leftColor);
        if (right > 0) FillRect(x + w - right, y + top, right, h - top - bottom, rightColor);
    }
}
