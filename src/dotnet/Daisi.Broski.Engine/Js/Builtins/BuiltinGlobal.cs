using System.Globalization;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Global functions the ES5 spec installs on the global object:
/// <c>parseInt</c>, <c>parseFloat</c>, <c>isNaN</c>,
/// <c>isFinite</c>. The <c>undefined</c>, <c>NaN</c>, and
/// <c>Infinity</c> constants are seeded directly into
/// <see cref="JsEngine.Globals"/> by the engine constructor.
/// </summary>
internal static class BuiltinGlobal
{
    public static void Install(JsEngine engine)
    {
        engine.Globals["parseInt"] = new JsFunction("parseInt", ParseInt);
        engine.Globals["parseFloat"] = new JsFunction("parseFloat", ParseFloat);
        engine.Globals["isNaN"] = new JsFunction("isNaN", IsNaN);
        engine.Globals["isFinite"] = new JsFunction("isFinite", IsFinite);

        // eval(source) — direct eval. Parses + compiles the
        // source string and executes it in a nested dispatch
        // loop so the caller's VM state survives. The nested
        // chunk runs in the SAME scope (globals env), matching
        // the spec's "direct eval" semantics: declarations
        // made inside eval are visible to the outer program
        // after eval returns. A parse / compile error surfaces
        // as a script-visible SyntaxError.
        engine.Globals["eval"] = new JsFunction("eval", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Undefined;
            if (args[0] is not string source)
            {
                // Spec: eval(non-string) returns the argument.
                return args[0];
            }
            Chunk chunk;
            try
            {
                var program = new JsParser(source).ParseProgram();
                chunk = new JsCompiler().Compile(program);
            }
            catch (JsParseException ex)
            {
                JsThrow.SyntaxError($"eval: {ex.Message}");
                return null;
            }
            catch (JsCompileException ex)
            {
                JsThrow.SyntaxError($"eval: {ex.Message}");
                return null;
            }
            return engine.Vm.RunChunkNested(chunk);
        });
    }

    /// <summary>
    /// ECMA §15.1.2.2 — <c>parseInt(string, radix)</c>. Coerces
    /// the first argument to a string, strips leading whitespace,
    /// handles an optional sign and an optional <c>0x</c> / <c>0X</c>
    /// prefix, and parses the longest prefix of valid digits in
    /// the given radix (or 10 if radix is 0 / omitted, or 16 if
    /// the string starts with <c>0x</c>). Returns <c>NaN</c> if
    /// no digits are found.
    /// </summary>
    private static object? ParseInt(object? thisVal, IReadOnlyList<object?> args)
    {
        var inputRaw = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
        int radix = args.Count > 1 ? (int)JsValue.ToInt32(args[1]) : 0;

        int i = 0;
        // Skip leading whitespace per §7.2 WhiteSpace.
        while (i < inputRaw.Length && IsWhitespace(inputRaw[i])) i++;

        int sign = 1;
        if (i < inputRaw.Length && (inputRaw[i] == '+' || inputRaw[i] == '-'))
        {
            if (inputRaw[i] == '-') sign = -1;
            i++;
        }

        // Hex prefix detection.
        bool stripPrefix = false;
        if (radix == 16 || radix == 0)
        {
            if (i + 1 < inputRaw.Length && inputRaw[i] == '0' &&
                (inputRaw[i + 1] == 'x' || inputRaw[i + 1] == 'X'))
            {
                radix = 16;
                stripPrefix = true;
            }
        }
        if (radix == 0) radix = 10;
        if (radix < 2 || radix > 36) return double.NaN;

        if (stripPrefix) i += 2;

        int digitStart = i;
        double value = 0;
        while (i < inputRaw.Length)
        {
            int d = DigitValue(inputRaw[i]);
            if (d < 0 || d >= radix) break;
            value = value * radix + d;
            i++;
        }

        if (i == digitStart) return double.NaN;
        return sign * value;
    }

    /// <summary>
    /// ECMA §15.1.2.3 — <c>parseFloat(string)</c>. Coerces to
    /// string, trims leading whitespace, and parses the longest
    /// prefix that is a valid decimal literal (optionally with
    /// sign, fraction, and exponent). Does not recognize hex
    /// literals, unlike <c>parseInt</c>. Returns <c>NaN</c> if
    /// no valid float prefix is present.
    /// </summary>
    private static object? ParseFloat(object? thisVal, IReadOnlyList<object?> args)
    {
        var raw = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
        var trimmed = raw.TrimStart();

        // Special: "Infinity" and "-Infinity".
        if (trimmed.StartsWith("Infinity", StringComparison.Ordinal))
            return double.PositiveInfinity;
        if (trimmed.StartsWith("-Infinity", StringComparison.Ordinal))
            return double.NegativeInfinity;
        if (trimmed.StartsWith("+Infinity", StringComparison.Ordinal))
            return double.PositiveInfinity;

        // Find the longest prefix parseable as a double. Start
        // from the full trimmed string and shrink until parse
        // succeeds — a bit wasteful but straightforward.
        int end = trimmed.Length;
        while (end > 0)
        {
            var candidate = trimmed.Substring(0, end);
            if (double.TryParse(
                    candidate,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v))
            {
                return v;
            }
            end--;
        }
        return double.NaN;
    }

    private static object? IsNaN(object? thisVal, IReadOnlyList<object?> args)
    {
        if (args.Count == 0) return true; // isNaN(undefined) → true
        return double.IsNaN(JsValue.ToNumber(args[0]));
    }

    private static object? IsFinite(object? thisVal, IReadOnlyList<object?> args)
    {
        if (args.Count == 0) return false;
        var n = JsValue.ToNumber(args[0]);
        return !double.IsNaN(n) && !double.IsInfinity(n);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static bool IsWhitespace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f' || c == '\u00A0';

    private static int DigitValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'z') return c - 'a' + 10;
        if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
        return -1;
    }
}
