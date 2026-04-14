using System.Globalization;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Parsed CSS length value. Phase-6c only models the units a
/// block-flow layout needs to converge on a pixel: absolute
/// pixels, percentages of the containing block, font-relative
/// (em / rem), the special <c>auto</c> sentinel, and a
/// catch-all <c>None</c> for unset properties. Larger units
/// (<c>vw</c> / <c>vh</c> / <c>cm</c> / <c>in</c>) and
/// computed-value-time math (<c>calc()</c>) are deferred —
/// real-world block layout converges fine on the first three.
/// </summary>
public readonly struct Length
{
    public LengthUnit Unit { get; }
    public double Value { get; }

    private Length(LengthUnit unit, double value) { Unit = unit; Value = value; }

    public static readonly Length None = new(LengthUnit.None, 0);
    public static readonly Length Auto = new(LengthUnit.Auto, 0);
    public static readonly Length Zero = new(LengthUnit.Px, 0);

    public static Length Px(double v) => new(LengthUnit.Px, v);
    public static Length Percent(double v) => new(LengthUnit.Percent, v);
    public static Length Em(double v) => new(LengthUnit.Em, v);
    public static Length Rem(double v) => new(LengthUnit.Rem, v);
    public static Length Vw(double v) => new(LengthUnit.Vw, v);
    public static Length Vh(double v) => new(LengthUnit.Vh, v);
    public static Length Vmin(double v) => new(LengthUnit.Vmin, v);
    public static Length Vmax(double v) => new(LengthUnit.Vmax, v);

    /// <summary>Per-thread viewport snapshot — set at the
    /// top of <c>LayoutTree.Build</c> so <c>vw</c> / <c>vh</c>
    /// resolve against the current viewport without having to
    /// thread it through every Length.Resolve callsite.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() { }
    [ThreadStatic] private static double _viewportW;
    [ThreadStatic] private static double _viewportH;
    public static void SetViewport(double w, double h)
    {
        _viewportW = w;
        _viewportH = h;
    }

    /// <summary>Parse a CSS length string. Recognized:
    /// <c>"auto"</c>, plain numbers (treated as px), and
    /// numeric values with the <c>px</c> / <c>%</c> /
    /// <c>em</c> / <c>rem</c> / <c>pt</c> suffixes. Anything
    /// else (units we don't model, garbage strings, empty)
    /// returns <see cref="None"/> so callers can treat
    /// "unparseable" identically to "unset".</summary>
    public static Length Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return None;
        var s = source.Trim();
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Auto;

        int i = 0;
        // Allow leading sign + digits + decimal point.
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return None;

        if (!double.TryParse(s.AsSpan(0, i), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value))
        {
            return None;
        }
        var unit = s.Substring(i).Trim().ToLowerInvariant();
        return unit switch
        {
            "" => Px(value),
            "px" => Px(value),
            "%" => Percent(value),
            "em" => Em(value),
            "rem" => Rem(value),
            "pt" => Px(value * 1.333),
            "vw" => Vw(value),
            "vh" => Vh(value),
            "vmin" => Vmin(value),
            "vmax" => Vmax(value),
            "cm" => Px(value * 37.795),
            "mm" => Px(value * 3.7795),
            "in" => Px(value * 96),
            _ => None,
        };
    }

    /// <summary>Resolve to absolute pixels against a
    /// containing-block size + a font-size context (used as
    /// the multiplier for <c>em</c>; <c>rem</c> always uses
    /// <paramref name="rootFontSize"/>). Returns
    /// <paramref name="fallback"/> for <see cref="None"/> /
    /// <see cref="Auto"/> — those flow through the layout
    /// algorithm separately.</summary>
    public double Resolve(double containingSize, double fontSize, double rootFontSize, double fallback = 0.0)
    {
        return Unit switch
        {
            LengthUnit.Px => Value,
            LengthUnit.Percent => containingSize * Value / 100.0,
            LengthUnit.Em => Value * fontSize,
            LengthUnit.Rem => Value * rootFontSize,
            LengthUnit.Vw => _viewportW * Value / 100.0,
            LengthUnit.Vh => _viewportH * Value / 100.0,
            LengthUnit.Vmin => Math.Min(_viewportW, _viewportH) * Value / 100.0,
            LengthUnit.Vmax => Math.Max(_viewportW, _viewportH) * Value / 100.0,
            _ => fallback,
        };
    }

    public bool IsAuto => Unit == LengthUnit.Auto;
    public bool IsNone => Unit == LengthUnit.None;
    public bool IsPercent => Unit == LengthUnit.Percent;
}

public enum LengthUnit
{
    None,
    Auto,
    Px,
    Percent,
    Em,
    Rem,
    Vw,
    Vh,
    Vmin,
    Vmax,
}
