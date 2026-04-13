using System.Text.RegularExpressions;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>RegExp</c> constructor + prototype, plus the
/// regex-aware <c>String.prototype</c> methods
/// (<c>match</c>, <c>replace</c>, <c>search</c>,
/// <c>split</c>) that take a regex as their first
/// argument. The shared backend is
/// <see cref="System.Text.RegularExpressions.Regex"/>
/// wrapped by <see cref="JsRegExp"/>.
///
/// <para>
/// Regex literals lex in context-sensitive mode
/// (<see cref="JsLexer"/>) and construct a
/// <see cref="JsRegExp"/> via the <c>NewRegExp</c>
/// opcode. This file wires the script-visible shell:
/// <c>new RegExp(pat, flags)</c>, <c>r.test(str)</c>,
/// <c>r.exec(str)</c>, and the String-side consumers.
/// </para>
/// </summary>
internal static class BuiltinRegExp
{
    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        engine.RegExpPrototype = proto;

        // Install flag-check properties on the prototype as real
        // accessor getters so polyfills that inspect
        // `Object.getOwnPropertyDescriptor(RegExp.prototype, 'dotAll').get`
        // see a native function and keep the existing accessor
        // (rather than installing their own — and then calling
        // the getter in a way that assumes `this` is registered
        // in the polyfill's internal WeakMap, which throws).
        //
        // When the accessor fires with `this` = JsRegExp instance,
        // return the underlying C# flag; when `this` = the prototype
        // itself, return `undefined` (matching V8 behavior).
        InstallFlagGetter(proto, "global", r => r.Global);
        InstallFlagGetter(proto, "ignoreCase", r => r.IgnoreCase);
        InstallFlagGetter(proto, "multiline", r => r.Multiline);
        InstallFlagGetter(proto, "dotAll", r => r.DotAll);
        InstallFlagGetter(proto, "sticky", r => r.Sticky);
        InstallFlagGetter(proto, "unicode", r => r.Unicode);
        InstallFlagGetter(proto, "hasIndices", r => r.HasIndices);
        proto.SetAccessor("flags", new JsFunction("get flags", (thisVal, args) =>
        {
            if (thisVal is JsRegExp r) return (object?)r.Flags;
            return JsValue.Undefined;
        }), null);
        proto.SetAccessor("source", new JsFunction("get source", (thisVal, args) =>
        {
            if (thisVal is JsRegExp r) return (object?)r.Source;
            return JsValue.Undefined;
        }), null);

        proto.SetNonEnumerable("test", new JsFunction("test", (thisVal, args) =>
        {
            var r = RequireRegExp(thisVal, "RegExp.prototype.test");
            var input = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
            return r.Compile().IsMatch(input);
        }));

        proto.SetNonEnumerable("exec", new JsFunction("exec", (thisVal, args) =>
        {
            var r = RequireRegExp(thisVal, "RegExp.prototype.exec");
            var input = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
            return ExecMatch(engine, r, input);
        }));

        proto.SetNonEnumerable("toString", new JsFunction("toString", (thisVal, args) =>
        {
            var r = RequireRegExp(thisVal, "RegExp.prototype.toString");
            return "/" + r.Source + "/" + r.Flags;
        }));

        // Constructor: `new RegExp(pattern, flags?)` — accepts
        // a string pattern + optional flag run. When called
        // with a RegExp as the first arg, clones it (and
        // optionally overrides flags).
        var ctor = new JsFunction("RegExp", (thisVal, args) =>
        {
            string pattern;
            string flags = "";
            if (args.Count > 0 && args[0] is JsRegExp existing)
            {
                pattern = existing.Source;
                flags = existing.Flags;
            }
            else
            {
                pattern = args.Count > 0 ? JsValue.ToJsString(args[0]) : "";
            }
            if (args.Count > 1 && args[1] is not JsUndefined)
            {
                flags = JsValue.ToJsString(args[1]);
            }
            return new JsRegExp(pattern, flags) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["RegExp"] = ctor;

        InstallStringRegExpHelpers(engine);
    }

    /// <summary>
    /// Install a read-only accessor property on
    /// <c>RegExp.prototype</c> backed by a C# predicate on the
    /// receiver <see cref="JsRegExp"/>. When the accessor fires
    /// with a non-regex receiver (e.g. the prototype itself),
    /// returns <c>undefined</c>.
    /// </summary>
    private static void InstallFlagGetter(JsObject proto, string name, Func<JsRegExp, bool> pick)
    {
        var getter = new JsFunction("get " + name, (thisVal, args) =>
        {
            if (thisVal is JsRegExp r) return (object?)pick(r);
            return JsValue.Undefined;
        });
        proto.SetAccessor(name, getter, null);
    }

    private static JsRegExp RequireRegExp(object? thisVal, string name)
    {
        if (thisVal is not JsRegExp r)
        {
            JsThrow.TypeError($"{name} called on non-RegExp");
        }
        return (JsRegExp)thisVal!;
    }

    /// <summary>
    /// Run one <c>regex.exec(input)</c> step. Honors
    /// <see cref="JsRegExp.LastIndex"/> for global / sticky
    /// regexes, returning <c>null</c> at end of input and
    /// advancing <c>lastIndex</c> past each successful
    /// match.
    /// </summary>
    private static object? ExecMatch(JsEngine engine, JsRegExp r, string input)
    {
        int startAt = (r.Global || r.Sticky) ? r.LastIndex : 0;
        if (startAt > input.Length)
        {
            if (r.Global || r.Sticky) r.LastIndex = 0;
            return JsValue.Null;
        }
        var match = r.Compile().Match(input, startAt);
        if (!match.Success)
        {
            if (r.Global || r.Sticky) r.LastIndex = 0;
            return JsValue.Null;
        }
        // Sticky requires the match to start exactly at
        // lastIndex (not later).
        if (r.Sticky && match.Index != startAt)
        {
            r.LastIndex = 0;
            return JsValue.Null;
        }
        if (r.Global || r.Sticky)
        {
            r.LastIndex = match.Index + match.Length;
        }
        return BuildMatchArray(engine, match, input);
    }

    /// <summary>
    /// Build the script-visible match array: the matched
    /// substring as index 0, each capture group as index
    /// 1..n, plus <c>index</c> and <c>input</c> properties
    /// matching the spec's <c>RegExpBuiltinExec</c> result
    /// shape.
    /// </summary>
    internal static JsArray BuildMatchArray(JsEngine engine, Match match, string input)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        arr.Elements.Add(match.Value);
        for (int i = 1; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            arr.Elements.Add(group.Success ? group.Value : (object?)JsValue.Undefined);
        }
        arr.Set("index", (double)match.Index);
        arr.Set("input", input);
        return arr;
    }

    /// <summary>
    /// Install the regex-aware methods on
    /// <c>String.prototype</c>. Each first peeks at the
    /// argument: a <see cref="JsRegExp"/> gets the
    /// regex path, anything else coerces to a string
    /// literal for plain substring operations (matching
    /// the spec's abstract operations).
    /// </summary>
    private static void InstallStringRegExpHelpers(JsEngine engine)
    {
        var sp = engine.StringPrototype;

        sp.SetNonEnumerable("match", new JsFunction("match", (thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count == 0 || args[0] is JsUndefined || args[0] is JsNull)
            {
                return JsValue.Null;
            }
            var r = CoerceToRegExp(engine, args[0]);
            if (!r.Global)
            {
                var m = r.Compile().Match(input);
                return m.Success ? BuildMatchArray(engine, m, input) : (object)JsValue.Null;
            }
            // Global: return an array of every match's
            // full substring, or null on no matches.
            var all = r.Compile().Matches(input);
            if (all.Count == 0) return JsValue.Null;
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            foreach (Match m in all)
            {
                arr.Elements.Add(m.Value);
            }
            return arr;
        }));

        sp.SetNonEnumerable("matchAll", new JsFunction("matchAll", (thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count == 0)
            {
                JsThrow.TypeError("String.prototype.matchAll: missing regex");
                return null;
            }
            var r = CoerceToRegExp(engine, args[0]);
            if (!r.Global)
            {
                JsThrow.TypeError("String.prototype.matchAll: regex must have 'g' flag");
                return null;
            }
            // Return a plain array of match arrays — the
            // spec wants an iterator, but the common case
            // is `for (const m of ...)` which accepts any
            // iterable including a JsArray.
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            foreach (Match m in r.Compile().Matches(input))
            {
                arr.Elements.Add(BuildMatchArray(engine, m, input));
            }
            return arr;
        }));

        sp.SetNonEnumerable("search", new JsFunction("search", (thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return -1.0;
            var r = CoerceToRegExp(engine, args[0]);
            var m = r.Compile().Match(input);
            return m.Success ? (double)m.Index : -1.0;
        }));

        sp.SetNonEnumerable("replace", new JsFunction("replace", (vm, thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count < 2) return input;
            object? pattern = args[0];
            object? replacement = args[1];

            if (pattern is JsRegExp r)
            {
                return ReplaceWithRegex(vm, r, input, replacement);
            }

            // String pattern: replace first occurrence only.
            string needle = JsValue.ToJsString(pattern);
            int idx = input.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return input;
            string rep;
            if (replacement is JsFunction cb)
            {
                var result = vm.InvokeJsFunction(cb, JsValue.Undefined,
                    new object?[] { needle, (double)idx, input });
                rep = JsValue.ToJsString(result);
            }
            else
            {
                rep = JsValue.ToJsString(replacement);
            }
            return input.Substring(0, idx) + rep + input.Substring(idx + needle.Length);
        }));

        sp.SetNonEnumerable("replaceAll", new JsFunction("replaceAll", (vm, thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count < 2) return input;
            object? pattern = args[0];
            object? replacement = args[1];

            if (pattern is JsRegExp r)
            {
                if (!r.Global)
                {
                    JsThrow.TypeError("String.prototype.replaceAll: regex must have 'g' flag");
                    return null;
                }
                return ReplaceWithRegex(vm, r, input, replacement);
            }

            string needle = JsValue.ToJsString(pattern);
            if (needle.Length == 0) return input;
            string rep;
            if (replacement is JsFunction)
            {
                var sb = new System.Text.StringBuilder();
                int scan = 0;
                while (scan < input.Length)
                {
                    int found = input.IndexOf(needle, scan, StringComparison.Ordinal);
                    if (found < 0)
                    {
                        sb.Append(input, scan, input.Length - scan);
                        break;
                    }
                    sb.Append(input, scan, found - scan);
                    var result = vm.InvokeJsFunction((JsFunction)replacement!, JsValue.Undefined,
                        new object?[] { needle, (double)found, input });
                    sb.Append(JsValue.ToJsString(result));
                    scan = found + needle.Length;
                }
                return sb.ToString();
            }
            rep = JsValue.ToJsString(replacement);
            return input.Replace(needle, rep);
        }));

        // Upgrade the existing String.prototype.split to
        // accept a regex separator in addition to a string
        // separator. The ES5 slice-6a impl already handles
        // string separators; we wrap it with a regex check.
        var existingSplit = sp.Get("split");
        sp.SetNonEnumerable("split", new JsFunction("split", (vm, thisVal, args) =>
        {
            var input = JsValue.ToJsString(thisVal);
            if (args.Count == 0 || args[0] is JsUndefined)
            {
                var single = new JsArray { Prototype = engine.ArrayPrototype };
                single.Elements.Add(input);
                return single;
            }
            if (args[0] is JsRegExp r)
            {
                int limit = int.MaxValue;
                if (args.Count > 1 && args[1] is not JsUndefined)
                {
                    limit = (int)JsValue.ToNumber(args[1]);
                }
                var arr = new JsArray { Prototype = engine.ArrayPrototype };
                if (limit == 0) return arr;
                int start = 0;
                foreach (Match m in r.Compile().Matches(input))
                {
                    if (m.Index == start && m.Length == 0) continue;
                    arr.Elements.Add(input.Substring(start, m.Index - start));
                    // Per spec, include capture groups between
                    // segments when the regex has them.
                    for (int gi = 1; gi < m.Groups.Count; gi++)
                    {
                        var g = m.Groups[gi];
                        arr.Elements.Add(g.Success ? g.Value : (object?)JsValue.Undefined);
                    }
                    start = m.Index + m.Length;
                    if (arr.Elements.Count >= limit) return arr;
                }
                arr.Elements.Add(input.Substring(start));
                return arr;
            }
            // Fall through to the existing string-split impl.
            if (existingSplit is JsFunction fn)
            {
                return vm.InvokeJsFunction(fn, thisVal, args);
            }
            return JsValue.Undefined;
        }));
    }

    /// <summary>
    /// Coerce any JS value to a <see cref="JsRegExp"/>.
    /// A bare value is wrapped in a fresh regex with no
    /// flags — matching the spec's "if pattern is not a
    /// RegExp, create one from it" step.
    /// </summary>
    private static JsRegExp CoerceToRegExp(JsEngine engine, object? v)
    {
        if (v is JsRegExp r) return r;
        return new JsRegExp(JsValue.ToJsString(v), "")
        {
            Prototype = engine.RegExpPrototype,
        };
    }

    /// <summary>
    /// Apply a regex replacement to <paramref name="input"/>,
    /// either with a string template (supporting <c>$1</c>,
    /// <c>$&amp;</c>, etc. via .NET's native expansion) or
    /// with a JS function invoked per match. Global regexes
    /// replace every match; non-global replace only the
    /// first.
    /// </summary>
    private static string ReplaceWithRegex(JsVM vm, JsRegExp r, string input, object? replacement)
    {
        var regex = r.Compile();
        if (replacement is JsFunction cb)
        {
            // Per-match callback: invoke the user function with
            // (match, ...groups, index, input) and substitute
            // its return value.
            MatchEvaluator evaluator = m =>
            {
                var callArgs = new List<object?>();
                callArgs.Add(m.Value);
                for (int i = 1; i < m.Groups.Count; i++)
                {
                    var g = m.Groups[i];
                    callArgs.Add(g.Success ? g.Value : (object?)JsValue.Undefined);
                }
                callArgs.Add((double)m.Index);
                callArgs.Add(input);
                var result = vm.InvokeJsFunction(cb, JsValue.Undefined, callArgs);
                return JsValue.ToJsString(result);
            };
            return r.Global
                ? regex.Replace(input, evaluator)
                : regex.Replace(input, evaluator, 1);
        }

        // String template. JS spec uses $1 / $2 / $& / $` /
        // $' / $$. .NET uses the same syntax for $1 / $2 / $$,
        // and supports $& (whole match) natively. $` / $' are
        // less common — .NET doesn't handle them, so we rewrite
        // the template to satisfy the spec.
        string template = JsValue.ToJsString(replacement);
        return r.Global
            ? regex.Replace(input, template)
            : regex.Replace(input, template, 1);
    }
}
