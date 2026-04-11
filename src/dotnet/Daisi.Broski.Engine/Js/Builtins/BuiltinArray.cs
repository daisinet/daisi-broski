using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Array</c> constructor and the non-callback methods of
/// <c>Array.prototype</c>: <c>push</c>, <c>pop</c>, <c>shift</c>,
/// <c>unshift</c>, <c>slice</c>, <c>concat</c>, <c>join</c>,
/// <c>indexOf</c>, <c>reverse</c>, <c>sort</c> (without
/// compareFn). Plus the static <c>Array.isArray</c>.
///
/// Callback-taking methods (<c>forEach</c>, <c>map</c>,
/// <c>filter</c>, <c>reduce</c>, <c>every</c>, <c>some</c>,
/// <c>sort</c> with compareFn) are slice 6b; they require the
/// VM to invoke a JS function from a native callback, which
/// means a re-entrant dispatch loop.
/// </summary>
internal static class BuiltinArray
{
    public static void Install(JsEngine engine)
    {
        var proto = engine.ArrayPrototype;

        Builtins.Method(proto, "push", Push);
        Builtins.Method(proto, "pop", Pop);
        Builtins.Method(proto, "shift", Shift);
        Builtins.Method(proto, "unshift", Unshift);
        Builtins.Method(proto, "slice", (t, a) => Slice(engine, t, a));
        Builtins.Method(proto, "concat", (t, a) => Concat(engine, t, a));
        Builtins.Method(proto, "join", Join);
        Builtins.Method(proto, "indexOf", IndexOf);
        Builtins.Method(proto, "reverse", Reverse);
        Builtins.Method(proto, "toString", ToStringMethod);

        // Callback-taking methods — these need a VM reference to
        // invoke the user callback synchronously. sort replaces
        // the slice-6a throw-on-compareFn with a real compare
        // dispatcher, and also handles the no-compareFn case.
        proto.SetNonEnumerable("sort",
            new JsFunction("sort", (vm, t, a) => SortWithCompare(vm, t, a)));
        proto.SetNonEnumerable("forEach",
            new JsFunction("forEach", (vm, t, a) => ForEach(vm, t, a)));
        proto.SetNonEnumerable("map",
            new JsFunction("map", (vm, t, a) => Map(vm, engine, t, a)));
        proto.SetNonEnumerable("filter",
            new JsFunction("filter", (vm, t, a) => Filter(vm, engine, t, a)));
        proto.SetNonEnumerable("reduce",
            new JsFunction("reduce", (vm, t, a) => Reduce(vm, t, a, fromLeft: true)));
        proto.SetNonEnumerable("reduceRight",
            new JsFunction("reduceRight", (vm, t, a) => Reduce(vm, t, a, fromLeft: false)));
        proto.SetNonEnumerable("every",
            new JsFunction("every", (vm, t, a) => Every(vm, t, a)));
        proto.SetNonEnumerable("some",
            new JsFunction("some", (vm, t, a) => Some(vm, t, a)));

        // Array constructor.
        var ctor = new JsFunction("Array", (thisVal, args) => Construct(engine, args));
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        Builtins.Method(ctor, "isArray", IsArray);
        engine.Globals["Array"] = ctor;

        // Array.prototype[Symbol.iterator] — returns an
        // iterator that walks the array's dense storage
        // (capturing a live reference, not a snapshot).
        proto.SetSymbol(
            engine.IteratorSymbol,
            new JsFunction(
                "[Symbol.iterator]",
                (thisVal, args) => CreateArrayIterator(engine, thisVal)));
    }

    /// <summary>
    /// Build an iterator object for an array-like receiver.
    /// The iterator captures the array reference and an
    /// index; each <c>next()</c> call advances the index and
    /// returns <c>{value, done}</c>. Iteration follows the
    /// live <c>length</c> so mutations during iteration are
    /// observed — consistent with how real engines treat
    /// array iteration.
    /// </summary>
    private static JsObject CreateArrayIterator(JsEngine engine, object? thisVal)
    {
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        int index = 0;
        iter.SetNonEnumerable(
            "next",
            new JsFunction(
                "next",
                (t, a) =>
                {
                    var src = thisVal as JsArray;
                    var result = new JsObject { Prototype = engine.ObjectPrototype };
                    if (src is null || index >= src.Elements.Count)
                    {
                        result.Set("value", JsValue.Undefined);
                        result.Set("done", JsValue.True);
                        return result;
                    }
                    result.Set("value", src.Elements[index]);
                    result.Set("done", JsValue.False);
                    index++;
                    return result;
                }));
        return iter;
    }

    // -------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §15.4.2 — <c>Array(len)</c> creates an array with
    /// the given length, <c>Array(e0, e1, ...)</c> with those
    /// elements. Called as <c>new Array(...)</c> or
    /// <c>Array(...)</c>; both behave identically here.
    /// </summary>
    private static object? Construct(JsEngine engine, IReadOnlyList<object?> args)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };

        if (args.Count == 1 && args[0] is double d)
        {
            if (d != Math.Truncate(d) || d < 0 || d > uint.MaxValue)
            {
                return JsThrow.RangeError("Invalid array length");
            }
            int len = (int)d;
            for (int i = 0; i < len; i++) arr.Elements.Add(JsValue.Undefined);
            return arr;
        }

        foreach (var a in args) arr.Elements.Add(a);
        return arr;
    }

    private static object? IsArray(object? thisVal, IReadOnlyList<object?> args) =>
        args.Count > 0 && args[0] is JsArray;

    // -------------------------------------------------------------------
    // Stack / queue operations
    // -------------------------------------------------------------------

    private static object? Push(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "push");
        foreach (var a in args) arr.Elements.Add(a);
        return (double)arr.Elements.Count;
    }

    private static object? Pop(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "pop");
        if (arr.Elements.Count == 0) return JsValue.Undefined;
        int last = arr.Elements.Count - 1;
        var value = arr.Elements[last];
        arr.Elements.RemoveAt(last);
        return value;
    }

    private static object? Shift(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "shift");
        if (arr.Elements.Count == 0) return JsValue.Undefined;
        var value = arr.Elements[0];
        arr.Elements.RemoveAt(0);
        return value;
    }

    private static object? Unshift(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "unshift");
        for (int i = args.Count - 1; i >= 0; i--)
        {
            arr.Elements.Insert(0, args[i]);
        }
        return (double)arr.Elements.Count;
    }

    // -------------------------------------------------------------------
    // Copying / concatenation
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §15.4.4.10 — <c>slice(start, end)</c>. Start and
    /// end may be negative (counted from the end). End defaults
    /// to <c>length</c>. Returns a shallow copy of the
    /// half-open range.
    /// </summary>
    private static object? Slice(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "slice");
        int len = arr.Elements.Count;
        int start = ClampRelativeIndex(args.Count > 0 ? JsValue.ToNumber(args[0]) : 0, len);
        int end = args.Count > 1 && args[1] is not JsUndefined
            ? ClampRelativeIndex(JsValue.ToNumber(args[1]), len)
            : len;

        var result = new JsArray { Prototype = engine.ArrayPrototype };
        for (int i = start; i < end; i++)
        {
            result.Elements.Add(arr.Elements[i]);
        }
        return result;
    }

    private static object? Concat(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "concat");
        var result = new JsArray { Prototype = engine.ArrayPrototype };
        foreach (var e in arr.Elements) result.Elements.Add(e);
        foreach (var a in args)
        {
            if (a is JsArray other)
            {
                foreach (var e in other.Elements) result.Elements.Add(e);
            }
            else
            {
                result.Elements.Add(a);
            }
        }
        return result;
    }

    // -------------------------------------------------------------------
    // Stringification
    // -------------------------------------------------------------------

    private static object? Join(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "join");
        string sep = args.Count > 0 && args[0] is not JsUndefined
            ? JsValue.ToJsString(args[0])
            : ",";
        return arr.Join(sep);
    }

    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "toString");
        return arr.Join(",");
    }

    // -------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §15.4.4.14 — <c>indexOf(searchElement, fromIndex)</c>.
    /// Uses strict equality. fromIndex may be negative.
    /// </summary>
    private static object? IndexOf(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "indexOf");
        if (args.Count == 0) return -1.0;
        var search = args[0];
        int len = arr.Elements.Count;
        int start = args.Count > 1 ? (int)JsValue.ToInt32(args[1]) : 0;
        if (start < 0) start = Math.Max(0, len + start);
        for (int i = start; i < len; i++)
        {
            if (JsValue.StrictEquals(arr.Elements[i], search)) return (double)i;
        }
        return -1.0;
    }

    // -------------------------------------------------------------------
    // In-place mutation
    // -------------------------------------------------------------------

    private static object? Reverse(object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "reverse");
        arr.Elements.Reverse();
        return arr;
    }

    /// <summary>
    /// ECMA §15.4.4.11 — <c>sort([compareFn])</c>. Without a
    /// compareFn, elements are coerced to strings and compared
    /// ordinally (undefined sorts to the end). With a compareFn,
    /// each pair-wise comparison invokes the callback via
    /// <see cref="JsVM.InvokeJsFunction"/>; the return value's
    /// sign determines order.
    /// </summary>
    private static object? SortWithCompare(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "sort");
        JsFunction? cmp = args.Count > 0 && args[0] is JsFunction f ? f : null;
        arr.Elements.Sort((a, b) =>
        {
            if (cmp is not null)
            {
                var r = vm.InvokeJsFunction(cmp, JsValue.Undefined, new[] { a, b });
                double n = JsValue.ToNumber(r);
                if (double.IsNaN(n) || n == 0) return 0;
                return n < 0 ? -1 : 1;
            }
            bool aUndef = a is JsUndefined;
            bool bUndef = b is JsUndefined;
            if (aUndef && bUndef) return 0;
            if (aUndef) return 1;
            if (bUndef) return -1;
            return string.CompareOrdinal(JsValue.ToJsString(a), JsValue.ToJsString(b));
        });
        return arr;
    }

    // -------------------------------------------------------------------
    // Callback-taking methods
    // -------------------------------------------------------------------

    private static object? ForEach(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "forEach");
        var cb = RequireFunction(args, 0, "forEach");
        object? thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            vm.InvokeJsFunction(cb, thisArg,
                new[] { arr.Elements[i], (object?)(double)i, (object?)arr });
        }
        return JsValue.Undefined;
    }

    private static object? Map(JsVM vm, JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "map");
        var cb = RequireFunction(args, 0, "map");
        object? thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var result = new JsArray { Prototype = engine.ArrayPrototype };
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var mapped = vm.InvokeJsFunction(cb, thisArg,
                new[] { arr.Elements[i], (object?)(double)i, (object?)arr });
            result.Elements.Add(mapped);
        }
        return result;
    }

    private static object? Filter(JsVM vm, JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "filter");
        var cb = RequireFunction(args, 0, "filter");
        object? thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var result = new JsArray { Prototype = engine.ArrayPrototype };
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var pass = vm.InvokeJsFunction(cb, thisArg,
                new[] { arr.Elements[i], (object?)(double)i, (object?)arr });
            if (JsValue.ToBoolean(pass))
            {
                result.Elements.Add(arr.Elements[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// ECMA §15.4.4.21 / §15.4.4.22 — <c>reduce</c> and
    /// <c>reduceRight</c>. If no initial value is given, the
    /// first / last element becomes the accumulator seed. Empty
    /// array with no initial value is a TypeError.
    /// </summary>
    private static object? Reduce(JsVM vm, object? thisVal, IReadOnlyList<object?> args, bool fromLeft)
    {
        var arr = RequireArray(thisVal, fromLeft ? "reduce" : "reduceRight");
        var cb = RequireFunction(args, 0, fromLeft ? "reduce" : "reduceRight");
        int len = arr.Elements.Count;
        bool hasInitial = args.Count > 1;
        object? accum;
        int i;
        int step = fromLeft ? 1 : -1;
        int end = fromLeft ? len : -1;

        if (hasInitial)
        {
            accum = args[1];
            i = fromLeft ? 0 : len - 1;
        }
        else
        {
            if (len == 0)
            {
                return JsThrow.TypeError("Reduce of empty array with no initial value");
            }
            i = fromLeft ? 0 : len - 1;
            accum = arr.Elements[i];
            i += step;
        }

        for (; i != end; i += step)
        {
            accum = vm.InvokeJsFunction(cb, JsValue.Undefined,
                new[] { accum, arr.Elements[i], (object?)(double)i, (object?)arr });
        }
        return accum;
    }

    private static object? Every(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "every");
        var cb = RequireFunction(args, 0, "every");
        object? thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var pass = vm.InvokeJsFunction(cb, thisArg,
                new[] { arr.Elements[i], (object?)(double)i, (object?)arr });
            if (!JsValue.ToBoolean(pass)) return false;
        }
        return true;
    }

    private static object? Some(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        var arr = RequireArray(thisVal, "some");
        var cb = RequireFunction(args, 0, "some");
        object? thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var pass = vm.InvokeJsFunction(cb, thisArg,
                new[] { arr.Elements[i], (object?)(double)i, (object?)arr });
            if (JsValue.ToBoolean(pass)) return true;
        }
        return false;
    }

    private static JsFunction RequireFunction(IReadOnlyList<object?> args, int index, string method)
    {
        if (index < args.Count && args[index] is JsFunction fn) return fn;
        JsThrow.TypeError($"Array.prototype.{method} callback must be a function");
        return null!;
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static JsArray RequireArray(object? thisVal, string method)
    {
        if (thisVal is JsArray arr) return arr;
        JsThrow.TypeError($"Array.prototype.{method} called on non-array");
        return null!; // Unreachable — JsThrow always throws.
    }

    /// <summary>
    /// Normalize a possibly-negative relative index into a
    /// [0, length] clamped integer, matching ECMA's
    /// "K = If start is negative, max(len+start, 0), else min(start, len)"
    /// pattern used by slice / splice / substring.
    /// </summary>
    private static int ClampRelativeIndex(double raw, int len)
    {
        if (double.IsNaN(raw)) return 0;
        int i = (int)raw;
        if (i < 0) return Math.Max(0, len + i);
        return Math.Min(i, len);
    }
}
