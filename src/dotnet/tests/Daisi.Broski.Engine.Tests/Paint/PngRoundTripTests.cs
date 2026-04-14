using Daisi.Broski.Engine.Paint;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6l — round-trip the <see cref="PngEncoder"/> +
/// <see cref="PngDecoder"/> against synthetic buffers so a
/// regression on either side surfaces immediately. Real
/// fidelity tests would diff against known-good PNGs from
/// the spec test suite; this is the minimum-viable check
/// that the two halves agree with each other.
/// </summary>
public class PngRoundTripTests
{
    [Fact]
    public void Encode_then_decode_round_trips_pixels()
    {
        var src = new RasterBuffer(8, 6);
        // Diagonal stripe of red on a blue background so a
        // filter or scanline-order bug would change the
        // result obviously.
        src.Clear(new PaintColor(0, 0, 200, 255));
        for (int y = 0; y < 6; y++)
        {
            int x = (y * 8) / 6;
            src.FillRect(x, y, 1, 1, new PaintColor(255, 0, 0, 255));
        }

        var bytes = PngEncoder.Encode(src);
        var decoded = PngDecoder.TryDecode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(src.Width, decoded!.Width);
        Assert.Equal(src.Height, decoded.Height);
        Assert.Equal(src.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_handles_alpha_channel()
    {
        var src = new RasterBuffer(4, 4);
        src.Clear(new PaintColor(255, 255, 255, 255));
        src.FillRect(1, 1, 2, 2, new PaintColor(255, 0, 0, 128));
        var bytes = PngEncoder.Encode(src);
        var decoded = PngDecoder.TryDecode(bytes);
        Assert.NotNull(decoded);
        // The alpha-blended pixel at (1,1) is the
        // pre-blended result of FillRect, so the round-trip
        // value should match exactly.
        int idx = (1 * 4 + 1) * 4;
        Assert.Equal(src.Pixels[idx + 3], decoded!.Pixels[idx + 3]);
    }

    [Fact]
    public void Decode_returns_null_on_non_PNG_data()
    {
        Assert.Null(PngDecoder.TryDecode(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(PngDecoder.TryDecode(System.Text.Encoding.UTF8.GetBytes("hello world")));
        Assert.Null(PngDecoder.TryDecode(System.Array.Empty<byte>()));
    }
}
