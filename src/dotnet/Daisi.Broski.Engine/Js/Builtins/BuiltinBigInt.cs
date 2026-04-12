using System.Globalization;
using System.Numerics;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2020 <c>BigInt</c> built-in: the constructor function
/// that coerces numbers / strings / booleans into a
/// <see cref="BigInteger"/>. The literal syntax (<c>42n</c>,
/// <c>0x1fn</c>) lands in the lexer + parser directly; this
/// file handles the imperative <c>BigInt(value)</c> path
/// that scripts use to construct BigInts from runtime
/// values.
///
/// <para>
/// Calling as <c>new BigInt(x)</c> throws
/// <c>TypeError</c> per spec — <c>BigInt</c> is not a
/// constructor in the class sense, only a coercion function.
/// </para>
///
/// <para>
/// Deferred: <c>BigInt.asIntN(bits, v)</c> and
/// <c>BigInt.asUintN(bits, v)</c> for fixed-width wrapping.
/// Neither shows up frequently in practice.
/// </para>
/// </summary>
internal static class BuiltinBigInt
{
    public static void Install(JsEngine engine)
    {
        var ctor = new JsFunction("BigInt", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is JsUndefined)
            {
                JsThrow.TypeError("Cannot convert undefined to a BigInt");
                return null;
            }
            return Coerce(args[0]);
        });
        engine.Globals["BigInt"] = ctor;
    }

    /// <summary>
    /// Convert a JS value into a <see cref="BigInteger"/>.
    /// Numbers must be integer-valued (non-integers throw
    /// <c>RangeError</c> per spec). Strings must parse as
    /// a decimal or hex integer literal. Other objects
    /// throw <c>TypeError</c>.
    /// </summary>
    private static object? Coerce(object? v)
    {
        switch (v)
        {
            case BigInteger bi:
                return bi;
            case bool b:
                return b ? BigInteger.One : BigInteger.Zero;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    JsThrow.RangeError("Cannot convert non-finite Number to a BigInt");
                    return null;
                }
                if (d != Math.Truncate(d))
                {
                    JsThrow.RangeError("Cannot convert non-integer Number to a BigInt");
                    return null;
                }
                return new BigInteger(d);
            case string s:
                var trimmed = s.Trim();
                if (trimmed.Length == 0) return BigInteger.Zero;
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefix with 0 so .NET treats the hex as
                    // unsigned — BigInt strings are
                    // always non-negative per spec.
                    if (BigInteger.TryParse(
                            "0" + trimmed.Substring(2),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var hex))
                    {
                        return hex;
                    }
                    JsThrow.SyntaxError($"Cannot convert '{s}' to a BigInt");
                    return null;
                }
                if (BigInteger.TryParse(
                        trimmed,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var dec))
                {
                    return dec;
                }
                JsThrow.SyntaxError($"Cannot convert '{s}' to a BigInt");
                return null;
            case JsNull:
                JsThrow.TypeError("Cannot convert null to a BigInt");
                return null;
            default:
                JsThrow.TypeError("Cannot convert this value to a BigInt");
                return null;
        }
    }
}
