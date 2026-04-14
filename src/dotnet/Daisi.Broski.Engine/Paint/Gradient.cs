using System.Globalization;

namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Pragmatic CSS linear-gradient parser + painter. Handles
/// the production patterns real marketing pages use:
/// <c>linear-gradient(45deg, red, blue)</c>,
/// <c>linear-gradient(to bottom right, #fff 0%, #000 100%)</c>,
/// <c>linear-gradient(to top, rgba(0,0,0,0.5), transparent)</c>.
///
/// <para>
/// Deferred:
/// <list type="bullet">
/// <item><c>radial-gradient()</c> / <c>conic-gradient()</c>
///   — different angle math; can layer in a follow-up
///   without touching the painter dispatch.</item>
/// <item><c>repeating-linear-gradient()</c> — modulo the
///   axis projection by the stop range.</item>
/// <item>Multiple stacked backgrounds via comma-separated
///   <c>background</c> values.</item>
/// <item>Color interpolation in spaces other than sRGB
///   (CSS Color 4's <c>in oklch</c> / <c>in display-p3</c>).</item>
/// </list>
/// </para>
/// </summary>
public static class Gradient
{
    /// <summary>True when <paramref name="value"/> looks
    /// like a linear-gradient declaration (potentially
    /// nested inside other tokens).</summary>
    public static bool IsLinearGradient(string value) =>
        !string.IsNullOrEmpty(value)
        && value.IndexOf("linear-gradient(", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Paint the gradient into <paramref name="buffer"/>
    /// across the rectangle (x, y, w, h). Stops outside the
    /// rect are ignored — every pixel receives the
    /// interpolated color at its position along the gradient
    /// axis.</summary>
    /// <summary>Paint a gradient clipped to a mask predicate.
    /// <paramref name="inside"/> receives box-local (lx, ly)
    /// coords and returns true when that pixel should be
    /// painted. Used by rounded-rect backgrounds so corners
    /// outside the rounded shape pass through.</summary>
    public static void PaintMasked(
        RasterBuffer buffer, int x, int y, int w, int h,
        ParsedLinearGradient gradient, Func<int, int, bool> inside)
    {
        if (w <= 0 || h <= 0) return;
        if (gradient.Stops.Count == 0) return;
        var stops = NormalizeStops(gradient.Stops);
        var (dx, dy) = DirectionVector(gradient.AngleDeg, w, h);
        double len = Math.Abs(dx * w) + Math.Abs(dy * h);
        if (len < 1) len = 1;
        double norm = Math.Sqrt(dx * dx + dy * dy);
        double udx = dx / norm;
        double udy = dy / norm;
        double originX = dx >= 0 ? 0 : w;
        double originY = dy >= 0 ? 0 : h;

        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(buffer.Width, x + w);
        int y1 = Math.Min(buffer.Height, y + h);
        for (int py = y0; py < y1; py++)
        {
            double localY = py - y - originY;
            for (int px = x0; px < x1; px++)
            {
                if (!inside(px - x, py - y)) continue;
                double localX = px - x - originX;
                double t = (localX * udx + localY * udy) / len;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                var color = SampleStops(stops, t);
                if (color.A == 0) continue;
                BlitPixel(buffer, px, py, color);
            }
        }
    }

    public static void Paint(
        RasterBuffer buffer, int x, int y, int w, int h, ParsedLinearGradient gradient)
    {
        if (w <= 0 || h <= 0) return;
        if (gradient.Stops.Count == 0) return;

        // Normalize stop positions to [0, 1].
        var stops = NormalizeStops(gradient.Stops);

        // Direction vector (dx, dy) in pixel space. The
        // gradient line spans the projection of the box's
        // diagonals onto this vector — see CSS Images
        // §3.1 for the formula. Rough-and-ready: for the
        // "to bottom right" case at angle 135deg, the line
        // touches both opposite corners.
        var (dx, dy) = DirectionVector(gradient.AngleDeg, w, h);
        // Gradient extent: project the box's four corners
        // onto the direction unit vector, take the spread.
        double len = Math.Abs(dx * w) + Math.Abs(dy * h);
        if (len < 1) len = 1;
        double udx = dx / Math.Sqrt(dx * dx + dy * dy);
        double udy = dy / Math.Sqrt(dx * dx + dy * dy);

        // Origin: project the corner whose projection is
        // smallest along the axis to position 0. For "to
        // bottom right" that's the top-left.
        double originX = dx >= 0 ? 0 : w;
        double originY = dy >= 0 ? 0 : h;

        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(buffer.Width, x + w);
        int y1 = Math.Min(buffer.Height, y + h);

        for (int py = y0; py < y1; py++)
        {
            double localY = py - y - originY;
            for (int px = x0; px < x1; px++)
            {
                double localX = px - x - originX;
                double t = (localX * udx + localY * udy) / len;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                var color = SampleStops(stops, t);
                if (color.A == 0) continue;
                BlitPixel(buffer, px, py, color);
            }
        }
    }

    /// <summary>Try to extract a linear-gradient declaration
    /// from a CSS value string. Returns null when the value
    /// doesn't contain a recognizable linear-gradient (or
    /// when parsing failed).</summary>
    public static ParsedLinearGradient? TryParseLinear(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int start = value.IndexOf("linear-gradient(", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int contentStart = start + "linear-gradient(".Length;
        int p = contentStart;
        int depth = 1;
        while (p < value.Length && depth > 0)
        {
            if (value[p] == '(') depth++;
            else if (value[p] == ')') depth--;
            if (depth > 0) p++;
        }
        if (p >= value.Length) return null;
        var inner = value.Substring(contentStart, p - contentStart);

        var args = SplitTopLevel(inner, ',');
        if (args.Count == 0) return null;

        // First token may be an angle / direction; otherwise
        // it's the first color stop.
        double angleDeg = 180; // CSS default = "to bottom"
        int firstStop = 0;
        if (TryParseAngle(args[0].Trim(), out var a))
        {
            angleDeg = a;
            firstStop = 1;
        }

        var stops = new List<GradientStop>();
        for (int i = firstStop; i < args.Count; i++)
        {
            if (TryParseStop(args[i].Trim(), out var stop))
            {
                stops.Add(stop);
            }
        }
        if (stops.Count == 0) return null;
        return new ParsedLinearGradient(angleDeg, stops);
    }

    private static (double dx, double dy) DirectionVector(double angleDeg, int w, int h)
    {
        // CSS angles: 0deg = "to top" (up = -y), positive
        // clockwise. Convert to standard math angle from
        // +x axis, counter-clockwise: math = 90 - css.
        double math = (90 - angleDeg) * Math.PI / 180.0;
        double dx = Math.Cos(math);
        double dy = -Math.Sin(math); // y grows downward
        return (dx, dy);
    }

    private static bool TryParseAngle(string token, out double degrees)
    {
        degrees = 0;
        if (string.IsNullOrEmpty(token)) return false;
        var t = token.ToLowerInvariant();
        // Direction keywords map to standard angles.
        switch (t)
        {
            case "to top": degrees = 0; return true;
            case "to right": degrees = 90; return true;
            case "to bottom": degrees = 180; return true;
            case "to left": degrees = 270; return true;
            case "to top right": degrees = 45; return true;
            case "to bottom right": degrees = 135; return true;
            case "to bottom left": degrees = 225; return true;
            case "to top left": degrees = 315; return true;
        }
        // Numeric angle with unit suffix.
        if (t.EndsWith("deg"))
        {
            return double.TryParse(t.AsSpan(0, t.Length - 3),
                NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
        }
        if (t.EndsWith("rad"))
        {
            if (double.TryParse(t.AsSpan(0, t.Length - 3),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var rad))
            {
                degrees = rad * 180 / Math.PI;
                return true;
            }
        }
        if (t.EndsWith("turn"))
        {
            if (double.TryParse(t.AsSpan(0, t.Length - 4),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var turn))
            {
                degrees = turn * 360;
                return true;
            }
        }
        return false;
    }

    private static bool TryParseStop(string token, out GradientStop stop)
    {
        stop = default;
        if (string.IsNullOrEmpty(token)) return false;
        var parts = SplitOnTopLevelWhitespace(token);
        if (parts.Count == 0) return false;

        // First token is the color; remaining (optional)
        // are positions. Real CSS allows two positions
        // per stop for color hints; we honor only the first.
        var color = CssColor.Parse(parts[0]);
        if (color.IsTransparent && parts[0] != "transparent") return false;
        double? pos = null;
        if (parts.Count > 1)
        {
            var posStr = parts[1].Trim();
            if (posStr.EndsWith('%') &&
                double.TryParse(posStr.AsSpan(0, posStr.Length - 1),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                pos = pct / 100.0;
            }
            else if (posStr.EndsWith("px") &&
                double.TryParse(posStr.AsSpan(0, posStr.Length - 2),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            {
                // Treat absolute lengths as a 0..1 fraction
                // proportional to a 1000-pixel reference; we
                // don't know the gradient length at parse
                // time. Approximation that reads better than
                // dropping the stop entirely.
                pos = px / 1000.0;
            }
        }
        stop = new GradientStop(color, pos);
        return true;
    }

    private static List<GradientStop> NormalizeStops(IReadOnlyList<GradientStop> source)
    {
        // CSS stops without explicit positions distribute
        // evenly between the previous and next explicit
        // ones, defaulting to 0 / 1 at the endpoints. The
        // simplification here: any unset positions fill in
        // proportional to source order between the latest
        // explicit position and the next one (or 1).
        var stops = source.Select(s => new GradientStop(s.Color, s.Position)).ToList();
        // Endpoints:
        if (!stops[0].Position.HasValue) stops[0] = new GradientStop(stops[0].Color, 0);
        if (!stops[^1].Position.HasValue) stops[^1] = new GradientStop(stops[^1].Color, 1);
        // Walk and fill.
        double last = stops[0].Position!.Value;
        for (int i = 1; i < stops.Count; i++)
        {
            if (stops[i].Position.HasValue) { last = stops[i].Position!.Value; continue; }
            int j = i;
            while (j < stops.Count && !stops[j].Position.HasValue) j++;
            double next = j < stops.Count ? stops[j].Position!.Value : 1;
            int gap = j - i + 1;
            for (int k = 0; k < gap - 1; k++)
            {
                double frac = (k + 1) / (double)gap;
                stops[i + k] = new GradientStop(stops[i + k].Color, last + (next - last) * frac);
            }
            i = j - 1;
        }
        return stops;
    }

    private static PaintColor SampleStops(IReadOnlyList<GradientStop> stops, double t)
    {
        // Find the segment t falls into and lerp.
        if (t <= stops[0].Position!.Value) return stops[0].Color;
        if (t >= stops[^1].Position!.Value) return stops[^1].Color;
        for (int i = 1; i < stops.Count; i++)
        {
            double pos = stops[i].Position!.Value;
            if (t > pos) continue;
            double prev = stops[i - 1].Position!.Value;
            double frac = pos == prev ? 0 : (t - prev) / (pos - prev);
            return Lerp(stops[i - 1].Color, stops[i].Color, frac);
        }
        return stops[^1].Color;
    }

    private static PaintColor Lerp(PaintColor a, PaintColor b, double t)
    {
        byte L(byte ca, byte cb) => (byte)(ca + (cb - ca) * t);
        return new PaintColor(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
    }

    private static void BlitPixel(RasterBuffer buffer, int x, int y, PaintColor color)
    {
        int idx = (y * buffer.Width + x) * 4;
        if (color.IsOpaque)
        {
            buffer.Pixels[idx] = color.R;
            buffer.Pixels[idx + 1] = color.G;
            buffer.Pixels[idx + 2] = color.B;
            buffer.Pixels[idx + 3] = 255;
        }
        else
        {
            double a = color.A / 255.0;
            double oneMinus = 1 - a;
            buffer.Pixels[idx] = (byte)(color.R * a + buffer.Pixels[idx] * oneMinus);
            buffer.Pixels[idx + 1] = (byte)(color.G * a + buffer.Pixels[idx + 1] * oneMinus);
            buffer.Pixels[idx + 2] = (byte)(color.B * a + buffer.Pixels[idx + 2] * oneMinus);
            buffer.Pixels[idx + 3] = (byte)Math.Min(255, buffer.Pixels[idx + 3] + color.A * oneMinus);
        }
    }

    private static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == separator && depth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    private static List<string> SplitOnTopLevelWhitespace(string s)
    {
        var parts = new List<string>();
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}

public sealed record ParsedLinearGradient(double AngleDeg, IReadOnlyList<GradientStop> Stops);

public readonly record struct GradientStop(PaintColor Color, double? Position);
