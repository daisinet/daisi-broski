using System.Globalization;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Sentinel for the <c>undefined</c> value. We use a distinct singleton
/// rather than conflating it with C# <c>null</c> because JavaScript
/// treats <c>undefined</c> and <c>null</c> as distinct types and values
/// — notably, <c>typeof undefined</c> is <c>"undefined"</c> while
/// <c>typeof null</c> is <c>"object"</c>.
/// </summary>
public sealed class JsUndefined
{
    internal JsUndefined() { }
    public override string ToString() => "undefined";
}

/// <summary>Sentinel for the <c>null</c> value.</summary>
public sealed class JsNull
{
    internal JsNull() { }
    public override string ToString() => "null";
}

/// <summary>
/// Singletons and coercion helpers for the ES5 value model. Phase 3a
/// uses boxed .NET objects as the value representation (DD-05 option
/// A): numbers are boxed <see cref="double"/>s, strings are C# strings,
/// booleans are boxed <see cref="bool"/>s, and <c>null</c> / <c>undefined</c>
/// are distinct singleton sentinels. Tagged-union struct refactoring
/// is DD-05's phase 3c item.
///
/// All coercions follow ECMA-262 §9 (Type Conversion). The equality
/// helpers follow §11.9 (Equality Operators). Edge cases not yet
/// handled (because they require objects, which slice 3 does not ship):
/// <c>ToPrimitive</c> on object operands, and any loose-equality case
/// that involves an object operand.
/// </summary>
public static class JsValue
{
    public static readonly object Undefined = new JsUndefined();
    public static readonly object Null = new JsNull();
    public static readonly object True = true;
    public static readonly object False = false;

    public static object Box(double n) => n;
    public static object Box(bool b) => b ? True : False;
    public static object Box(string s) => s;

    // -------------------------------------------------------------------
    // Type predicates
    // -------------------------------------------------------------------

    public static bool IsUndefined(object? v) => v is JsUndefined;
    public static bool IsNull(object? v) => v is JsNull;
    public static bool IsNumber(object? v) => v is double;
    public static bool IsString(object? v) => v is string;
    public static bool IsBoolean(object? v) => v is bool;

    // -------------------------------------------------------------------
    // ToBoolean — ECMA §9.2
    // -------------------------------------------------------------------

    public static bool ToBoolean(object? v)
    {
        if (v is JsUndefined || v is JsNull) return false;
        if (v is bool b) return b;
        if (v is double d) return d != 0 && !double.IsNaN(d);
        if (v is string s) return s.Length > 0;
        // Objects (including arrays, functions when we add them)
        // always coerce to true — even empty arrays and boxed
        // zero-like objects like `new Number(0)`.
        return true;
    }

    // -------------------------------------------------------------------
    // ToNumber — ECMA §9.3
    // -------------------------------------------------------------------

    public static double ToNumber(object? v)
    {
        if (v is JsUndefined) return double.NaN;
        if (v is JsNull) return 0.0;
        if (v is bool b) return b ? 1.0 : 0.0;
        if (v is double d) return d;
        if (v is string s) return StringToNumber(s);
        // ECMA §9.3 also defines ToPrimitive(object, hint: Number)
        // which, for arrays, delegates to join(',') and then
        // StringToNumber. Empty array → "" → 0, single-element
        // numeric array → the element as a number, etc. Plain
        // objects → "[object Object]" → NaN.
        if (v is JsArray arr) return StringToNumber(arr.Join(","));
        if (v is JsObject) return double.NaN;
        return double.NaN;
    }

    /// <summary>
    /// ECMA §9.3.1 — ToNumber applied to a string. The spec allows
    /// leading / trailing whitespace, an optional sign, and the
    /// literal forms <c>Infinity</c>, <c>0x...</c>, plus decimal /
    /// scientific notation. An empty trimmed string is <c>0</c>, not
    /// <c>NaN</c> (the one non-intuitive case).
    /// </summary>
    private static double StringToNumber(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length == 0) return 0.0;
        if (trimmed == "Infinity" || trimmed == "+Infinity") return double.PositiveInfinity;
        if (trimmed == "-Infinity") return double.NegativeInfinity;

        // Hex literal — no sign, no fractional part allowed.
        if (trimmed.Length > 2 && trimmed[0] == '0' && (trimmed[1] == 'x' || trimmed[1] == 'X'))
        {
            if (long.TryParse(
                    trimmed.AsSpan(2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var hex))
            {
                return (double)hex;
            }
            return double.NaN;
        }

        if (double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return result;
        }
        return double.NaN;
    }

    // -------------------------------------------------------------------
    // ToString — ECMA §9.8
    // -------------------------------------------------------------------

    public static string ToJsString(object? v)
    {
        if (v is JsUndefined) return "undefined";
        if (v is JsNull) return "null";
        if (v is bool b) return b ? "true" : "false";
        if (v is double d) return NumberToString(d);
        if (v is string s) return s;
        // Array.prototype.toString delegates to join(',') per spec;
        // we inline that to avoid a dependency on the built-in
        // library (which slice 6 will ship).
        if (v is JsArray arr) return arr.Join(",");
        // Plain objects render as the canonical "[object Object]"
        // string that `Object.prototype.toString` produces for
        // untagged objects.
        if (v is JsObject) return "[object Object]";
        return v?.ToString() ?? "null";
    }

    /// <summary>
    /// ECMA §9.8.1 — ToString applied to a Number. We match the
    /// observable output of V8 / SpiderMonkey for the common cases:
    /// integers render without a decimal point, infinities render as
    /// <c>"Infinity"</c> / <c>"-Infinity"</c>, <c>NaN</c> renders as
    /// <c>"NaN"</c>, and everything else falls back to the .NET
    /// round-trip format. The spec's exact double-to-decimal
    /// algorithm (Steele + White) is not implemented — the few cases
    /// where <c>R</c> formatting disagrees with V8 are noted in tests
    /// and will be tightened in phase 3c when test262 cares.
    /// </summary>
    private static string NumberToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return "0";
        // Integer case — cover the common "was this ever really a
        // float?" path. Avoids rendering `1` as `"1"` vs `"1.0"`.
        if (d >= long.MinValue && d <= long.MaxValue && d == Math.Truncate(d))
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    // -------------------------------------------------------------------
    // TypeOf — ECMA §11.4.3
    // -------------------------------------------------------------------

    public static string TypeOf(object? v)
    {
        if (v is JsUndefined) return "undefined";
        if (v is JsNull) return "object"; // historical quirk
        if (v is bool) return "boolean";
        if (v is double) return "number";
        if (v is string) return "string";
        if (v is JsFunction) return "function";
        return "object";
    }

    // -------------------------------------------------------------------
    // Equality — ECMA §11.9
    // -------------------------------------------------------------------

    /// <summary>
    /// Strict equality (<c>===</c>). Different types are never equal.
    /// Within a type: <c>NaN</c> is not equal to anything (including
    /// itself); <c>+0</c> equals <c>-0</c>; strings compare ordinally;
    /// objects compare by reference. Undefined and null are each equal
    /// to themselves.
    /// </summary>
    public static bool StrictEquals(object? a, object? b)
    {
        if (a is JsUndefined) return b is JsUndefined;
        if (a is JsNull) return b is JsNull;
        if (a is bool ab) return b is bool bb && ab == bb;
        if (a is double ad)
        {
            if (b is not double bd) return false;
            if (double.IsNaN(ad) || double.IsNaN(bd)) return false;
            return ad == bd;
        }
        if (a is string asStr) return b is string bs && asStr == bs;
        return ReferenceEquals(a, b);
    }

    /// <summary>
    /// Loose equality (<c>==</c>). Delegates to strict equality when
    /// the types match; otherwise applies the coercion rules in
    /// ECMA §11.9.3. Object comparisons are not supported (slice 3
    /// has no object values); if they arrive here we fall back to
    /// <see cref="StrictEquals"/>.
    /// </summary>
    public static bool LooseEquals(object? a, object? b)
    {
        // Same type → strict.
        if (SameType(a, b)) return StrictEquals(a, b);

        // null == undefined
        if ((a is JsNull && b is JsUndefined) || (a is JsUndefined && b is JsNull)) return true;

        // number == string → number == ToNumber(string)
        if (a is double && b is string) return StrictEquals(a, ToNumber(b));
        if (a is string && b is double) return StrictEquals(ToNumber(a), b);

        // boolean on either side → ToNumber(bool) vs other, then retry.
        if (a is bool) return LooseEquals(ToNumber(a), b);
        if (b is bool) return LooseEquals(a, ToNumber(b));

        // Slice 3 has no objects — anything else is unequal.
        return false;
    }

    private static bool SameType(object? a, object? b)
    {
        if (a is JsUndefined) return b is JsUndefined;
        if (a is JsNull) return b is JsNull;
        if (a is bool) return b is bool;
        if (a is double) return b is double;
        if (a is string) return b is string;
        return a?.GetType() == b?.GetType();
    }

    // -------------------------------------------------------------------
    // Integer coercions for bitwise operators — ECMA §9.5 / §9.6
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §9.5 — ToInt32. Coerce to number, replace NaN / infinity
    /// with 0, truncate toward zero, then wrap modulo 2^32 into the
    /// signed 32-bit range.
    /// </summary>
    public static int ToInt32(object? v)
    {
        double n = ToNumber(v);
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0) return 0;
        double posInt = Math.Sign(n) * Math.Floor(Math.Abs(n));
        double mod = posInt - 4294967296.0 * Math.Floor(posInt / 4294967296.0);
        if (mod >= 2147483648.0) mod -= 4294967296.0;
        return (int)mod;
    }

    /// <summary>ECMA §9.6 — ToUint32, same shape but unsigned wrap.</summary>
    public static uint ToUint32(object? v)
    {
        double n = ToNumber(v);
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0) return 0;
        double posInt = Math.Sign(n) * Math.Floor(Math.Abs(n));
        double mod = posInt - 4294967296.0 * Math.Floor(posInt / 4294967296.0);
        if (mod < 0) mod += 4294967296.0;
        return (uint)mod;
    }
}
