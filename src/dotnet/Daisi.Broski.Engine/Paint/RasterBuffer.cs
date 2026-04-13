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
