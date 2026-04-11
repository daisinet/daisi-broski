namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Math</c> — a single global object carrying mathematical
/// constants and static methods. Not a constructor; <c>new Math()</c>
/// is a TypeError. Most methods are thin wrappers around
/// <see cref="System.Math"/>; the handful that differ (like
/// <c>Math.min</c> / <c>Math.max</c> with no arguments returning
/// <c>Infinity</c> / <c>-Infinity</c>) are implemented explicitly.
/// </summary>
internal static class BuiltinMath
{
    public static void Install(JsEngine engine)
    {
        var math = new JsObject { Prototype = engine.ObjectPrototype };

        // Constants.
        math.SetNonEnumerable("PI", Math.PI);
        math.SetNonEnumerable("E", Math.E);
        math.SetNonEnumerable("LN2", Math.Log(2));
        math.SetNonEnumerable("LN10", Math.Log(10));
        math.SetNonEnumerable("LOG2E", 1.0 / Math.Log(2));
        math.SetNonEnumerable("LOG10E", 1.0 / Math.Log(10));
        math.SetNonEnumerable("SQRT2", Math.Sqrt(2));
        math.SetNonEnumerable("SQRT1_2", Math.Sqrt(0.5));

        // One-argument numeric methods.
        Builtins.Method(math, "abs", Unary(Math.Abs));
        Builtins.Method(math, "ceil", Unary(Math.Ceiling));
        Builtins.Method(math, "floor", Unary(Math.Floor));
        Builtins.Method(math, "round", Unary(JsRound));
        Builtins.Method(math, "sqrt", Unary(Math.Sqrt));
        Builtins.Method(math, "exp", Unary(Math.Exp));
        Builtins.Method(math, "log", Unary(Math.Log));
        Builtins.Method(math, "sin", Unary(Math.Sin));
        Builtins.Method(math, "cos", Unary(Math.Cos));
        Builtins.Method(math, "tan", Unary(Math.Tan));
        Builtins.Method(math, "asin", Unary(Math.Asin));
        Builtins.Method(math, "acos", Unary(Math.Acos));
        Builtins.Method(math, "atan", Unary(Math.Atan));
        Builtins.Method(math, "sign", Unary(n => double.IsNaN(n) ? double.NaN : (double)Math.Sign(n)));

        // Two-argument numeric methods.
        Builtins.Method(math, "pow", Binary(Math.Pow));
        Builtins.Method(math, "atan2", Binary(Math.Atan2));

        // Variadic min / max.
        Builtins.Method(math, "min", MinMax(true));
        Builtins.Method(math, "max", MinMax(false));

        // No-argument random.
        var rng = new Random();
        Builtins.Method(math, "random", (t, a) => rng.NextDouble());

        engine.Globals["Math"] = math;
    }

    private static Func<object?, IReadOnlyList<object?>, object?> Unary(Func<double, double> op) =>
        (thisVal, args) =>
        {
            if (args.Count == 0) return double.NaN;
            var n = JsValue.ToNumber(args[0]);
            if (double.IsNaN(n)) return double.NaN;
            return op(n);
        };

    private static Func<object?, IReadOnlyList<object?>, object?> Binary(Func<double, double, double> op) =>
        (thisVal, args) =>
        {
            if (args.Count < 2) return double.NaN;
            var a = JsValue.ToNumber(args[0]);
            var b = JsValue.ToNumber(args[1]);
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            return op(a, b);
        };

    private static Func<object?, IReadOnlyList<object?>, object?> MinMax(bool isMin) =>
        (thisVal, args) =>
        {
            // ECMA §15.8.2.11-12: Math.min() is +Infinity,
            // Math.max() is -Infinity. Any NaN arg yields NaN.
            double result = isMin ? double.PositiveInfinity : double.NegativeInfinity;
            foreach (var a in args)
            {
                var n = JsValue.ToNumber(a);
                if (double.IsNaN(n)) return double.NaN;
                result = isMin ? Math.Min(result, n) : Math.Max(result, n);
            }
            return result;
        };

    /// <summary>
    /// ECMA §15.8.2.15 — <c>Math.round</c>. Differs from
    /// <see cref="Math.Round(double)"/> in that JS rounds half
    /// values UP (toward +Infinity), not to even. So
    /// <c>Math.round(0.5)</c> is 1 and <c>Math.round(-0.5)</c>
    /// is 0, not -0.
    /// </summary>
    private static double JsRound(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n)) return n;
        return Math.Floor(n + 0.5);
    }
}
