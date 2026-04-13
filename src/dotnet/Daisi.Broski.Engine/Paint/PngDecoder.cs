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
                    // data[p+10] = compression (must be 0)
                    // data[p+11] = filter method (must be 0)
                    interlace = data[p + 12];
                }
                else if (type == "IDAT")
                {
                    idatStream.Write(data, p, len);
                }
                else if (type == "IEND")
                {
                    break;
                }
                p += len + 4; // payload + CRC (we trust it; mismatched CRC just means corruption)
            }

            if (width <= 0 || height <= 0) return null;
            if (bitDepth != 8) return null;
            if (colorType != 2 && colorType != 6) return null;
            if (interlace != 0) return null;

            int channels = colorType == 6 ? 4 : 3;
            int stride = width * channels;
            int totalRaw = (stride + 1) * height; // +1 filter byte per row

            // Inflate IDAT (zlib-wrapped DEFLATE) into the
            // raw filtered scanline bytes.
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

            return Unfilter(raw, width, height, channels);
        }
        catch
        {
            // Decoder errors (truncated IDAT, bad zlib, etc.)
            // surface as null rather than propagating —
            // a broken image shouldn't abort the render.
            return null;
        }
    }

    /// <summary>Apply PNG row filters in reverse and pack
    /// into a <see cref="RasterBuffer"/> (RGBA8888). For
    /// truecolor (RGB) inputs, alpha is set to 255.</summary>
    private static RasterBuffer Unfilter(byte[] raw, int width, int height, int channels)
    {
        int stride = width * channels;
        var prev = new byte[stride];
        var current = new byte[stride];
        var buffer = new RasterBuffer(width, height);

        int p = 0;
        for (int row = 0; row < height; row++)
        {
            byte filter = raw[p++];
            // Each row starts with the filter byte, then
            // `stride` bytes of filtered pixel data.
            switch (filter)
            {
                case 0: // None
                    Buffer.BlockCopy(raw, p, current, 0, stride);
                    break;
                case 1: // Sub
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= channels ? current[i - channels] : (byte)0;
                        current[i] = (byte)(raw[p + i] + left);
                    }
                    break;
                case 2: // Up
                    for (int i = 0; i < stride; i++)
                    {
                        current[i] = (byte)(raw[p + i] + prev[i]);
                    }
                    break;
                case 3: // Average
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= channels ? current[i - channels] : (byte)0;
                        byte up = prev[i];
                        current[i] = (byte)(raw[p + i] + ((left + up) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= channels ? current[i - channels] : (byte)0;
                        byte up = prev[i];
                        byte upLeft = i >= channels ? prev[i - channels] : (byte)0;
                        current[i] = (byte)(raw[p + i] + PaethPredictor(left, up, upLeft));
                    }
                    break;
                default:
                    return buffer; // unknown filter — bail
            }
            p += stride;

            // Pack into RGBA in the output buffer.
            int outOffset = row * width * 4;
            if (channels == 4)
            {
                Buffer.BlockCopy(current, 0, buffer.Pixels, outOffset, stride);
            }
            else // RGB → RGBA
            {
                for (int x = 0; x < width; x++)
                {
                    int src = x * 3;
                    int dst = outOffset + x * 4;
                    buffer.Pixels[dst] = current[src];
                    buffer.Pixels[dst + 1] = current[src + 1];
                    buffer.Pixels[dst + 2] = current[src + 2];
                    buffer.Pixels[dst + 3] = 255;
                }
            }
            (prev, current) = (current, prev);
        }
        return buffer;
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
