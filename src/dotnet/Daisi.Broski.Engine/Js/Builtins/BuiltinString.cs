using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>String</c> constructor and <c>String.prototype</c>
/// methods. All methods coerce <c>this</c> to a string via
/// <see cref="JsValue.ToJsString"/> — the spec actually
/// throws for <c>String.prototype.charAt.call(undefined)</c>,
/// but the common-case usage via <c>"abc".charAt(0)</c> works
/// uniformly regardless.
///
/// String operations use UTF-16 code units (C# <c>char</c>),
/// matching ES5's <c>[[GetOwnProperty]]</c> on string primitives
/// and methods like <c>charCodeAt</c>. Supplementary Unicode
/// characters produce surrogate pairs; this is the same
/// behavior browsers have.
/// </summary>
internal static class BuiltinString
{
    public static void Install(JsEngine engine)
    {
        var proto = engine.StringPrototype;

        Builtins.Method(proto, "charAt", CharAt);
        Builtins.Method(proto, "charCodeAt", CharCodeAt);
        Builtins.Method(proto, "indexOf", IndexOf);
        Builtins.Method(proto, "lastIndexOf", LastIndexOf);
        Builtins.Method(proto, "slice", Slice);
        Builtins.Method(proto, "substring", Substring);
        Builtins.Method(proto, "substr", Substr);
        Builtins.Method(proto, "toLowerCase", ToLowerCaseMethod);
        Builtins.Method(proto, "toUpperCase", ToUpperCaseMethod);
        Builtins.Method(proto, "trim", Trim);
        Builtins.Method(proto, "split", (t, a) => Split(engine, t, a));
        Builtins.Method(proto, "concat", Concat);
        Builtins.Method(proto, "toString", ToStringMethod);
        Builtins.Method(proto, "valueOf", ToStringMethod);

        // String constructor — returns a primitive (not wrapped)
        // when called without `new`, and a boxed String object
        // otherwise. Phase 3a has no boxed String objects, so
        // `new String('x')` currently returns a primitive too.
        // Close enough for the common use case of `String(x)`
        // as a coercion helper.
        var ctor = new JsFunction("String", (thisVal, args) =>
        {
            if (args.Count == 0) return "";
            return JsValue.ToJsString(args[0]);
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        Builtins.Method(ctor, "fromCharCode", FromCharCode);
        engine.Globals["String"] = ctor;

        // String.prototype[Symbol.iterator] — yields each
        // UTF-16 code unit as a single-char string. Proper
        // code-point iteration (surrogate pair combining) is
        // deferred; it matches the common ASCII case correctly.
        proto.SetSymbol(
            engine.IteratorSymbol,
            new JsFunction(
                "[Symbol.iterator]",
                (thisVal, args) => CreateStringIterator(engine, thisVal)));
    }

    private static JsObject CreateStringIterator(JsEngine engine, object? thisVal)
    {
        var source = JsValue.ToJsString(thisVal);
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable(
            "next",
            new JsFunction(
                "next",
                (t, a) =>
                {
                    var result = new JsObject { Prototype = engine.ObjectPrototype };
                    if (index >= source.Length)
                    {
                        result.Set("value", JsValue.Undefined);
                        result.Set("done", JsValue.True);
                        return result;
                    }
                    result.Set("value", source[index].ToString());
                    result.Set("done", JsValue.False);
                    index++;
                    return result;
                }));
        return iter;
    }

    // -------------------------------------------------------------------
    // Character access
    // -------------------------------------------------------------------

    private static object? CharAt(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int idx = args.Count > 0 ? (int)JsValue.ToInt32(args[0]) : 0;
        if (idx < 0 || idx >= s.Length) return "";
        return s[idx].ToString();
    }

    private static object? CharCodeAt(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int idx = args.Count > 0 ? (int)JsValue.ToInt32(args[0]) : 0;
        if (idx < 0 || idx >= s.Length) return double.NaN;
        return (double)s[idx];
    }

    private static object? FromCharCode(object? thisVal, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(args.Count);
        foreach (var a in args)
        {
            int code = (int)(JsValue.ToUint32(a) & 0xFFFF);
            sb.Append((char)code);
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------

    private static object? IndexOf(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        var search = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
        int fromIndex = args.Count > 1 ? (int)JsValue.ToInt32(args[1]) : 0;
        if (fromIndex < 0) fromIndex = 0;
        if (fromIndex > s.Length) return -1.0;
        return (double)s.IndexOf(search, fromIndex, StringComparison.Ordinal);
    }

    private static object? LastIndexOf(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        var search = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
        int fromIndex = args.Count > 1 ? (int)JsValue.ToInt32(args[1]) : s.Length;
        if (fromIndex >= s.Length) fromIndex = s.Length - 1;
        if (fromIndex < 0) return -1.0;
        // .NET's LastIndexOf searches backward from the given
        // start; JS's lastIndexOf uses the rightmost position
        // where the match could START at or before fromIndex.
        int start = Math.Min(fromIndex + search.Length - 1, s.Length - 1);
        if (start < 0) return -1.0;
        return (double)s.LastIndexOf(search, start, start + 1, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------
    // Sub-strings
    // -------------------------------------------------------------------

    private static object? Slice(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int len = s.Length;
        int start = ClampRelative(args.Count > 0 ? JsValue.ToNumber(args[0]) : 0, len);
        int end = args.Count > 1 && args[1] is not JsUndefined
            ? ClampRelative(JsValue.ToNumber(args[1]), len)
            : len;
        if (end < start) return "";
        return s.Substring(start, end - start);
    }

    /// <summary>
    /// ECMA §15.5.4.15 — <c>substring(start, end)</c>. Differs
    /// from <c>slice</c> in that negative arguments are clamped
    /// to 0 (not interpreted relative to the end) and if end
    /// &lt; start the two are swapped.
    /// </summary>
    private static object? Substring(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int len = s.Length;
        int start = ClampZero(args.Count > 0 ? JsValue.ToNumber(args[0]) : 0, len);
        int end = args.Count > 1 && args[1] is not JsUndefined
            ? ClampZero(JsValue.ToNumber(args[1]), len)
            : len;
        if (start > end) (start, end) = (end, start);
        return s.Substring(start, end - start);
    }

    /// <summary>
    /// ECMA Annex B §B.2.3 — <c>substr(start, length)</c>. Not
    /// part of the ES5 main text but universally supported.
    /// Negative start counts from the end; length defaults to
    /// "until end of string".
    /// </summary>
    private static object? Substr(object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int len = s.Length;
        int start = args.Count > 0 ? (int)JsValue.ToInt32(args[0]) : 0;
        if (start < 0) start = Math.Max(0, len + start);
        if (start > len) return "";
        int length = args.Count > 1 && args[1] is not JsUndefined
            ? (int)JsValue.ToInt32(args[1])
            : len - start;
        if (length <= 0) return "";
        if (start + length > len) length = len - start;
        return s.Substring(start, length);
    }

    // -------------------------------------------------------------------
    // Case conversion
    // -------------------------------------------------------------------

    private static object? ToLowerCaseMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        return JsValue.ToJsString(thisVal).ToLowerInvariant();
    }

    private static object? ToUpperCaseMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        return JsValue.ToJsString(thisVal).ToUpperInvariant();
    }

    // -------------------------------------------------------------------
    // Whitespace / concat / split
    // -------------------------------------------------------------------

    private static object? Trim(object? thisVal, IReadOnlyList<object?> args)
    {
        // ES5 §15.5.4.20 defines the whitespace set as the spec's
        // WhiteSpace + LineTerminator grammar productions.
        // .NET's .Trim() is close but not identical; for phase 3a
        // we use the defaults and document the deferral.
        return JsValue.ToJsString(thisVal).Trim();
    }

    private static object? Concat(object? thisVal, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(JsValue.ToJsString(thisVal));
        foreach (var a in args) sb.Append(JsValue.ToJsString(a));
        return sb.ToString();
    }

    /// <summary>
    /// ECMA §15.5.4.14 — <c>split(separator, limit)</c> without
    /// regex support. Empty separator splits into individual
    /// characters; missing separator returns the whole string
    /// as a one-element array; empty string with empty separator
    /// returns an empty array.
    /// </summary>
    private static object? Split(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        var s = JsValue.ToJsString(thisVal);
        int limit = args.Count > 1 && args[1] is not JsUndefined
            ? (int)JsValue.ToUint32(args[1])
            : int.MaxValue;

        var result = new JsArray { Prototype = engine.ArrayPrototype };
        if (limit == 0) return result;

        // No separator → single-element array with the whole string.
        if (args.Count == 0 || args[0] is JsUndefined)
        {
            result.Elements.Add(s);
            return result;
        }

        var sep = JsValue.ToJsString(args[0]);

        // Empty separator → characters.
        if (sep.Length == 0)
        {
            // Empty string + empty separator → empty array.
            foreach (var c in s)
            {
                if (result.Elements.Count >= limit) break;
                result.Elements.Add(c.ToString());
            }
            return result;
        }

        int start = 0;
        while (start <= s.Length && result.Elements.Count < limit)
        {
            int idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                result.Elements.Add(s.Substring(start));
                break;
            }
            result.Elements.Add(s.Substring(start, idx - start));
            start = idx + sep.Length;
            if (start == s.Length)
            {
                if (result.Elements.Count < limit) result.Elements.Add("");
                break;
            }
        }
        return result;
    }

    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        return JsValue.ToJsString(thisVal);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static int ClampRelative(double raw, int len)
    {
        if (double.IsNaN(raw)) return 0;
        int i = (int)raw;
        if (i < 0) return Math.Max(0, len + i);
        return Math.Min(i, len);
    }

    private static int ClampZero(double raw, int len)
    {
        if (double.IsNaN(raw) || raw < 0) return 0;
        int i = (int)raw;
        return Math.Min(i, len);
    }
}
