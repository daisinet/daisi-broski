using System.Reflection;
using Daisi.Broski.Engine.Paint;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6.x — smoke tests for
/// <see cref="JpegDecoder"/>. We don't ship a JPEG encoder,
/// so positive checks use a small pre-encoded fixture
/// embedded in the test assembly. Negative checks confirm
/// the decoder bails out gracefully on garbage.
/// </summary>
public class JpegDecoderTests
{
    [Fact]
    public void Decode_fixture_returns_expected_size()
    {
        var bytes = LoadFixture("TestFixture.jpg");
        var decoded = JpegDecoder.TryDecode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded!.Width);
        Assert.Equal(16, decoded.Height);
        // Every pixel should be fully opaque — JFIF has no
        // alpha channel, so the decoder fills with 255.
        for (int i = 3; i < decoded.Pixels.Length; i += 4)
        {
            Assert.Equal(255, decoded.Pixels[i]);
        }
    }

    [Fact]
    public void Decode_fixture_picks_up_yellow_center()
    {
        // Fixture is a magenta/pink background with a
        // yellow 8×8 block at (4,4)..(11,11). JPEG is
        // lossy so we allow per-channel wiggle — we just
        // need to see the center is much yellower than the
        // corners.
        var bytes = LoadFixture("TestFixture.jpg");
        var decoded = JpegDecoder.TryDecode(bytes);
        Assert.NotNull(decoded);

        var center = GetPixel(decoded!, 8, 8);
        var corner = GetPixel(decoded!, 0, 0);
        // Yellow → R high, G high, B low.
        Assert.True(center.R > 150, $"center R {center.R}");
        Assert.True(center.G > 150, $"center G {center.G}");
        Assert.True(center.B < 120, $"center B {center.B}");
        // Magenta-ish background → B should be higher
        // than the yellow center.
        Assert.True(corner.B > center.B);
    }

    [Fact]
    public void Decode_returns_null_on_non_JPEG_data()
    {
        Assert.Null(JpegDecoder.TryDecode(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(JpegDecoder.TryDecode(System.Text.Encoding.UTF8.GetBytes("not a jpeg")));
        Assert.Null(JpegDecoder.TryDecode(Array.Empty<byte>()));
        // PNG signature — decoder should reject, not crash.
        Assert.Null(JpegDecoder.TryDecode(new byte[]
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    [Fact]
    public void Decode_returns_null_on_truncated_JPEG()
    {
        var bytes = LoadFixture("TestFixture.jpg");
        // Drop everything after the SOI — decoder should
        // handle a partial file without blowing up.
        var truncated = bytes[..2];
        Assert.Null(JpegDecoder.TryDecode(truncated));
    }

    [Fact]
    public void Decode_returns_null_on_progressive_JPEG()
    {
        // Flip the SOF0 marker (0xC0) to SOF2 (0xC2) to
        // simulate a progressive JPEG. We don't support
        // progressive mode — the decoder should bail out
        // rather than crash or produce garbage.
        var bytes = LoadFixture("TestFixture.jpg");
        var progressive = (byte[])bytes.Clone();
        for (int i = 0; i < progressive.Length - 1; i++)
        {
            if (progressive[i] == 0xFF && progressive[i + 1] == 0xC0)
            {
                progressive[i + 1] = 0xC2;
                break;
            }
        }
        Assert.Null(JpegDecoder.TryDecode(progressive));
    }

    [Fact]
    public void ImageDecoder_dispatches_to_JPEG_and_PNG()
    {
        var jpeg = LoadFixture("TestFixture.jpg");
        Assert.NotNull(ImageDecoder.TryDecode(jpeg));

        // Round-trip a trivial PNG to check PNG dispatch.
        var src = new RasterBuffer(4, 4);
        src.Clear(new PaintColor(10, 20, 30, 255));
        var png = PngEncoder.Encode(src);
        Assert.NotNull(ImageDecoder.TryDecode(png));

        // Random bytes → null.
        Assert.Null(ImageDecoder.TryDecode(new byte[] { 0, 1, 2, 3 }));
    }

    private static byte[] LoadFixture(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Default resource naming convention is
        // <DefaultNamespace>.<Path>.<Name> with directory
        // separators becoming dots.
        var resource = $"Daisi.Broski.Engine.Tests.Paint.{name}";
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{resource}' missing. Available: " +
                string.Join(", ", asm.GetManifestResourceNames()));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static (byte R, byte G, byte B, byte A) GetPixel(RasterBuffer b, int x, int y)
    {
        int i = (y * b.Width + x) * 4;
        return (b.Pixels[i], b.Pixels[i + 1], b.Pixels[i + 2], b.Pixels[i + 3]);
    }
}
