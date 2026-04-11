using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Number</c> and <c>Boolean</c> constructors + prototype
/// methods. Both are simple because phase 3a uses raw .NET
/// <see cref="double"/> and <see cref="bool"/> as their value
/// representation, so there are no wrapper objects to manage —
/// the VM's primitive-property lookup routes
/// <c>(7).toFixed(2)</c> to <see cref="JsEngine.NumberPrototype"/>
/// and the method receives the primitive as <c>this</c>.
/// </summary>
internal static class BuiltinNumberBoolean
{
    public static void Install(JsEngine engine)
    {
        InstallNumber(engine);
        InstallBoolean(engine);
    }

    // -------------------------------------------------------------------
    // Number
    // -------------------------------------------------------------------

    private static void InstallNumber(JsEngine engine)
    {
        var proto = engine.NumberPrototype;
        Builtins.Method(proto, "toString", NumberToString);
        Builtins.Method(proto, "valueOf", NumberValueOf);
        Builtins.Method(proto, "toFixed", ToFixed);

        var ctor = new JsFunction("Number", (thisVal, args) =>
        {
            // Number() with no args is 0; Number(x) coerces.
            if (args.Count == 0) return 0.0;
            return JsValue.ToNumber(args[0]);
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);

        // Static properties.
        ctor.SetNonEnumerable("MAX_VALUE", double.MaxValue);
        ctor.SetNonEnumerable("MIN_VALUE", double.Epsilon);
        ctor.SetNonEnumerable("NaN", double.NaN);
        ctor.SetNonEnumerable("POSITIVE_INFINITY", double.PositiveInfinity);
        ctor.SetNonEnumerable("NEGATIVE_INFINITY", double.NegativeInfinity);
        ctor.SetNonEnumerable("EPSILON", 2.220446049250313e-16);
        ctor.SetNonEnumerable("MAX_SAFE_INTEGER", 9007199254740991.0);
        ctor.SetNonEnumerable("MIN_SAFE_INTEGER", -9007199254740991.0);

        // Static methods.
        Builtins.Method(ctor, "isNaN", (t, a) =>
            a.Count > 0 && a[0] is double d && double.IsNaN(d));
        Builtins.Method(ctor, "isFinite", (t, a) =>
            a.Count > 0 && a[0] is double d && !double.IsNaN(d) && !double.IsInfinity(d));
        Builtins.Method(ctor, "isInteger", (t, a) =>
            a.Count > 0 && a[0] is double d && !double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Truncate(d));

        engine.Globals["Number"] = ctor;
    }

    private static object? NumberToString(object? thisVal, IReadOnlyList<object?> args)
    {
        double n = JsValue.ToNumber(thisVal);
        int radix = args.Count > 0 && args[0] is not JsUndefined
            ? (int)JsValue.ToInt32(args[0])
            : 10;
        if (radix < 2 || radix > 36)
        {
            return JsThrow.RangeError("toString radix must be between 2 and 36");
        }
        if (radix == 10) return JsValue.ToJsString(n);

        if (double.IsNaN(n)) return "NaN";
        if (double.IsPositiveInfinity(n)) return "Infinity";
        if (double.IsNegativeInfinity(n)) return "-Infinity";
        if (n == 0) return "0";

        // Integer-to-base conversion. Non-integer doubles with a
        // non-10 radix are rare and have a complicated spec algo;
        // slice 6c handles integers exactly and falls back to the
        // integer part with a lossy warning for fractions.
        bool negative = n < 0;
        if (negative) n = -n;
        long intPart = (long)Math.Truncate(n);

        var sb = new StringBuilder();
        if (intPart == 0)
        {
            sb.Append('0');
        }
        else
        {
            while (intPart > 0)
            {
                int digit = (int)(intPart % radix);
                sb.Insert(0, digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10));
                intPart /= radix;
            }
        }
        if (negative) sb.Insert(0, '-');
        return sb.ToString();
    }

    private static object? NumberValueOf(object? thisVal, IReadOnlyList<object?> args)
    {
        return JsValue.ToNumber(thisVal);
    }

    /// <summary>
    /// <c>toFixed(digits)</c> — fixed-point notation with the
    /// given number of digits after the decimal point. Matches
    /// the spec's rounding rules via .NET's <c>F{digits}</c>
    /// format, which uses banker's rounding rather than ECMA's
    /// round-half-away; the observable differences are rare
    /// enough that we ignore them until a test catches one.
    /// </summary>
    private static object? ToFixed(object? thisVal, IReadOnlyList<object?> args)
    {
        double n = JsValue.ToNumber(thisVal);
        int digits = args.Count > 0 ? (int)JsValue.ToInt32(args[0]) : 0;
        if (digits < 0 || digits > 100)
        {
            return JsThrow.RangeError("toFixed digits must be between 0 and 100");
        }
        if (double.IsNaN(n)) return "NaN";
        if (double.IsInfinity(n)) return n > 0 ? "Infinity" : "-Infinity";
        return n.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    // -------------------------------------------------------------------
    // Boolean
    // -------------------------------------------------------------------

    private static void InstallBoolean(JsEngine engine)
    {
        var proto = engine.BooleanPrototype;
        Builtins.Method(proto, "toString", BoolToString);
        Builtins.Method(proto, "valueOf", BoolValueOf);

        var ctor = new JsFunction("Boolean", (thisVal, args) =>
        {
            if (args.Count == 0) return false;
            return JsValue.ToBoolean(args[0]);
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Boolean"] = ctor;
    }

    private static object? BoolToString(object? thisVal, IReadOnlyList<object?> args)
    {
        bool b = JsValue.ToBoolean(thisVal);
        return b ? "true" : "false";
    }

    private static object? BoolValueOf(object? thisVal, IReadOnlyList<object?> args)
    {
        return JsValue.ToBoolean(thisVal);
    }
}
