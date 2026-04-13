using System.IO.Compression;

namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Hand-rolled PNG decoder, BCL-only. Returns a
/// <see cref="RasterBuffer"/> with the decoded RGBA pixels.
/// Mirrors the encoder side of <see cref="PngEncoder"/> —
/// supports the same minimum-viable subset:
///
/// <list type="bullet">
/// <item>8-bit depth (the common case; 16-bit and sub-byte
///   palettes deferred).</item>
/// <item>Color types 2 (truecolor RGB) and 6 (truecolor +
///   alpha). Indexed (3) and grayscale (0/4) deferred —
///   they require palette + tRNS chunk handling.</item>
/// <item>Non-interlaced. Adam7 interlacing is rare on the
///   modern web and adds two passes of complexity for
///   tiny visual quality benefit at typical viewport
///   sizes.</item>
/// <item>All 5 row filters (None, Sub, Up, Average,
///   Paeth) — required by the spec for any decoder
///   that wants to read real-world PNGs.</item>
/// <item>IDAT spanning multiple chunks (concatenated and
///   inflated as one zlib stream).</item>
/// </list>
///
/// <para>
/// Returns <c>null</c> when the input isn't a recognizable
/// PNG or uses an unsupported feature — callers should
/// fall back to the placeholder rect they'd already paint
/// for missing images.
/// </para>
/// </summary>
public static class PngDecoder
{
    public static RasterBuffer? TryDecode(byte[] data)
    {
        if (data is null || data.Length < 8) return null;
        if (!HasPngSignature(data)) return null;

        try
        {
            int p = 8;
            int width = 0, height = 0;
            byte bitDepth = 0, colorType = 0;
            byte interlace = 0;
            byte[]? palette = null;
            byte[]? palAlpha = null;
            using var idatStream = new MemoryStream();

            while (p + 8 <= data.Length)
            {
                int len = ReadU32(data, p); p += 4;
                if (len < 0 || p + 4 + len + 4 > data.Length) return null;
                string type = ReadAscii4(data, p); p += 4;

                if (type == "IHDR")
                {
                    if (len != 13) return null;
                    width = ReadU32(data, p);
                    height = ReadU32(data, p + 4);
                    bitDepth = data[p + 8];
                    colorType = data[p + 9];
                    interlace = data[p + 12];
                }
                else if (type == "PLTE")
                {
                    palette = new byte[len];
                    Buffer.BlockCopy(data, p, palette, 0, len);
                }
                else if (type == "tRNS")
                {
                    palAlpha = new byte[len];
                    Buffer.BlockCopy(data, p, palAlpha, 0, len);
                }
                else if (type == "IDAT")
                {
                    idatStream.Write(data, p, len);
                }
                else if (type == "IEND")
                {
                    break;
                }
                p += len + 4;
            }

            if (width <= 0 || height <= 0) return null;
            if (interlace != 0) return null;
            // Supported color types:
            //   0 — grayscale (bit depth 8 only for v1)
            //   2 — truecolor RGB (bit depth 8)
            //   3 — indexed palette (bit depth 1/2/4/8)
            //   4 — grayscale + alpha (bit depth 8)
            //   6 — truecolor RGB + alpha (bit depth 8)
            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break; // gray
                case 2: channels = 3; break; // RGB
                case 3: channels = 1; break; // palette index
                case 4: channels = 2; break; // gray+alpha
                case 6: channels = 4; break; // RGBA
                default: return null;
            }
            if (colorType != 3 && bitDepth != 8) return null;
            if (colorType == 3 && bitDepth > 8) return null;

            // Palette PNGs can pack multiple pixels per byte
            // for bit depths 1 / 2 / 4. Compute the raw
            // scanline size in bits → bytes (rounded up).
            int bitsPerPixel = colorType == 3 ? bitDepth : channels * 8;
            int rowBits = width * bitsPerPixel;
            int stride = (rowBits + 7) / 8;
            int totalRaw = (stride + 1) * height;

            var raw = new byte[totalRaw];
            idatStream.Position = 0;
            using (var z = new ZLibStream(idatStream, CompressionMode.Decompress, leaveOpen: true))
            {
                int read = 0;
                while (read < totalRaw)
                {
                    int n = z.Read(raw, read, totalRaw - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < totalRaw) return null;
            }

            return Unfilter(raw, width, height, colorType, bitDepth, channels,
                stride, palette, palAlpha);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Apply PNG row filters in reverse and expand
    /// the scanline into RGBA8888 in the output buffer. The
    /// filter byte distance is the pixel's byte stride for
    /// 8-bit formats; for sub-byte palette PNGs (1/2/4 bit
    /// depths) the spec says filtering uses 1 byte
    /// regardless.</summary>
    private static RasterBuffer? Unfilter(
        byte[] raw, int width, int height,
        int colorType, int bitDepth, int channels, int stride,
        byte[]? palette, byte[]? palAlpha)
    {
        // bpp for the filter's "left" distance — spec §9.2:
        // "bytes per pixel", rounded up, with sub-byte formats
        // using 1.
        int filterBpp = (colorType == 3 || bitDepth < 8)
            ? 1
            : channels * (bitDepth / 8);

        var prev = new byte[stride];
        var current = new byte[stride];
        var buffer = new RasterBuffer(width, height);

        int p = 0;
        for (int row = 0; row < height; row++)
        {
            byte filter = raw[p++];
            switch (filter)
            {
                case 0:
                    Buffer.BlockCopy(raw, p, current, 0, stride);
                    break;
                case 1:
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= filterBpp ? current[i - filterBpp] : (byte)0;
                        current[i] = (byte)(raw[p + i] + left);
                    }
                    break;
                case 2:
                    for (int i = 0; i < stride; i++)
                    {
                        current[i] = (byte)(raw[p + i] + prev[i]);
                    }
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= filterBpp ? current[i - filterBpp] : (byte)0;
                        byte up = prev[i];
                        current[i] = (byte)(raw[p + i] + ((left + up) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= filterBpp ? current[i - filterBpp] : (byte)0;
                        byte up = prev[i];
                        byte upLeft = i >= filterBpp ? prev[i - filterBpp] : (byte)0;
                        current[i] = (byte)(raw[p + i] + PaethPredictor(left, up, upLeft));
                    }
                    break;
                default:
                    return null;
            }
            p += stride;

            int outOffset = row * width * 4;
            ExpandToRgba(current, colorType, bitDepth, width, palette, palAlpha,
                buffer.Pixels, outOffset);
            (prev, current) = (current, prev);
        }
        return buffer;
    }

    private static void ExpandToRgba(
        byte[] current, int colorType, int bitDepth, int width,
        byte[]? palette, byte[]? palAlpha,
        byte[] dest, int destOffset)
    {
        switch (colorType)
        {
            case 0: // grayscale
                for (int x = 0; x < width; x++)
                {
                    byte g = current[x];
                    int d = destOffset + x * 4;
                    dest[d] = g;
                    dest[d + 1] = g;
                    dest[d + 2] = g;
                    dest[d + 3] = 255;
                }
                return;
            case 2: // RGB
                for (int x = 0; x < width; x++)
                {
                    int s = x * 3;
                    int d = destOffset + x * 4;
                    dest[d] = current[s];
                    dest[d + 1] = current[s + 1];
                    dest[d + 2] = current[s + 2];
                    dest[d + 3] = 255;
                }
                return;
            case 3: // palette
                if (palette is null) return;
                for (int x = 0; x < width; x++)
                {
                    int idx;
                    if (bitDepth == 8)
                    {
                        idx = current[x];
                    }
                    else
                    {
                        // Sub-byte packed indexes: most-significant
                        // bit first per spec §7.2.
                        int bitOffset = x * bitDepth;
                        int byteIdx = bitOffset >> 3;
                        int shift = 8 - bitDepth - (bitOffset & 7);
                        int mask = (1 << bitDepth) - 1;
                        idx = (current[byteIdx] >> shift) & mask;
                    }
                    int pStart = idx * 3;
                    if (pStart + 2 >= palette.Length) continue;
                    int d = destOffset + x * 4;
                    dest[d] = palette[pStart];
                    dest[d + 1] = palette[pStart + 1];
                    dest[d + 2] = palette[pStart + 2];
                    dest[d + 3] = (palAlpha is not null && idx < palAlpha.Length)
                        ? palAlpha[idx] : (byte)255;
                }
                return;
            case 4: // grayscale + alpha
                for (int x = 0; x < width; x++)
                {
                    int s = x * 2;
                    byte g = current[s];
                    int d = destOffset + x * 4;
                    dest[d] = g;
                    dest[d + 1] = g;
                    dest[d + 2] = g;
                    dest[d + 3] = current[s + 1];
                }
                return;
            case 6: // RGBA
                Buffer.BlockCopy(current, 0, dest, destOffset, width * 4);
                return;
        }
    }

    /// <summary>The Paeth predictor from PNG spec §9.4.</summary>
    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static bool HasPngSignature(byte[] data) =>
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private static int ReadU32(byte[] data, int p) =>
        (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];

    private static string ReadAscii4(byte[] data, int p) =>
        new string(new[] { (char)data[p], (char)data[p + 1], (char)data[p + 2], (char)data[p + 3] });
}
