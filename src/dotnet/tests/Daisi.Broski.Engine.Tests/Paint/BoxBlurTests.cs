using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Daisi.Broski.Engine.Paint;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6.x — tests for <see cref="BoxBlur"/> and the
/// <c>backdrop-filter: blur(Npx)</c> painter integration
/// built on it. Covers the primitive itself (does it
/// average neighboring pixels?), region clipping (does a
/// blur respect its rectangle?), and end-to-end painting
/// (does a glassmorphic card actually pick up blurred
/// pixels from the background it sits on?).
/// </summary>
public class BoxBlurTests
{
    private static PaintColor PixelAt(RasterBuffer b, int x, int y)
    {
        int i = (y * b.Width + x) * 4;
        return new PaintColor(b.Pixels[i], b.Pixels[i + 1],
            b.Pixels[i + 2], b.Pixels[i + 3]);
    }

    [Fact]
    public void Blur_averages_a_black_white_boundary()
    {
        // Checkerboard halves: left half black, right half
        // white. After blur with radius 2, the pixels near
        // the seam should be gray (not pure black or pure
        // white) because the kernel samples across the
        // boundary.
        var buf = new RasterBuffer(20, 5);
        buf.FillRect(0, 0, 10, 5, new PaintColor(0, 0, 0, 255));
        buf.FillRect(10, 0, 10, 5, new PaintColor(255, 255, 255, 255));

        BoxBlur.BlurRegion(buf, 0, 0, 20, 5, radius: 2);

        // Seam column (x=10) should be mid-gray-ish after
        // averaging over 5 pixels {B,B,W,W,W} = 3/5 * 255.
        var seam = PixelAt(buf, 10, 2);
        Assert.InRange((int)seam.R, 100, 200);
        Assert.InRange((int)seam.G, 100, 200);
        Assert.InRange((int)seam.B, 100, 200);
    }

    [Fact]
    public void Blur_leaves_pixels_outside_region_untouched()
    {
        // Blur only the inner 4x4 of an 8x8 buffer. The
        // outer border should keep its sentinel color
        // exactly — nothing bleeds in from the blurred
        // interior.
        var buf = new RasterBuffer(8, 8);
        buf.Clear(new PaintColor(10, 20, 30, 255));
        // Paint a bright square inside so blur has something
        // to smear.
        buf.FillRect(3, 3, 2, 2, new PaintColor(200, 200, 200, 255));

        BoxBlur.BlurRegion(buf, 2, 2, 4, 4, radius: 1);

        // (0, 0) is outside the blur region — must keep
        // the original sentinel.
        var untouched = PixelAt(buf, 0, 0);
        Assert.Equal(10, untouched.R);
        Assert.Equal(20, untouched.G);
        Assert.Equal(30, untouched.B);
    }

    [Fact]
    public void Blur_radius_zero_is_noop()
    {
        var buf = new RasterBuffer(4, 4);
        buf.FillRect(1, 1, 2, 2, new PaintColor(255, 0, 0, 255));
        var before = (byte[])buf.Pixels.Clone();
        BoxBlur.BlurRegion(buf, 0, 0, 4, 4, radius: 0);
        Assert.Equal(before, buf.Pixels);
    }

    [Fact]
    public void Backdrop_filter_blurs_pixels_under_element()
    {
        // A black/white split underneath a centered card.
        // Without backdrop-filter, the card's pixels stay
        // the solid color of whichever half they land on.
        // With backdrop-filter: blur(), the seam becomes
        // gray inside the card because the blur averaged
        // across it.
        var html = """
            <html><head><style>
              body { margin: 0; padding: 0; }
              .canvas { width: 200px; height: 100px; position: relative; }
              .left  { position: absolute; left: 0;   top: 0; width: 100px; height: 100px; background: black; }
              .right { position: absolute; left: 100px; top: 0; width: 100px; height: 100px; background: white; }
              .card  { position: absolute; left: 60px;  top: 20px; width: 80px; height: 60px;
                       backdrop-filter: blur(6px); }
            </style></head>
            <body><div class="canvas">
              <div class="left"></div><div class="right"></div>
              <div class="card"></div>
            </div></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var viewport = new Viewport { Width = 200, Height = 100 };
        var root = LayoutTree.Build(doc, viewport);
        var raster = Painter.Paint(root, doc, viewport);

        // The seam is at x=100. Inside the card, close to
        // the seam, we should see a smooth gradient instead
        // of a hard black/white boundary.
        var nearLeft = PixelAt(raster, 95, 50);
        var nearRight = PixelAt(raster, 105, 50);
        // At least one of these should be mid-gray after
        // blur — the exact pixel that crosses the seam
        // depends on border-box rounding. We just need
        // evidence the blur ran (neither pure 0 nor pure
        // 255 at both points).
        bool leftBlurred = nearLeft.R > 20 && nearLeft.R < 235;
        bool rightBlurred = nearRight.R > 20 && nearRight.R < 235;
        Assert.True(leftBlurred || rightBlurred,
            $"no blur evidence: nearLeft.R={nearLeft.R} nearRight.R={nearRight.R}");
    }

    [Fact]
    public void Backdrop_filter_zero_radius_is_noop()
    {
        // blur(0px) must not disturb pixels — some pages
        // toggle blur on/off by animating the radius and
        // a zero radius should be a true pass-through.
        var html = """
            <html><head><style>
              body { margin: 0; }
              .bg { width: 40px; height: 40px; background: red; }
              .card { position: absolute; top: 10px; left: 10px;
                      width: 20px; height: 20px;
                      backdrop-filter: blur(0px); }
            </style></head>
            <body><div class="bg"></div><div class="card"></div></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var viewport = new Viewport { Width = 40, Height = 40 };
        var root = LayoutTree.Build(doc, viewport);
        var raster = Painter.Paint(root, doc, viewport);

        // Dead center of the red background, under the
        // card with zero-radius blur — should still be
        // red.
        var center = PixelAt(raster, 20, 20);
        Assert.True(center.R > 200, $"center R={center.R} — blur(0) disturbed pixels");
    }

    [Fact]
    public void Backdrop_filter_respects_border_radius_mask()
    {
        // A circular card (border-radius: 50%) over a
        // striped background. The four corners of the
        // card's bounding box are outside the circle — the
        // blur must NOT apply there, so those corner
        // pixels keep their original color.
        var html = """
            <html><head><style>
              body { margin: 0; }
              .bg { width: 60px; height: 60px; background: black; }
              .card { position: absolute; top: 0; left: 0;
                      width: 60px; height: 60px; border-radius: 50%;
                      backdrop-filter: blur(8px); }
            </style></head>
            <body><div class="bg"></div><div class="card"></div></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var viewport = new Viewport { Width = 60, Height = 60 };
        var root = LayoutTree.Build(doc, viewport);
        var raster = Painter.Paint(root, doc, viewport);

        // The top-left corner (0, 0) is outside the
        // circle — it should be pure black (the original
        // bg), not a gray from blur-smeared-with-white-
        // canvas-behind.
        var corner = PixelAt(raster, 0, 0);
        Assert.Equal(0, corner.R);
        Assert.Equal(0, corner.G);
        Assert.Equal(0, corner.B);
    }

    [Fact]
    public void Webkit_backdrop_filter_prefix_is_accepted()
    {
        // Many sites still ship the -webkit- prefixed form
        // because Safari needed it until recently. Both
        // should drive the same behavior.
        var html = """
            <html><head><style>
              body { margin: 0; }
              .bg { width: 40px; height: 40px; background: black; }
              .c { position: absolute; top: 0; left: 0;
                   width: 40px; height: 40px;
                   -webkit-backdrop-filter: blur(4px); }
            </style></head>
            <body><div class="bg"></div><div class="c"></div></body></html>
            """;
        var doc = HtmlTreeBuilder.Parse(html);
        var viewport = new Viewport { Width = 40, Height = 40 };
        var root = LayoutTree.Build(doc, viewport);
        // Should not throw; if the vendor prefix wasn't
        // recognized we'd still render (as no-op) but the
        // blur-then-no-op code path shouldn't error either.
        var _ = Painter.Paint(root, doc, viewport);
    }
}
