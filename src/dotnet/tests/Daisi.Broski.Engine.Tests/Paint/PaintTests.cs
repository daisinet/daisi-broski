using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Daisi.Broski.Engine.Paint;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6f — paint walks. Verifies that the painter fills
/// background-color rects, draws borders per side, encodes
/// to a valid PNG header, and respects the canvas-color
/// fallback chain. Pixel-level fidelity tests check a
/// handful of key pixels rather than diffing whole images
/// so the suite stays small.
/// </summary>
public class PaintTests
{
    private static (LayoutBox root, RasterBuffer raster) PaintHtml(
        string html, int width = 100, int height = 100)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        var viewport = new Viewport { Width = width, Height = height };
        var root = LayoutTree.Build(doc, viewport);
        var raster = Painter.Paint(root, doc, viewport);
        return (root, raster);
    }

    private static PaintColor PixelAt(RasterBuffer buffer, int x, int y)
    {
        int idx = (y * buffer.Width + x) * 4;
        return new PaintColor(
            buffer.Pixels[idx], buffer.Pixels[idx + 1],
            buffer.Pixels[idx + 2], buffer.Pixels[idx + 3]);
    }

    // ---------------------------------------------------------
    // CssColor parsing
    // ---------------------------------------------------------

    [Fact]
    public void Hex_three_digit_expands()
    {
        var c = CssColor.Parse("#f00");
        Assert.Equal(255, c.R); Assert.Equal(0, c.G); Assert.Equal(0, c.B);
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void Hex_six_digit_parses()
    {
        var c = CssColor.Parse("#3366cc");
        Assert.Equal(0x33, c.R); Assert.Equal(0x66, c.G); Assert.Equal(0xCC, c.B);
    }

    [Fact]
    public void Rgb_function()
    {
        var c = CssColor.Parse("rgb(10, 20, 30)");
        Assert.Equal(10, c.R); Assert.Equal(20, c.G); Assert.Equal(30, c.B);
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void Rgba_function_with_alpha()
    {
        var c = CssColor.Parse("rgba(255, 0, 0, 0.5)");
        Assert.Equal(255, c.R);
        Assert.True(c.A > 100 && c.A < 200);
    }

    [Fact]
    public void Named_colors_resolve()
    {
        Assert.Equal(255, CssColor.Parse("red").R);
        Assert.Equal(255, CssColor.Parse("white").R);
        Assert.Equal(0, CssColor.Parse("black").R);
        Assert.Equal(0, CssColor.Parse("transparent").A);
    }

    [Fact]
    public void Garbage_input_gives_transparent()
    {
        Assert.True(CssColor.Parse("not-a-color").IsTransparent);
        Assert.True(CssColor.Parse("").IsTransparent);
    }

    // ---------------------------------------------------------
    // RasterBuffer
    // ---------------------------------------------------------

    [Fact]
    public void FillRect_sets_pixels_inside_the_rect()
    {
        var b = new RasterBuffer(10, 10);
        b.FillRect(2, 3, 4, 5, new PaintColor(100, 150, 200, 255));
        var p = PixelAt(b, 2, 3);
        Assert.Equal(100, p.R);
        Assert.Equal(150, p.G);
        Assert.Equal(200, p.B);
        // Outside the rect: untouched (alpha 0).
        Assert.Equal(0, PixelAt(b, 0, 0).A);
    }

    [Fact]
    public void FillRect_clips_to_buffer_bounds()
    {
        var b = new RasterBuffer(10, 10);
        b.FillRect(-5, -5, 100, 100, PaintColor.Black);
        Assert.Equal(0, PixelAt(b, 0, 0).R);
        Assert.Equal(255, PixelAt(b, 0, 0).A);
    }

    [Fact]
    public void StrokeRect_paints_each_side()
    {
        var b = new RasterBuffer(10, 10);
        b.StrokeRect(0, 0, 10, 10, 1, 1, 1, 1,
            PaintColor.Black, PaintColor.Black,
            PaintColor.Black, PaintColor.Black);
        // Top-left corner is on the top edge.
        Assert.Equal(255, PixelAt(b, 0, 0).A);
        // Center is interior — untouched.
        Assert.Equal(0, PixelAt(b, 5, 5).A);
    }

    // ---------------------------------------------------------
    // PNG encoder
    // ---------------------------------------------------------

    [Fact]
    public void Png_encoded_buffer_has_valid_signature()
    {
        var b = new RasterBuffer(4, 4, PaintColor.Black);
        var png = PngEncoder.Encode(b);
        Assert.True(png.Length > 0);
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
        Assert.Equal(0x0D, png[4]);
        Assert.Equal(0x0A, png[5]);
    }

    [Fact]
    public void Png_contains_an_IDAT_chunk()
    {
        var b = new RasterBuffer(4, 4, PaintColor.Black);
        var png = PngEncoder.Encode(b);
        // Search for ASCII "IDAT" in the byte stream.
        bool found = false;
        for (int i = 0; i < png.Length - 4; i++)
        {
            if (png[i] == 'I' && png[i + 1] == 'D' &&
                png[i + 2] == 'A' && png[i + 3] == 'T')
            {
                found = true; break;
            }
        }
        Assert.True(found, "PNG output missing IDAT chunk");
    }

    [Fact]
    public void Png_ends_with_IEND_chunk()
    {
        var b = new RasterBuffer(4, 4, PaintColor.Black);
        var png = PngEncoder.Encode(b);
        // Last 8 bytes: chunk type (IEND) + 4-byte CRC.
        // Layout per spec: 4 bytes length, 4 bytes type,
        // 0 bytes data, 4 bytes CRC. So IEND name is at
        // png[^8..^4].
        Assert.Equal((byte)'I', png[^8]);
        Assert.Equal((byte)'E', png[^7]);
        Assert.Equal((byte)'N', png[^6]);
        Assert.Equal((byte)'D', png[^5]);
    }

    // ---------------------------------------------------------
    // Painter integration
    // ---------------------------------------------------------

    [Fact]
    public void Body_background_color_fills_canvas()
    {
        var (_, raster) = PaintHtml("""
            <html><head><style>
              body { background-color: red; margin: 0; }
            </style></head><body></body></html>
            """);
        var p = PixelAt(raster, 50, 50);
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Default_canvas_is_white_when_no_background_set()
    {
        var (_, raster) = PaintHtml("<html><body></body></html>");
        var p = PixelAt(raster, 50, 50);
        Assert.Equal(255, p.R);
        Assert.Equal(255, p.G);
        Assert.Equal(255, p.B);
    }

    [Fact]
    public void Element_background_paints_inside_its_box()
    {
        var (_, raster) = PaintHtml("""
            <html><head><style>
              body { margin: 0; }
              #x { width: 50px; height: 50px;
                   background-color: blue; }
            </style></head>
            <body><div id="x"></div></body></html>
            """);
        // Pixel inside the box is blue.
        Assert.Equal(0, PixelAt(raster, 10, 10).R);
        Assert.Equal(0, PixelAt(raster, 10, 10).G);
        Assert.Equal(255, PixelAt(raster, 10, 10).B);
        // Pixel outside the box (lower-right area) is white
        // (canvas default).
        Assert.Equal(255, PixelAt(raster, 80, 80).R);
    }

    [Fact]
    public void Border_paints_on_the_specified_side()
    {
        var (_, raster) = PaintHtml("""
            <html><head><style>
              body { margin: 0; }
              #x { width: 50px; height: 50px; background-color: white;
                   border: 5px solid red; }
            </style></head>
            <body><div id="x"></div></body></html>
            """);
        // Top-left of the border-box: should be red (border).
        // Border width 5 → x in [0,5) is red, x in [5, 60) inside the box.
        Assert.Equal(255, PixelAt(raster, 0, 0).R);
        Assert.Equal(0, PixelAt(raster, 0, 0).G);
        // Center of the box: white background.
        Assert.Equal(255, PixelAt(raster, 30, 30).R);
        Assert.Equal(255, PixelAt(raster, 30, 30).G);
        Assert.Equal(255, PixelAt(raster, 30, 30).B);
    }
}
