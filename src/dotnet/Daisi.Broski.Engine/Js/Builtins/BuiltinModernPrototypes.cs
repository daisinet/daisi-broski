using System.Globalization;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2015+ additions to <c>Array.prototype</c>,
/// <c>Object</c>, <c>String.prototype</c>, and
/// <c>Number</c> that the original slice-6a / slice-6b
/// built-in library shipped without. Installed as a
/// separate file so the phase-3a files stay untouched —
/// every method here layers onto an existing prototype.
///
/// <para>
/// Scope picked to match what modern real-world scripts
/// actually call: framework runtimes, bundler helpers,
/// data libraries, and plain app code all reach for
/// <c>Array.from</c>, <c>Object.assign</c>,
/// <c>padStart</c>, <c>String.prototype.startsWith</c>,
/// <c>Number.isInteger</c>, etc. Before this slice
/// scripts that touched any of them got
/// <c>undefined is not a function</c>.
/// </para>
/// </summary>
internal static class BuiltinModernPrototypes
{
    public static void Install(JsEngine engine)
    {
        InstallArrayAdditions(engine);
        InstallObjectAdditions(engine);
        InstallStringAdditions(engine);
        InstallNumberAdditions(engine);
    }

    // =======================================================
    // Array.prototype additions
    // =======================================================

    private static void InstallArrayAdditions(JsEngine engine)
    {
        var proto = engine.ArrayPrototype;

        proto.SetNonEnumerable("find", new JsFunction("find", (vm, thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.find");
            if (args.Count == 0 || args[0] is not JsFunction cb) return JsValue.Undefined;
            for (int i = 0; i < arr.Elements.Count; i++)
            {
                var el = arr.Elements[i];
                var r = vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { el, (double)i, arr });
                if (JsValue.ToBoolean(r)) return el;
            }
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("findIndex", new JsFunction("findIndex", (vm, thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.findIndex");
            if (args.Count == 0 || args[0] is not JsFunction cb) return -1.0;
            for (int i = 0; i < arr.Elements.Count; i++)
            {
                var r = vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { arr.Elements[i], (double)i, arr });
                if (JsValue.ToBoolean(r)) return (double)i;
            }
            return -1.0;
        }));

        proto.SetNonEnumerable("findLast", new JsFunction("findLast", (vm, thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.findLast");
            if (args.Count == 0 || args[0] is not JsFunction cb) return JsValue.Undefined;
            for (int i = arr.Elements.Count - 1; i >= 0; i--)
            {
                var el = arr.Elements[i];
                var r = vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { el, (double)i, arr });
                if (JsValue.ToBoolean(r)) return el;
            }
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("findLastIndex", new JsFunction("findLastIndex", (vm, thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.findLastIndex");
            if (args.Count == 0 || args[0] is not JsFunction cb) return -1.0;
            for (int i = arr.Elements.Count - 1; i >= 0; i--)
            {
                var r = vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { arr.Elements[i], (double)i, arr });
                if (JsValue.ToBoolean(r)) return (double)i;
            }
            return -1.0;
        }));

        proto.SetNonEnumerable("includes", new JsFunction("includes", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.includes");
            if (args.Count == 0) return false;
            var target = args[0];
            int fromIndex = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            if (fromIndex < 0) fromIndex = Math.Max(0, arr.Elements.Count + fromIndex);
            for (int i = fromIndex; i < arr.Elements.Count; i++)
            {
                if (SameValueZero(arr.Elements[i], target)) return true;
            }
            return false;
        }));

        proto.SetNonEnumerable("lastIndexOf", new JsFunction("lastIndexOf", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.lastIndexOf");
            if (args.Count == 0 || arr.Elements.Count == 0) return -1.0;
            var target = args[0];
            int fromIndex = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : arr.Elements.Count - 1;
            if (fromIndex < 0) fromIndex = arr.Elements.Count + fromIndex;
            if (fromIndex >= arr.Elements.Count) fromIndex = arr.Elements.Count - 1;
            for (int i = fromIndex; i >= 0; i--)
            {
                if (JsValue.StrictEquals(arr.Elements[i], target)) return (double)i;
            }
            return -1.0;
        }));

        proto.SetNonEnumerable("at", new JsFunction("at", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.at");
            if (args.Count == 0) return JsValue.Undefined;
            int idx = (int)JsValue.ToNumber(args[0]);
            if (idx < 0) idx += arr.Elements.Count;
            if (idx < 0 || idx >= arr.Elements.Count) return JsValue.Undefined;
            return arr.Elements[idx];
        }));

        proto.SetNonEnumerable("fill", new JsFunction("fill", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.fill");
            if (args.Count == 0) return arr;
            var value = args[0];
            int start = args.Count > 1 ? ResolveIndex(args[1], arr.Elements.Count, 0) : 0;
            int end = args.Count > 2 ? ResolveIndex(args[2], arr.Elements.Count, arr.Elements.Count) : arr.Elements.Count;
            for (int i = start; i < end; i++)
            {
                arr.Elements[i] = value;
            }
            return arr;
        }));

        proto.SetNonEnumerable("copyWithin", new JsFunction("copyWithin", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.copyWithin");
            int len = arr.Elements.Count;
            int target = args.Count > 0 ? ResolveIndex(args[0], len, 0) : 0;
            int start = args.Count > 1 ? ResolveIndex(args[1], len, 0) : 0;
            int end = args.Count > 2 ? ResolveIndex(args[2], len, len) : len;
            int count = Math.Min(end - start, len - target);
            if (count > 0)
            {
                // Copy to a temporary buffer first so overlapping
                // ranges don't clobber each other.
                var buf = new object?[count];
                for (int i = 0; i < count; i++) buf[i] = arr.Elements[start + i];
                for (int i = 0; i < count; i++) arr.Elements[target + i] = buf[i];
            }
            return arr;
        }));

        proto.SetNonEnumerable("flat", new JsFunction("flat", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.flat");
            int depth = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 1;
            var result = new JsArray { Prototype = engine.ArrayPrototype };
            FlattenInto(arr, depth, result);
            return result;
        }));

        proto.SetNonEnumerable("flatMap", new JsFunction("flatMap", (vm, thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.flatMap");
            if (args.Count == 0 || args[0] is not JsFunction cb) return arr;
            var result = new JsArray { Prototype = engine.ArrayPrototype };
            for (int i = 0; i < arr.Elements.Count; i++)
            {
                var mapped = vm.InvokeJsFunction(cb, JsValue.Undefined,
                    new object?[] { arr.Elements[i], (double)i, arr });
                // Spec: one level of flattening for flatMap.
                if (mapped is JsArray sub)
                {
                    foreach (var item in sub.Elements) result.Elements.Add(item);
                }
                else
                {
                    result.Elements.Add(mapped);
                }
            }
            return result;
        }));

        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.keys");
            return CreateArrayIterator(engine, arr, ArrayIterKind.Keys);
        }));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.values");
            return CreateArrayIterator(engine, arr, ArrayIterKind.Values);
        }));
        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var arr = RequireArray(thisVal, "Array.prototype.entries");
            return CreateArrayIterator(engine, arr, ArrayIterKind.Entries);
        }));

        // Array.from and Array.of live on the constructor, not
        // the prototype.
        if (engine.Globals.TryGetValue("Array", out var arrCtor) && arrCtor is JsFunction arrCtorFn)
        {
            arrCtorFn.SetNonEnumerable("from", new JsFunction("from", (vm, thisVal, args) =>
            {
                var result = new JsArray { Prototype = engine.ArrayPrototype };
                if (args.Count == 0) return result;
                var source = args[0];
                JsFunction? mapFn = args.Count > 1 && args[1] is JsFunction mf ? mf : null;
                if (source is JsArray src)
                {
                    // Fast path: copy or map source elements.
                    for (int i = 0; i < src.Elements.Count; i++)
                    {
                        var e = src.Elements[i];
                        if (mapFn is not null)
                        {
                            e = vm.InvokeJsFunction(mapFn, JsValue.Undefined, new object?[] { e, (double)i });
                        }
                        result.Elements.Add(e);
                    }
                    return result;
                }
                if (source is string s)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        object? e = s[i].ToString();
                        if (mapFn is not null)
                        {
                            e = vm.InvokeJsFunction(mapFn, JsValue.Undefined, new object?[] { e, (double)i });
                        }
                        result.Elements.Add(e);
                    }
                    return result;
                }
                // Fall back to the iterator protocol.
                if (source is JsObject obj)
                {
                    // Array-like: has numeric length.
                    var lenVal = obj.Get("length");
                    if (lenVal is double len)
                    {
                        int count = (int)len;
                        for (int i = 0; i < count; i++)
                        {
                            var e = obj.Get(i.ToString(CultureInfo.InvariantCulture));
                            if (mapFn is not null)
                            {
                                e = vm.InvokeJsFunction(mapFn, JsValue.Undefined, new object?[] { e, (double)i });
                            }
                            result.Elements.Add(e);
                        }
                        return result;
                    }
                    // TODO: full iterator protocol (Symbol.iterator)
                    // support. Most call sites hit the array-like
                    // path in practice.
                }
                return result;
            }));

            arrCtorFn.SetNonEnumerable("of", new JsFunction("of", (thisVal, args) =>
            {
                var result = new JsArray { Prototype = engine.ArrayPrototype };
                foreach (var item in args) result.Elements.Add(item);
                return result;
            }));
        }
    }

    private static JsArray RequireArray(object? thisVal, string name)
    {
        if (thisVal is not JsArray a)
        {
            JsThrow.TypeError($"{name} called on non-array");
        }
        return (JsArray)thisVal!;
    }

    /// <summary>
    /// ECMAScript <c>SameValueZero</c> equality — identical
    /// to strict equality except <c>NaN</c> is considered
    /// equal to itself. Used by <c>Array.prototype.includes</c>,
    /// <c>Map</c> / <c>Set</c>, and friends.
    /// </summary>
    private static bool SameValueZero(object? a, object? b)
    {
        if (a is double da && b is double db)
        {
            if (double.IsNaN(da) && double.IsNaN(db)) return true;
            return da == db;
        }
        return JsValue.StrictEquals(a, b);
    }

    private static int ResolveIndex(object? raw, int length, int fallback)
    {
        if (raw is JsUndefined) return fallback;
        double n = JsValue.ToNumber(raw);
        if (double.IsNaN(n)) return fallback;
        int idx = (int)n;
        if (idx < 0) idx += length;
        if (idx < 0) idx = 0;
        if (idx > length) idx = length;
        return idx;
    }

    private static void FlattenInto(JsArray src, int depth, JsArray dest)
    {
        foreach (var item in src.Elements)
        {
            if (item is JsArray inner && depth > 0)
            {
                FlattenInto(inner, depth - 1, dest);
            }
            else
            {
                dest.Elements.Add(item);
            }
        }
    }

    private enum ArrayIterKind { Keys, Values, Entries }

    private static JsObject CreateArrayIterator(JsEngine engine, JsArray arr, ArrayIterKind kind)
    {
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (index >= arr.Elements.Count)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            object? value = kind switch
            {
                ArrayIterKind.Keys => (double)index,
                ArrayIterKind.Values => arr.Elements[index],
                _ => PairAsEntry(engine, index, arr.Elements[index]),
            };
            index++;
            result.Set("value", value);
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static JsArray PairAsEntry(JsEngine engine, int index, object? value)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        arr.Elements.Add((double)index);
        arr.Elements.Add(value);
        return arr;
    }

    // =======================================================
    // Object static additions
    // =======================================================

    private static void InstallObjectAdditions(JsEngine engine)
    {
        if (!engine.Globals.TryGetValue("Object", out var objCtor) ||
            objCtor is not JsFunction fn) return;

        fn.SetNonEnumerable("assign", new JsFunction("assign", (thisVal, args) =>
        {
            if (args.Count == 0)
            {
                JsThrow.TypeError("Object.assign: target is required");
                return null;
            }
            if (args[0] is not JsObject target)
            {
                JsThrow.TypeError("Object.assign: target must be an object");
                return null;
            }
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] is not JsObject src) continue;
                foreach (var key in src.OwnKeys())
                {
                    target.Set(key, src.Get(key));
                }
            }
            return target;
        }));

        fn.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            if (args.Count == 0 || args[0] is not JsObject obj) return arr;
            foreach (var key in obj.OwnKeys())
            {
                var pair = new JsArray { Prototype = engine.ArrayPrototype };
                pair.Elements.Add(key);
                pair.Elements.Add(obj.Get(key));
                arr.Elements.Add(pair);
            }
            return arr;
        }));

        fn.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            if (args.Count == 0 || args[0] is not JsObject obj) return arr;
            foreach (var key in obj.OwnKeys())
            {
                arr.Elements.Add(obj.Get(key));
            }
            return arr;
        }));

        fn.SetNonEnumerable("fromEntries", new JsFunction("fromEntries", (thisVal, args) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (args.Count == 0) return result;
            if (args[0] is JsArray arr)
            {
                foreach (var entry in arr.Elements)
                {
                    if (entry is JsArray pair && pair.Elements.Count >= 2)
                    {
                        result.Set(JsValue.ToJsString(pair.Elements[0]), pair.Elements[1]);
                    }
                }
            }
            return result;
        }));

        // freeze / isFrozen are approximate — we don't enforce
        // immutability because there's no configurable/writable
        // descriptor model yet. The no-op keeps script code that
        // calls them from crashing.
        fn.SetNonEnumerable("freeze", new JsFunction("freeze", (thisVal, args) =>
            args.Count > 0 ? args[0] : JsValue.Undefined));
        fn.SetNonEnumerable("isFrozen", new JsFunction("isFrozen", (thisVal, args) => false));
        fn.SetNonEnumerable("seal", new JsFunction("seal", (thisVal, args) =>
            args.Count > 0 ? args[0] : JsValue.Undefined));
        fn.SetNonEnumerable("isSealed", new JsFunction("isSealed", (thisVal, args) => false));
        fn.SetNonEnumerable("preventExtensions", new JsFunction("preventExtensions", (thisVal, args) =>
            args.Count > 0 ? args[0] : JsValue.Undefined));
        fn.SetNonEnumerable("isExtensible", new JsFunction("isExtensible", (thisVal, args) => true));

        fn.SetNonEnumerable("getOwnPropertyNames", new JsFunction("getOwnPropertyNames", (thisVal, args) =>
        {
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            if (args.Count == 0 || args[0] is not JsObject obj) return arr;
            foreach (var key in obj.OwnKeys()) arr.Elements.Add(key);
            return arr;
        }));

        fn.SetNonEnumerable("getOwnPropertyDescriptor", new JsFunction("getOwnPropertyDescriptor", (thisVal, args) =>
        {
            if (args.Count < 2 || args[0] is not JsObject obj) return JsValue.Undefined;
            var key = JsValue.ToJsString(args[1]);
            if (!obj.Has(key)) return JsValue.Undefined;
            var desc = new JsObject { Prototype = engine.ObjectPrototype };
            desc.Set("value", obj.Get(key));
            desc.Set("writable", true);
            desc.Set("enumerable", obj.IsEnumerable(key));
            desc.Set("configurable", true);
            return desc;
        }));

        fn.SetNonEnumerable("defineProperty", new JsFunction("defineProperty", (thisVal, args) =>
        {
            if (args.Count < 3 || args[0] is not JsObject obj)
            {
                JsThrow.TypeError("Object.defineProperty: target must be an object");
                return null;
            }
            var key = JsValue.ToJsString(args[1]);
            if (args[2] is not JsObject desc)
            {
                JsThrow.TypeError("Object.defineProperty: descriptor must be an object");
                return null;
            }
            // Handle data vs accessor descriptors. Ignoring
            // writable/configurable/enumerable flags — we don't
            // enforce them yet.
            var getter = desc.Get("get");
            var setter = desc.Get("set");
            if (getter is JsFunction getFn || setter is JsFunction setFn)
            {
                obj.SetAccessor(key, getter as JsFunction, setter as JsFunction);
            }
            else if (desc.Has("value"))
            {
                obj.Set(key, desc.Get("value"));
            }
            return obj;
        }));

        fn.SetNonEnumerable("defineProperties", new JsFunction("defineProperties", (thisVal, args) =>
        {
            if (args.Count < 2 || args[0] is not JsObject obj) return args.Count > 0 ? args[0] : JsValue.Undefined;
            if (args[1] is not JsObject descs) return obj;
            foreach (var key in descs.OwnKeys())
            {
                if (descs.Get(key) is JsObject desc)
                {
                    var getter = desc.Get("get");
                    var setter = desc.Get("set");
                    if (getter is JsFunction || setter is JsFunction)
                    {
                        obj.SetAccessor(key, getter as JsFunction, setter as JsFunction);
                    }
                    else if (desc.Has("value"))
                    {
                        obj.Set(key, desc.Get("value"));
                    }
                }
            }
            return obj;
        }));

        fn.SetNonEnumerable("setPrototypeOf", new JsFunction("setPrototypeOf", (thisVal, args) =>
        {
            if (args.Count < 2 || args[0] is not JsObject obj) return args.Count > 0 ? args[0] : JsValue.Undefined;
            if (args[1] is JsObject proto) obj.Prototype = proto;
            else if (args[1] is JsNull) obj.Prototype = null;
            return obj;
        }));

        fn.SetNonEnumerable("is", new JsFunction("is", (thisVal, args) =>
        {
            var a = args.Count > 0 ? args[0] : JsValue.Undefined;
            var b = args.Count > 1 ? args[1] : JsValue.Undefined;
            // Object.is is SameValue: strict equality + NaN
            // is equal to itself + +0 / -0 are distinct.
            if (a is double da && b is double db)
            {
                if (double.IsNaN(da) && double.IsNaN(db)) return true;
                if (da == 0 && db == 0)
                {
                    return double.IsNegative(da) == double.IsNegative(db);
                }
                return da == db;
            }
            return JsValue.StrictEquals(a, b);
        }));
    }

    // =======================================================
    // String.prototype additions
    // =======================================================

    private static void InstallStringAdditions(JsEngine engine)
    {
        var proto = engine.StringPrototype;

        proto.SetNonEnumerable("startsWith", new JsFunction("startsWith", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return false;
            var search = JsValue.ToJsString(args[0]);
            int pos = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            if (pos < 0) pos = 0;
            if (pos + search.Length > self.Length) return false;
            return self.Substring(pos, search.Length) == search;
        }));

        proto.SetNonEnumerable("endsWith", new JsFunction("endsWith", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return false;
            var search = JsValue.ToJsString(args[0]);
            int endPos = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : self.Length;
            if (endPos > self.Length) endPos = self.Length;
            int start = endPos - search.Length;
            if (start < 0) return false;
            return self.Substring(start, search.Length) == search;
        }));

        proto.SetNonEnumerable("includes", new JsFunction("includes", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return false;
            var search = JsValue.ToJsString(args[0]);
            int start = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            if (start < 0) start = 0;
            return self.IndexOf(search, start, StringComparison.Ordinal) >= 0;
        }));

        proto.SetNonEnumerable("padStart", new JsFunction("padStart", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return self;
            int targetLen = (int)JsValue.ToNumber(args[0]);
            if (targetLen <= self.Length) return self;
            string padStr = args.Count > 1 && args[1] is not JsUndefined
                ? JsValue.ToJsString(args[1])
                : " ";
            if (padStr.Length == 0) return self;
            int needed = targetLen - self.Length;
            var sb = new System.Text.StringBuilder(targetLen);
            while (sb.Length < needed)
            {
                int take = Math.Min(padStr.Length, needed - sb.Length);
                sb.Append(padStr, 0, take);
            }
            sb.Append(self);
            return sb.ToString();
        }));

        proto.SetNonEnumerable("padEnd", new JsFunction("padEnd", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return self;
            int targetLen = (int)JsValue.ToNumber(args[0]);
            if (targetLen <= self.Length) return self;
            string padStr = args.Count > 1 && args[1] is not JsUndefined
                ? JsValue.ToJsString(args[1])
                : " ";
            if (padStr.Length == 0) return self;
            int needed = targetLen - self.Length;
            var sb = new System.Text.StringBuilder(targetLen).Append(self);
            while (sb.Length < targetLen)
            {
                int take = Math.Min(padStr.Length, targetLen - sb.Length);
                sb.Append(padStr, 0, take);
            }
            return sb.ToString();
        }));

        proto.SetNonEnumerable("repeat", new JsFunction("repeat", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            int count = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            if (count < 0)
            {
                JsThrow.RangeError("Invalid count value");
                return null;
            }
            if (count == 0 || self.Length == 0) return "";
            var sb = new System.Text.StringBuilder(self.Length * count);
            for (int i = 0; i < count; i++) sb.Append(self);
            return sb.ToString();
        }));

        proto.SetNonEnumerable("trimStart", new JsFunction("trimStart", (thisVal, args) =>
            JsValue.ToJsString(thisVal).TrimStart()));
        proto.SetNonEnumerable("trimLeft", new JsFunction("trimLeft", (thisVal, args) =>
            JsValue.ToJsString(thisVal).TrimStart()));
        proto.SetNonEnumerable("trimEnd", new JsFunction("trimEnd", (thisVal, args) =>
            JsValue.ToJsString(thisVal).TrimEnd()));
        proto.SetNonEnumerable("trimRight", new JsFunction("trimRight", (thisVal, args) =>
            JsValue.ToJsString(thisVal).TrimEnd()));

        proto.SetNonEnumerable("at", new JsFunction("at", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            if (args.Count == 0) return JsValue.Undefined;
            int idx = (int)JsValue.ToNumber(args[0]);
            if (idx < 0) idx += self.Length;
            if (idx < 0 || idx >= self.Length) return JsValue.Undefined;
            return self[idx].ToString();
        }));

        proto.SetNonEnumerable("codePointAt", new JsFunction("codePointAt", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            int idx = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            if (idx < 0 || idx >= self.Length) return JsValue.Undefined;
            return (double)char.ConvertToUtf32(self, idx);
        }));

        proto.SetNonEnumerable("normalize", new JsFunction("normalize", (thisVal, args) =>
        {
            var self = JsValue.ToJsString(thisVal);
            // .NET string.Normalize supports NFC/NFD/NFKC/NFKD.
            // JS accepts exactly those four names; default is NFC.
            var form = args.Count > 0 && args[0] is not JsUndefined
                ? JsValue.ToJsString(args[0])
                : "NFC";
            try
            {
                return form switch
                {
                    "NFC" => self.Normalize(System.Text.NormalizationForm.FormC),
                    "NFD" => self.Normalize(System.Text.NormalizationForm.FormD),
                    "NFKC" => self.Normalize(System.Text.NormalizationForm.FormKC),
                    "NFKD" => self.Normalize(System.Text.NormalizationForm.FormKD),
                    _ => self,
                };
            }
            catch
            {
                return self;
            }
        }));
    }

    // =======================================================
    // Number static additions + prototype additions
    // =======================================================

    private static void InstallNumberAdditions(JsEngine engine)
    {
        if (!engine.Globals.TryGetValue("Number", out var numCtor) ||
            numCtor is not JsFunction fn) return;

        // Static numeric constants. ES2015 pinned these values
        // so they're fixed constants script code can rely on.
        fn.SetNonEnumerable("MAX_SAFE_INTEGER", 9007199254740991.0);
        fn.SetNonEnumerable("MIN_SAFE_INTEGER", -9007199254740991.0);
        fn.SetNonEnumerable("MAX_VALUE", double.MaxValue);
        fn.SetNonEnumerable("MIN_VALUE", 5e-324);
        fn.SetNonEnumerable("EPSILON", 2.220446049250313e-16);
        fn.SetNonEnumerable("POSITIVE_INFINITY", double.PositiveInfinity);
        fn.SetNonEnumerable("NEGATIVE_INFINITY", double.NegativeInfinity);
        fn.SetNonEnumerable("NaN", double.NaN);

        fn.SetNonEnumerable("isInteger", new JsFunction("isInteger", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not double d) return false;
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            return d == Math.Truncate(d);
        }));

        fn.SetNonEnumerable("isFinite", new JsFunction("isFinite", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not double d) return false;
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }));

        fn.SetNonEnumerable("isNaN", new JsFunction("isNaN", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not double d) return false;
            return double.IsNaN(d);
        }));

        fn.SetNonEnumerable("isSafeInteger", new JsFunction("isSafeInteger", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not double d) return false;
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            if (d != Math.Truncate(d)) return false;
            return Math.Abs(d) <= 9007199254740991.0;
        }));

        // Number.parseInt / parseFloat are the same as the globals.
        if (engine.Globals.TryGetValue("parseInt", out var pInt))
        {
            fn.SetNonEnumerable("parseInt", pInt!);
        }
        if (engine.Globals.TryGetValue("parseFloat", out var pFlt))
        {
            fn.SetNonEnumerable("parseFloat", pFlt!);
        }
    }
}
