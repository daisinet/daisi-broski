namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Separable box blur over a rectangular region of a
/// <see cref="RasterBuffer"/>. Two 1D passes (horizontal
/// then vertical) give a box-kernel blur; three chained
/// iterations approximate a Gaussian of the same total
/// radius (per Wells 1986 and the widely-cited stackblur
/// derivation), but one pass is enough for the cosmetic
/// <c>backdrop-filter: blur()</c> we need here.
///
/// <para>
/// Used by the painter to implement CSS
/// <c>backdrop-filter: blur(Npx)</c>. The painter snapshots
/// the buffer region under the element's border-box,
/// blurs the snapshot with <see cref="BlurRegion"/>, and
/// writes it back — masked by the rounded-rect shape so
/// <c>border-radius</c> clips the blur the same way it
/// clips the background fill.
/// </para>
///
/// <para>
/// Deliberately not a Gaussian — a real Gaussian kernel is
/// separable but needs per-radius weight precompute. Box
/// blur is visually close enough for glassmorphism (the
/// human eye can't tell a Gaussian from three box passes
/// at typical blur radii) and a fixed-cost O(pixels) no
/// matter how big the radius, which matters for the 40-80px
/// radii these CSS effects commonly use.
/// </para>
/// </summary>
internal static class BoxBlur
{
    /// <summary>Blur the rectangle
    /// <c>[x, x+w) × [y, y+h)</c> of <paramref name="buffer"/>
    /// in-place with a box kernel of radius
    /// <paramref name="radius"/>. Pixel contribution stops at
    /// the region boundary — we don't sample from outside
    /// the clipped rect, so blur applied to a card's backdrop
    /// doesn't bleed the document margin color into the card.
    /// A <paramref name="radius"/> of 0 (or a zero-area rect)
    /// is a no-op.</summary>
    public static void BlurRegion(
        RasterBuffer buffer, int x, int y, int w, int h, int radius)
    {
        if (radius <= 0) return;
        if (w <= 0 || h <= 0) return;

        // Clip to buffer bounds — callers may pass a
        // border-box rect that partly overflows the canvas
        // (the dev viewport could be smaller than declared
        // element sizes during layout).
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(buffer.Width, x + w);
        int y1 = Math.Min(buffer.Height, y + h);
        int rw = x1 - x0;
        int rh = y1 - y0;
        if (rw <= 0 || rh <= 0) return;

        // Scratch buffer holds the horizontal-pass output;
        // the vertical pass reads from it and writes back
        // into the main buffer. Allocating 4*rw*rh bytes is
        // cheap relative to the blur itself.
        var scratch = new byte[rw * rh * 4];

        // Horizontal pass — for each row, accumulate a
        // running sum over a window of radius*2+1 pixels.
        // Running-sum approach makes every pixel an O(1)
        // operation regardless of radius.
        BlurHorizontal(buffer, scratch, x0, y0, rw, rh, radius);
        BlurVertical(scratch, buffer, x0, y0, rw, rh, radius);
    }

    private static void BlurHorizontal(
        RasterBuffer src, byte[] dst,
        int x0, int y0, int rw, int rh, int radius)
    {
        int srcStride = src.Width * 4;
        int dstStride = rw * 4;
        int window = radius * 2 + 1;

        for (int row = 0; row < rh; row++)
        {
            int srcRow = (y0 + row) * srcStride + x0 * 4;
            int dstRow = row * dstStride;

            int sumR = 0, sumG = 0, sumB = 0, sumA = 0, count = 0;

            // Prime the window with the first `radius+1`
            // pixels (indices 0..radius clamped to region).
            for (int k = 0; k <= radius && k < rw; k++)
            {
                int s = srcRow + k * 4;
                sumR += src.Pixels[s];
                sumG += src.Pixels[s + 1];
                sumB += src.Pixels[s + 2];
                sumA += src.Pixels[s + 3];
                count++;
            }

            for (int col = 0; col < rw; col++)
            {
                int d = dstRow + col * 4;
                dst[d] = (byte)(sumR / count);
                dst[d + 1] = (byte)(sumG / count);
                dst[d + 2] = (byte)(sumB / count);
                dst[d + 3] = (byte)(sumA / count);

                // Slide: add the pixel entering the window
                // on the right, subtract the one leaving on
                // the left. Clamps at the region edges so
                // we don't pull color from outside the
                // clipped region.
                int nextIn = col + radius + 1;
                if (nextIn < rw)
                {
                    int s = srcRow + nextIn * 4;
                    sumR += src.Pixels[s];
                    sumG += src.Pixels[s + 1];
                    sumB += src.Pixels[s + 2];
                    sumA += src.Pixels[s + 3];
                    count++;
                }
                int nextOut = col - radius;
                if (nextOut >= 0)
                {
                    int s = srcRow + nextOut * 4;
                    sumR -= src.Pixels[s];
                    sumG -= src.Pixels[s + 1];
                    sumB -= src.Pixels[s + 2];
                    sumA -= src.Pixels[s + 3];
                    count--;
                }
            }
        }
    }

    private static void BlurVertical(
        byte[] src, RasterBuffer dst,
        int x0, int y0, int rw, int rh, int radius)
    {
        int srcStride = rw * 4;
        int dstStride = dst.Width * 4;

        for (int col = 0; col < rw; col++)
        {
            int srcCol = col * 4;
            int dstCol = x0 * 4 + col * 4;

            int sumR = 0, sumG = 0, sumB = 0, sumA = 0, count = 0;

            for (int k = 0; k <= radius && k < rh; k++)
            {
                int s = k * srcStride + srcCol;
                sumR += src[s];
                sumG += src[s + 1];
                sumB += src[s + 2];
                sumA += src[s + 3];
                count++;
            }

            for (int row = 0; row < rh; row++)
            {
                int d = (y0 + row) * dstStride + dstCol;
                dst.Pixels[d] = (byte)(sumR / count);
                dst.Pixels[d + 1] = (byte)(sumG / count);
                dst.Pixels[d + 2] = (byte)(sumB / count);
                dst.Pixels[d + 3] = (byte)(sumA / count);

                int nextIn = row + radius + 1;
                if (nextIn < rh)
                {
                    int s = nextIn * srcStride + srcCol;
                    sumR += src[s];
                    sumG += src[s + 1];
                    sumB += src[s + 2];
                    sumA += src[s + 3];
                    count++;
                }
                int nextOut = row - radius;
                if (nextOut >= 0)
                {
                    int s = nextOut * srcStride + srcCol;
                    sumR -= src[s];
                    sumG -= src[s + 1];
                    sumB -= src[s + 2];
                    sumA -= src[s + 3];
                    count--;
                }
            }
        }
    }

    /// <summary>Blur a rectangular region masked to a
    /// rounded-rect shape — pixels outside the mask keep
    /// their original unblurred color. Used by the painter
    /// so <c>border-radius</c> clips <c>backdrop-filter</c>
    /// the same way it clips the element's background.
    /// When all four radii are zero this is equivalent to
    /// <see cref="BlurRegion"/>.</summary>
    public static void BlurRegionMaskedRounded(
        RasterBuffer buffer, int x, int y, int w, int h, int radius,
        int cornerTL, int cornerTR, int cornerBR, int cornerBL)
    {
        if (radius <= 0) return;
        if (w <= 0 || h <= 0) return;
        if (cornerTL == 0 && cornerTR == 0 && cornerBR == 0 && cornerBL == 0)
        {
            BlurRegion(buffer, x, y, w, h, radius);
            return;
        }

        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(buffer.Width, x + w);
        int y1 = Math.Min(buffer.Height, y + h);
        int rw = x1 - x0;
        int rh = y1 - y0;
        if (rw <= 0 || rh <= 0) return;

        // Snapshot the original region first so the mask
        // can pick between blurred-inside and original-
        // outside pixel-by-pixel.
        var original = new byte[rw * rh * 4];
        int srcStride = buffer.Width * 4;
        for (int row = 0; row < rh; row++)
        {
            Buffer.BlockCopy(buffer.Pixels,
                (y0 + row) * srcStride + x0 * 4,
                original, row * rw * 4, rw * 4);
        }

        // Blur everything, then restore the pixels outside
        // the rounded-rect mask. Two passes is cheaper than
        // a masked blur because the blur kernel itself is
        // already an O(pixels) sliding sum — trying to skip
        // pixels mid-sum breaks the running-total invariant.
        BlurRegion(buffer, x0, y0, rw, rh, radius);

        for (int row = 0; row < rh; row++)
        {
            int localY = row;
            int absY = y0 + row;
            for (int col = 0; col < rw; col++)
            {
                int localX = col;
                bool inside = RasterBuffer.PointInRoundedRect(
                    x0 + col - x, absY - y, w, h,
                    cornerTL, cornerTR, cornerBR, cornerBL);
                if (!inside)
                {
                    int s = (localY * rw + localX) * 4;
                    int d = absY * srcStride + (x0 + col) * 4;
                    buffer.Pixels[d] = original[s];
                    buffer.Pixels[d + 1] = original[s + 1];
                    buffer.Pixels[d + 2] = original[s + 2];
                    buffer.Pixels[d + 3] = original[s + 3];
                }
            }
        }
    }
}
