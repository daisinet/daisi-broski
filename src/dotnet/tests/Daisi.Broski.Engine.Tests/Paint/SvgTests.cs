using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Layout;
using Daisi.Broski.Engine.Paint;
using Daisi.Broski.Engine.Paint.Svg;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6n — inline SVG rendering. Tests pixel-sample each
/// shape at a known coordinate and assert the fill color
/// (or background) at that point. Keeps the suite small
/// while verifying the scanline filler, path parser, and
/// viewBox transform all agree on geometry.
/// </summary>
public class SvgTests
{
    private static PaintColor PixelAt(RasterBuffer buf, int x, int y)
    {
        int idx = (y * buf.Width + x) * 4;
        return new PaintColor(buf.Pixels[idx], buf.Pixels[idx + 1],
            buf.Pixels[idx + 2], buf.Pixels[idx + 3]);
    }

    // ---------------------------------------------------------
    // Path parser (geometry, not rasterization)
    // ---------------------------------------------------------

    [Fact]
    public void Path_parses_simple_closed_triangle()
    {
        var sub = SvgPath.Parse("M 0 0 L 10 0 L 5 10 Z");
        Assert.Single(sub);
        // M + 2 L's + Z close → 4 vertices (the Z appends the
        // start).
        Assert.Equal(4, sub[0].Count);
        Assert.Equal((0, 0), sub[0][0]);
        Assert.Equal((10d, 0d), sub[0][1]);
        Assert.Equal((5d, 10d), sub[0][2]);
        Assert.Equal((0d, 0d), sub[0][3]); // close
    }

    [Fact]
    public void Path_handles_relative_commands()
    {
        // m 10,10 l 5,0 → absolute (10,10) then (15,10).
        var sub = SvgPath.Parse("m 10 10 l 5 0 l 0 5 z");
        Assert.Single(sub);
        Assert.Equal((10d, 10d), sub[0][0]);
        Assert.Equal((15d, 10d), sub[0][1]);
        Assert.Equal((15d, 15d), sub[0][2]);
    }

    [Fact]
    public void Path_flattens_cubic_bezier_into_many_vertices()
    {
        var sub = SvgPath.Parse("M 0 0 C 10 0 10 10 0 10");
        Assert.Single(sub);
        // 1 start + 16 samples from the cubic = 17.
        Assert.Equal(17, sub[0].Count);
    }

    [Fact]
    public void Path_tokenizer_splits_implicit_number_boundaries()
    {
        // "1.5.5" is two numbers (1.5 and .5) — classic
        // minified-SVG idiom.
        var sub = SvgPath.Parse("M 1.5.5 L 2 2");
        Assert.Single(sub);
        Assert.Equal((1.5, 0.5), sub[0][0]);
    }

    // ---------------------------------------------------------
    // Rendering — pixel-sample after paint
    // ---------------------------------------------------------

    /// <summary>Render SVG fragment directly via the
    /// renderer at a known (x, y, w, h) into a white buffer —
    /// bypasses the HTML+layout path so pixel assertions
    /// exercise the rasterizer, not the inline-flow math.</summary>
    private static RasterBuffer RenderSvgFragment(
        string svgMarkup, int w = 50, int h = 50)
    {
        var html = $"<html><body>{svgMarkup}</body></html>";
        var doc = HtmlTreeBuilder.Parse(html);
        var svg = FindSvg(doc.DocumentElement!);
        Assert.NotNull(svg);
        int svgW = int.Parse(svg!.GetAttribute("width") ?? w.ToString(),
            System.Globalization.CultureInfo.InvariantCulture);
        int svgH = int.Parse(svg.GetAttribute("height") ?? h.ToString(),
            System.Globalization.CultureInfo.InvariantCulture);
        var buf = new RasterBuffer(w, h, PaintColor.White);
        SvgRenderer.Render(svg, 0, 0, svgW, svgH, buf);
        return buf;
    }

    private static Daisi.Broski.Engine.Dom.Element? FindSvg(Daisi.Broski.Engine.Dom.Element root)
    {
        if (root.TagName == "svg") return root;
        foreach (var child in root.Children)
        {
            var hit = FindSvg(child);
            if (hit is not null) return hit;
        }
        return null;
    }

    [Fact]
    public void Rect_fills_with_red()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" />" +
            "</svg>");
        var p = PixelAt(buf, 5, 5);
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Circle_fills_center_pixel()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"40\" height=\"40\">" +
            "<circle cx=\"20\" cy=\"20\" r=\"18\" fill=\"#0080ff\" />" +
            "</svg>");
        var p = PixelAt(buf, 20, 20);
        Assert.Equal(0, p.R);
        Assert.Equal(0x80, p.G);
        Assert.Equal(0xFF, p.B);
    }

    [Fact]
    public void Circle_outside_radius_is_background()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"40\" height=\"40\">" +
            "<circle cx=\"20\" cy=\"20\" r=\"5\" fill=\"red\" />" +
            "</svg>");
        // Pixel (0,0) is well outside the circle — should be
        // the buffer's white background.
        var p = PixelAt(buf, 0, 0);
        Assert.Equal(255, p.R);
        Assert.Equal(255, p.G);
        Assert.Equal(255, p.B);
    }

    [Fact]
    public void ViewBox_scales_geometry_into_target_rect()
    {
        // viewBox 0-10, svg sized 40x40. Rect fills the full
        // viewBox → fills the 40x40 svg → pixel (20, 20)
        // should be green.
        var buf = RenderSvgFragment(
            "<svg width=\"40\" height=\"40\" viewBox=\"0 0 10 10\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"green\" />" +
            "</svg>");
        var p = PixelAt(buf, 20, 20);
        Assert.Equal(0, p.R);
        Assert.Equal(128, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Path_triangle_fills_interior()
    {
        // Triangle from (0,0)→(40,0)→(20,40)→close. Pixel
        // (20, 20) is inside the triangle interior.
        var buf = RenderSvgFragment(
            "<svg width=\"40\" height=\"40\">" +
            "<path d=\"M 0 0 L 40 0 L 20 40 Z\" fill=\"orange\" />" +
            "</svg>");
        var p = PixelAt(buf, 20, 20);
        Assert.Equal(255, p.R);
        Assert.Equal(165, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Polygon_fill_respects_points_attribute()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"20\" height=\"20\">" +
            "<polygon points=\"0,0 20,0 20,20 0,20\" fill=\"#112233\" />" +
            "</svg>");
        var p = PixelAt(buf, 10, 10);
        Assert.Equal(0x11, p.R);
        Assert.Equal(0x22, p.G);
        Assert.Equal(0x33, p.B);
    }

    [Fact]
    public void Group_cascades_fill_to_children()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"20\" height=\"20\">" +
            "<g fill=\"red\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" />" +
            "</g></svg>");
        var p = PixelAt(buf, 10, 10);
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Fill_none_leaves_background_visible()
    {
        var buf = RenderSvgFragment(
            "<svg width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"none\" />" +
            "</svg>");
        var p = PixelAt(buf, 10, 10);
        Assert.Equal(255, p.R);
        Assert.Equal(255, p.G);
        Assert.Equal(255, p.B);
    }

    [Fact]
    public void Fill_opacity_blends_with_background()
    {
        // 50% opacity red on white: ~ (255, 128, 128).
        var buf = RenderSvgFragment(
            "<svg width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" " +
              "fill=\"red\" fill-opacity=\"0.5\" />" +
            "</svg>");
        var p = PixelAt(buf, 10, 10);
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 120, 135);
        Assert.InRange(p.B, 120, 135);
    }
}
