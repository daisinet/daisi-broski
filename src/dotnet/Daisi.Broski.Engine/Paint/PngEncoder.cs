using System.IO.Compression;

namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Hand-rolled PNG encoder for <see cref="RasterBuffer"/>
/// instances. Writes a minimum-conforming PNG: 8-bit
/// truecolor + alpha, no interlacing, no ancillary
/// chunks. The IDAT scanlines are ZLib-compressed via the
/// BCL's <see cref="ZLibStream"/> (which writes the
/// 2-byte zlib header + adler32 trailer the spec requires).
///
/// <para>
/// Built BCL-only because product code can't take NuGet
/// dependencies. The two pieces the BCL doesn't ship are
/// the CRC32 used per chunk and the row-filter selector;
/// both are implemented inline.
/// </para>
/// </summary>
public static class PngEncoder
{
    public static byte[] Encode(RasterBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Width <= 0 || buffer.Height <= 0)
        {
            throw new InvalidOperationException(
                "Cannot encode a zero-area RasterBuffer to PNG.");
        }
        using var ms = new MemoryStream();
        WriteSignature(ms);
        WriteIhdr(ms, buffer.Width, buffer.Height);
        WriteIdat(ms, buffer);
        WriteIend(ms);
        return ms.ToArray();
    }

    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    private static void WriteSignature(Stream s) =>
        s.Write(PngSignature, 0, PngSignature.Length);

    private static void WriteIhdr(Stream s, int width, int height)
    {
        // IHDR layout per spec §11.2.2: width (u32), height
        // (u32), bit depth (u8), color type (u8),
        // compression (u8 = 0), filter (u8 = 0), interlace
        // (u8 = 0).
        var data = new byte[13];
        WriteU32(data, 0, (uint)width);
        WriteU32(data, 4, (uint)height);
        data[8] = 8;  // 8 bits per channel
        data[9] = 6;  // truecolor + alpha
        data[10] = 0; // deflate compression
        data[11] = 0; // adaptive filtering
        data[12] = 0; // no interlace
        WriteChunk(s, "IHDR", data);
    }

    private static void WriteIdat(Stream s, RasterBuffer buffer)
    {
        // Build the filtered scanline stream: one byte per row
        // for the filter type (0 = None — we don't bother
        // picking smart filters in v1) followed by the raw
        // RGBA bytes.
        int stride = buffer.Width * 4;
        using var raw = new MemoryStream((stride + 1) * buffer.Height);
        for (int row = 0; row < buffer.Height; row++)
        {
            raw.WriteByte(0); // filter type: None
            raw.Write(buffer.Pixels, row * stride, stride);
        }
        // Compress via zlib (DEFLATE + zlib wrapper).
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }
        WriteChunk(s, "IDAT", compressed.ToArray());
    }

    private static void WriteIend(Stream s) =>
        WriteChunk(s, "IEND", Array.Empty<byte>());

    /// <summary>Emit one PNG chunk: 4-byte length, 4-byte
    /// type, payload bytes, 4-byte CRC32 over (type +
    /// payload). The CRC excludes the length field per
    /// spec §5.3.</summary>
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        if (type.Length != 4) throw new ArgumentException("PNG chunk type must be 4 chars");
        var lenBytes = new byte[4];
        WriteU32(lenBytes, 0, (uint)data.Length);
        s.Write(lenBytes, 0, 4);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        var crc = Crc32.Compute(typeBytes, 0, 4);
        crc = Crc32.Update(crc, data, 0, data.Length);
        crc ^= 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteU32(crcBytes, 0, crc);
        s.Write(crcBytes, 0, 4);
    }

    private static void WriteU32(byte[] dst, int offset, uint value)
    {
        // PNG uses big-endian everywhere.
        dst[offset] = (byte)((value >> 24) & 0xFF);
        dst[offset + 1] = (byte)((value >> 16) & 0xFF);
        dst[offset + 2] = (byte)((value >> 8) & 0xFF);
        dst[offset + 3] = (byte)(value & 0xFF);
    }
}

/// <summary>CRC32 (IEEE 802.3 polynomial, the variant PNG
/// uses). Hand-rolled because <c>System.IO.Hashing.Crc32</c>
/// is a NuGet package and product code is BCL-only. The
/// table is built once per process.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    public static uint Compute(byte[] buffer, int offset, int length)
    {
        return Update(0xFFFFFFFFu, buffer, offset, length);
    }

    public static uint Update(uint crc, byte[] buffer, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            crc = Table[(crc ^ buffer[offset + i]) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }
}
