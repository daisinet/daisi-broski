using System.Globalization;

namespace Daisi.Broski.Engine.Paint.Svg;

/// <summary>
/// Parses SVG path data (the value of a
/// <c>&lt;path d="..."&gt;</c> attribute) into a flat list
/// of polygon vertices. Cubic and quadratic Béziers are
/// flattened into line segments via fixed-step subdivision
/// — coarse but visually adequate at typical screenshot
/// scales, and dodges the recursive de Casteljau bookkeeping
/// a fidelity-grade flattener would need.
///
/// <para>
/// Supported commands (case-insensitive — uppercase is
/// absolute, lowercase is relative per spec):
/// <c>M m L l H h V v C c S s Q q T t Z z</c>. Elliptical
/// arc (<c>A a</c>) is deliberately skipped — its
/// parameter set is large and arcs are uncommon outside of
/// circle/ellipse approximations that we already cover.
/// </para>
/// </summary>
public static class SvgPath
{
    /// <summary>Subpaths produced by the path data — a
    /// new subpath starts at every <c>M</c> command and
    /// closes when <c>Z</c> is hit (or implicitly at the
    /// end of input). Each subpath is a flat polygon
    /// suitable for scanline fill.</summary>
    public static List<List<(double X, double Y)>> Parse(string d)
    {
        var subpaths = new List<List<(double, double)>>();
        if (string.IsNullOrEmpty(d)) return subpaths;

        var tokens = Tokenize(d);
        int i = 0;
        double cx = 0, cy = 0;        // current point
        double startX = 0, startY = 0; // subpath start
        double prevCtrlX = 0, prevCtrlY = 0; // previous Bezier control (for S/T)
        char lastCmd = ' ';
        List<(double, double)> current = new();

        while (i < tokens.Count)
        {
            char cmd;
            if (tokens[i].Length == 1 && IsCommand(tokens[i][0]))
            {
                cmd = tokens[i][0];
                i++;
            }
            else
            {
                // Implicit repeat of the previous command.
                cmd = lastCmd switch
                {
                    'M' => 'L',
                    'm' => 'l',
                    _ => lastCmd,
                };
                if (cmd == ' ') return subpaths;
            }
            lastCmd = cmd;
            bool relative = char.IsLower(cmd);
            char absCmd = char.ToUpperInvariant(cmd);

            switch (absCmd)
            {
                case 'M':
                    {
                        if (i + 1 >= tokens.Count) return subpaths;
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x += cx; y += cy; }
                        if (current.Count > 0) subpaths.Add(current);
                        current = new() { (x, y) };
                        cx = x; cy = y;
                        startX = x; startY = y;
                        prevCtrlX = cx; prevCtrlY = cy;
                        break;
                    }
                case 'L':
                    {
                        if (i + 1 >= tokens.Count) return subpaths;
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x += cx; y += cy; }
                        current.Add((x, y));
                        cx = x; cy = y;
                        prevCtrlX = cx; prevCtrlY = cy;
                        break;
                    }
                case 'H':
                    {
                        if (i >= tokens.Count) return subpaths;
                        double x = ParseNum(tokens[i++]);
                        if (relative) x += cx;
                        current.Add((x, cy));
                        cx = x;
                        prevCtrlX = cx; prevCtrlY = cy;
                        break;
                    }
                case 'V':
                    {
                        if (i >= tokens.Count) return subpaths;
                        double y = ParseNum(tokens[i++]);
                        if (relative) y += cy;
                        current.Add((cx, y));
                        cy = y;
                        prevCtrlX = cx; prevCtrlY = cy;
                        break;
                    }
                case 'C':
                    {
                        if (i + 5 >= tokens.Count) return subpaths;
                        double x1 = ParseNum(tokens[i++]);
                        double y1 = ParseNum(tokens[i++]);
                        double x2 = ParseNum(tokens[i++]);
                        double y2 = ParseNum(tokens[i++]);
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        FlattenCubic(current, cx, cy, x1, y1, x2, y2, x, y);
                        cx = x; cy = y;
                        prevCtrlX = x2; prevCtrlY = y2;
                        break;
                    }
                case 'S':
                    {
                        if (i + 3 >= tokens.Count) return subpaths;
                        double x2 = ParseNum(tokens[i++]);
                        double y2 = ParseNum(tokens[i++]);
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x2 += cx; y2 += cy; x += cx; y += cy; }
                        // S reflects the previous control point.
                        double x1 = 2 * cx - prevCtrlX;
                        double y1 = 2 * cy - prevCtrlY;
                        FlattenCubic(current, cx, cy, x1, y1, x2, y2, x, y);
                        cx = x; cy = y;
                        prevCtrlX = x2; prevCtrlY = y2;
                        break;
                    }
                case 'Q':
                    {
                        if (i + 3 >= tokens.Count) return subpaths;
                        double x1 = ParseNum(tokens[i++]);
                        double y1 = ParseNum(tokens[i++]);
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x1 += cx; y1 += cy; x += cx; y += cy; }
                        FlattenQuadratic(current, cx, cy, x1, y1, x, y);
                        cx = x; cy = y;
                        prevCtrlX = x1; prevCtrlY = y1;
                        break;
                    }
                case 'T':
                    {
                        if (i + 1 >= tokens.Count) return subpaths;
                        double x = ParseNum(tokens[i++]);
                        double y = ParseNum(tokens[i++]);
                        if (relative) { x += cx; y += cy; }
                        double x1 = 2 * cx - prevCtrlX;
                        double y1 = 2 * cy - prevCtrlY;
                        FlattenQuadratic(current, cx, cy, x1, y1, x, y);
                        cx = x; cy = y;
                        prevCtrlX = x1; prevCtrlY = y1;
                        break;
                    }
                case 'Z':
                    if (current.Count > 0)
                    {
                        current.Add((startX, startY));
                        subpaths.Add(current);
                        current = new();
                        cx = startX; cy = startY;
                    }
                    break;
                default:
                    // Unknown command (likely 'A') — skip
                    // tokens until we hit the next command.
                    while (i < tokens.Count && !IsCommand(tokens[i][0])) i++;
                    break;
            }
        }
        if (current.Count > 0) subpaths.Add(current);
        return subpaths;
    }

    /// <summary>Flatten a cubic Bézier into line segments
    /// via fixed-step sampling. 16 steps is enough to keep
    /// tile-mesh artifacts invisible at viewport scales
    /// without being a measurable cost.</summary>
    private static void FlattenCubic(
        List<(double, double)> dst,
        double x0, double y0, double x1, double y1,
        double x2, double y2, double x3, double y3)
    {
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double mt = 1 - t;
            double x = mt * mt * mt * x0
                + 3 * mt * mt * t * x1
                + 3 * mt * t * t * x2
                + t * t * t * x3;
            double y = mt * mt * mt * y0
                + 3 * mt * mt * t * y1
                + 3 * mt * t * t * y2
                + t * t * t * y3;
            dst.Add((x, y));
        }
    }

    private static void FlattenQuadratic(
        List<(double, double)> dst,
        double x0, double y0, double x1, double y1, double x2, double y2)
    {
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double mt = 1 - t;
            double x = mt * mt * x0 + 2 * mt * t * x1 + t * t * x2;
            double y = mt * mt * y0 + 2 * mt * t * y1 + t * t * y2;
            dst.Add((x, y));
        }
    }

    private static bool IsCommand(char c) => "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;

    private static double ParseNum(string s)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 0;
    }

    /// <summary>Tokenize SVG path data into commands and
    /// numeric parameters. Numbers can be separated by
    /// whitespace, commas, or by an implicit boundary
    /// where the next number starts (negative sign, second
    /// decimal point in a coordinate pair like
    /// <c>1.5.5</c>).</summary>
    private static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        int i = 0;
        var current = new System.Text.StringBuilder();
        bool inNumber = false;
        bool numHasDot = false;
        bool numHasE = false;
        while (i < source.Length)
        {
            char c = source[i];
            if (IsCommand(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                tokens.Add(c.ToString());
                inNumber = false; numHasDot = false; numHasE = false;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == ',')
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                inNumber = false; numHasDot = false; numHasE = false;
                i++;
                continue;
            }
            if (c == '-' || c == '+')
            {
                // Negative inside a number is exponent sign:
                // 1e-3, 2E+4. Otherwise it starts a new number.
                if (inNumber && current.Length > 0
                    && (current[^1] == 'e' || current[^1] == 'E'))
                {
                    current.Append(c);
                    i++;
                    continue;
                }
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                current.Append(c);
                inNumber = true; numHasDot = false; numHasE = false;
                i++;
                continue;
            }
            if (c == '.')
            {
                if (numHasDot)
                {
                    // Second dot → implicit number boundary
                    // (e.g. "1.5.5" splits as "1.5" + ".5").
                    tokens.Add(current.ToString());
                    current.Clear();
                    current.Append('.');
                    numHasDot = true; numHasE = false;
                    i++;
                    continue;
                }
                current.Append(c);
                inNumber = true; numHasDot = true;
                i++;
                continue;
            }
            if (char.IsDigit(c))
            {
                current.Append(c);
                inNumber = true;
                i++;
                continue;
            }
            if (c == 'e' || c == 'E')
            {
                if (inNumber && !numHasE)
                {
                    current.Append(c);
                    numHasE = true;
                    i++;
                    continue;
                }
            }
            // Unrecognized — skip.
            i++;
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
