using Daisi.Broski.Engine.Paint;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Paint;

/// <summary>
/// Phase 6m — linear gradient parsing + painting. The
/// painter samples are pixel-checked at known positions so
/// a regression in the angle math or the stop interpolation
/// surfaces immediately.
/// </summary>
public class GradientTests
{
    private static PaintColor PixelAt(RasterBuffer buf, int x, int y)
    {
        int idx = (y * buf.Width + x) * 4;
        return new PaintColor(buf.Pixels[idx], buf.Pixels[idx + 1],
            buf.Pixels[idx + 2], buf.Pixels[idx + 3]);
    }

    [Fact]
    public void Parse_simple_two_color_gradient()
    {
        var g = Gradient.TryParseLinear("linear-gradient(red, blue)");
        Assert.NotNull(g);
        Assert.Equal(180, g!.AngleDeg);
        Assert.Equal(2, g.Stops.Count);
        Assert.Equal(255, g.Stops[0].Color.R);
        Assert.Equal(255, g.Stops[1].Color.B);
    }

    [Fact]
    public void Parse_to_right_keyword()
    {
        var g = Gradient.TryParseLinear("linear-gradient(to right, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(90, g!.AngleDeg);
    }

    [Fact]
    public void Parse_diagonal_keyword()
    {
        var g = Gradient.TryParseLinear("linear-gradient(to bottom right, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(135, g!.AngleDeg);
    }

    [Fact]
    public void Parse_explicit_stop_positions()
    {
        var g = Gradient.TryParseLinear("linear-gradient(red 0%, blue 100%)");
        Assert.NotNull(g);
        Assert.Equal(0, g!.Stops[0].Position);
        Assert.Equal(1, g.Stops[1].Position);
    }

    [Fact]
    public void Paint_top_to_bottom_changes_color_per_row()
    {
        // 180deg = "to bottom" — top row red, bottom row blue.
        var buf = new RasterBuffer(10, 10);
        var g = Gradient.TryParseLinear("linear-gradient(red, blue)")!;
        Gradient.Paint(buf, 0, 0, 10, 10, g);
        var top = PixelAt(buf, 5, 0);
        var bottom = PixelAt(buf, 5, 9);
        Assert.True(top.R > top.B,
            $"top pixel should be redder: r={top.R} b={top.B}");
        Assert.True(bottom.B > bottom.R,
            $"bottom pixel should be bluer: r={bottom.R} b={bottom.B}");
    }

    [Fact]
    public void Paint_to_right_changes_color_per_column()
    {
        var buf = new RasterBuffer(10, 10);
        var g = Gradient.TryParseLinear("linear-gradient(to right, red, blue)")!;
        Gradient.Paint(buf, 0, 0, 10, 10, g);
        var left = PixelAt(buf, 0, 5);
        var right = PixelAt(buf, 9, 5);
        Assert.True(left.R > left.B);
        Assert.True(right.B > right.R);
    }

    [Fact]
    public void Paint_three_stop_gradient_picks_middle_color()
    {
        var buf = new RasterBuffer(10, 10);
        var g = Gradient.TryParseLinear(
            "linear-gradient(to right, red, lime, blue)")!;
        Gradient.Paint(buf, 0, 0, 10, 10, g);
        var middle = PixelAt(buf, 5, 5);
        // Center stop is lime → green dominates.
        Assert.True(middle.G > middle.R,
            $"middle should be greener than red: g={middle.G} r={middle.R}");
        Assert.True(middle.G > middle.B);
    }

    [Fact]
    public void IsLinearGradient_detects_inside_a_value()
    {
        Assert.True(Gradient.IsLinearGradient("linear-gradient(red, blue)"));
        Assert.True(Gradient.IsLinearGradient(
            "linear-gradient(45deg, rgba(0,0,0,0.5), transparent)"));
        Assert.False(Gradient.IsLinearGradient("red"));
        Assert.False(Gradient.IsLinearGradient(""));
    }
}
