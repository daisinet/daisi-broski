namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2015 collections — <c>Map</c>, <c>Set</c>,
/// <c>WeakMap</c>, <c>WeakSet</c>. Each constructor accepts
/// an optional initial iterable (a list of <c>[k, v]</c>
/// pairs for <c>Map</c> / <c>WeakMap</c>, a list of values
/// for <c>Set</c> / <c>WeakSet</c>) and populates the
/// collection by driving the ES2015 iterator protocol via
/// <see cref="JsVM.GetIteratorFromIterable"/>.
///
/// Slice 3b-8 does not implement real property-descriptor
/// getters, so <c>.size</c> is intercepted inside
/// <see cref="JsMap.Get(string)"/> / <see cref="JsSet.Get(string)"/>
/// and always reports the current entry count. <c>Map</c>
/// and <c>Set</c> are iterable — their <c>[Symbol.iterator]</c>
/// defaults to <c>entries()</c> for maps and <c>values()</c>
/// for sets, matching spec.
///
/// <c>WeakMap</c> and <c>WeakSet</c> are not actually weak
/// in this engine; see the class docs on
/// <see cref="JsWeakMap"/> / <see cref="JsWeakSet"/> for
/// the rationale.
/// </summary>
internal static class BuiltinCollections
{
    public static void Install(JsEngine engine)
    {
        InstallMap(engine);
        InstallSet(engine);
        InstallWeakMap(engine);
        InstallWeakSet(engine);
    }

    // ------------------------------------------------------
    // Map
    // ------------------------------------------------------

    private static void InstallMap(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.get");
            return m.MapGet(args.Count > 0 ? args[0] : JsValue.Undefined);
        }));

        proto.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.set");
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            var value = args.Count > 1 ? args[1] : JsValue.Undefined;
            m.MapPut(key, value);
            return m; // spec: returns the Map for chaining
        }));

        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.has");
            return m.MapHas(args.Count > 0 ? args[0] : JsValue.Undefined);
        }));

        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.delete");
            return m.MapDelete(args.Count > 0 ? args[0] : JsValue.Undefined);
        }));

        proto.SetNonEnumerable("clear", new JsFunction("clear", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.clear");
            m.MapClear();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction cb)
            {
                JsThrow.TypeError("Map.prototype.forEach callback is not a function");
            }
            var fn = (JsFunction)args[0]!;
            // Snapshot entries so concurrent mutation during
            // iteration doesn't crash .NET's Dictionary
            // enumerator. Spec actually requires visiting
            // entries added during iteration too — this is a
            // known deferral.
            var snapshot = new List<KeyValuePair<object, object?>>(m.Entries);
            foreach (var kv in snapshot)
            {
                vm.InvokeJsFunction(fn, JsValue.Undefined, new[] { kv.Value, kv.Key, (object?)m });
            }
            return JsValue.Undefined;
        }));

        // Iterator helpers: keys() / values() / entries() /
        // [Symbol.iterator]. Each returns a fresh iterator
        // object bound to a snapshot of the map's keys in
        // insertion order.
        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.keys");
            return CreateMapIterator(engine, m, MapIterKind.Keys);
        }));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.values");
            return CreateMapIterator(engine, m, MapIterKind.Values);
        }));
        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype.entries");
            return CreateMapIterator(engine, m, MapIterKind.Entries);
        }));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (thisVal, args) =>
        {
            var m = RequireMap(thisVal, "Map.prototype[Symbol.iterator]");
            return CreateMapIterator(engine, m, MapIterKind.Entries);
        }));

        // Constructor — accepts an optional iterable of
        // [key, value] pairs.
        var ctor = new JsFunction("Map", (vm, thisVal, args) =>
        {
            var m = new JsMap { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                PopulateMapFromIterable(vm, engine, m, args[0]);
            }
            return m;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Map"] = ctor;
    }

    private static JsMap RequireMap(object? thisVal, string name)
    {
        if (thisVal is not JsMap m)
        {
            JsThrow.TypeError($"{name} called on non-Map");
        }
        return (JsMap)thisVal!;
    }

    private enum MapIterKind { Keys, Values, Entries }

    private static JsObject CreateMapIterator(JsEngine engine, JsMap map, MapIterKind kind)
    {
        // Snapshot entries so the iterator isn't disturbed
        // by mutation during iteration. Full live-iteration
        // semantics are a known deferral.
        var snapshot = new List<KeyValuePair<object, object?>>(map.Entries);
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (index >= snapshot.Count)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            var kv = snapshot[index++];
            object? value = kind switch
            {
                MapIterKind.Keys => kv.Key,
                MapIterKind.Values => kv.Value,
                MapIterKind.Entries => MakeEntryPair(engine, kv.Key, kv.Value),
                _ => JsValue.Undefined,
            };
            result.Set("value", value);
            result.Set("done", JsValue.False);
            return result;
        }));
        // Every built-in iterator is itself iterable —
        // `[Symbol.iterator]()` returns the iterator.
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction(
            "[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static JsArray MakeEntryPair(JsEngine engine, object? key, object? value)
    {
        var pair = new JsArray { Prototype = engine.ArrayPrototype };
        pair.Elements.Add(key);
        pair.Elements.Add(value);
        return pair;
    }

    private static void PopulateMapFromIterable(JsVM vm, JsEngine engine, JsMap map, object? iterable)
    {
        var iter = vm.GetIteratorFromIterable(iterable);
        if (iter is not JsObject iterObj) return;
        var nextFn = iterObj.Get("next") as JsFunction;
        if (nextFn is null)
        {
            JsThrow.TypeError("Map iterable's iterator has no next() method");
        }
        while (true)
        {
            var stepResult = vm.InvokeJsFunction(nextFn!, iterObj, Array.Empty<object?>());
            if (stepResult is not JsObject step)
            {
                JsThrow.TypeError("Iterator result is not an object");
                return;
            }
            if (JsValue.ToBoolean(step.Get("done"))) return;
            var pair = step.Get("value");
            if (pair is not JsArray arr)
            {
                // Spec: any object with integer-indexed 0 and
                // 1 properties. We support JsArray for
                // simplicity — users who hit this edge case
                // should pass real 2-element arrays.
                if (pair is JsObject po)
                {
                    map.MapPut(po.Get("0"), po.Get("1"));
                    continue;
                }
                JsThrow.TypeError("Map iterable element is not a [key, value] pair");
                return;
            }
            var k = arr.Elements.Count > 0 ? arr.Elements[0] : JsValue.Undefined;
            var v = arr.Elements.Count > 1 ? arr.Elements[1] : JsValue.Undefined;
            map.MapPut(k, v);
        }
    }

    // ------------------------------------------------------
    // Set
    // ------------------------------------------------------

    private static void InstallSet(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("add", new JsFunction("add", (thisVal, args) =>
        {
            var s = RequireSet(thisVal, "Set.prototype.add");
            s.SetAdd(args.Count > 0 ? args[0] : JsValue.Undefined);
            return s; // chainable
        }));
        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var s = RequireSet(thisVal, "Set.prototype.has");
            return s.SetHas(args.Count > 0 ? args[0] : JsValue.Undefined);
        }));
        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var s = RequireSet(thisVal, "Set.prototype.delete");
            return s.SetDelete(args.Count > 0 ? args[0] : JsValue.Undefined);
        }));
        proto.SetNonEnumerable("clear", new JsFunction("clear", (thisVal, args) =>
        {
            var s = RequireSet(thisVal, "Set.prototype.clear");
            s.SetClear();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var s = RequireSet(thisVal, "Set.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction)
            {
                JsThrow.TypeError("Set.prototype.forEach callback is not a function");
            }
            var fn = (JsFunction)args[0]!;
            var snapshot = new List<object?>(s.Entries.Keys);
            foreach (var v in snapshot)
            {
                // Spec: callback args are (value, value, set).
                // The duplicated value is a historical quirk.
                vm.InvokeJsFunction(fn, JsValue.Undefined, new[] { v, v, (object?)s });
            }
            return JsValue.Undefined;
        }));

        // Iterator helpers — for Set, keys() / values() /
        // entries() / [Symbol.iterator] all yield the
        // spec-mandated shapes. entries() yields [v, v]
        // pairs (Set's "key" is also its value).
        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
            CreateSetIterator(engine, RequireSet(thisVal, "Set.prototype.keys"), false)));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
            CreateSetIterator(engine, RequireSet(thisVal, "Set.prototype.values"), false)));
        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
            CreateSetIterator(engine, RequireSet(thisVal, "Set.prototype.entries"), true)));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (thisVal, args) =>
            CreateSetIterator(engine, RequireSet(thisVal, "Set.prototype[Symbol.iterator]"), false)));

        var ctor = new JsFunction("Set", (vm, thisVal, args) =>
        {
            var s = new JsSet { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                PopulateSetFromIterable(vm, engine, s, args[0]);
            }
            return s;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Set"] = ctor;
    }

    private static JsSet RequireSet(object? thisVal, string name)
    {
        if (thisVal is not JsSet s)
        {
            JsThrow.TypeError($"{name} called on non-Set");
        }
        return (JsSet)thisVal!;
    }

    private static JsObject CreateSetIterator(JsEngine engine, JsSet set, bool asEntries)
    {
        var snapshot = new List<object?>(set.Entries.Keys);
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (index >= snapshot.Count)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            var v = snapshot[index++];
            result.Set("value", asEntries ? MakeEntryPair(engine, v, v) : v);
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction(
            "[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static void PopulateSetFromIterable(JsVM vm, JsEngine engine, JsSet set, object? iterable)
    {
        var iter = vm.GetIteratorFromIterable(iterable);
        if (iter is not JsObject iterObj) return;
        var nextFn = iterObj.Get("next") as JsFunction;
        if (nextFn is null)
        {
            JsThrow.TypeError("Set iterable's iterator has no next() method");
        }
        while (true)
        {
            var stepResult = vm.InvokeJsFunction(nextFn!, iterObj, Array.Empty<object?>());
            if (stepResult is not JsObject step)
            {
                JsThrow.TypeError("Iterator result is not an object");
                return;
            }
            if (JsValue.ToBoolean(step.Get("done"))) return;
            set.SetAdd(step.Get("value"));
        }
    }

    // ------------------------------------------------------
    // WeakMap
    // ------------------------------------------------------

    private static void InstallWeakMap(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var m = RequireWeakMap(thisVal, "WeakMap.prototype.get");
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key is not JsObject keyObj) return JsValue.Undefined;
            return m.Entries.TryGetValue(keyObj, out var v) ? v : JsValue.Undefined;
        }));
        proto.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            var m = RequireWeakMap(thisVal, "WeakMap.prototype.set");
            if (args.Count < 1 || args[0] is not JsObject keyObj)
            {
                JsThrow.TypeError("WeakMap key must be an object");
            }
            m.Entries[(JsObject)args[0]!] = args.Count > 1 ? args[1] : JsValue.Undefined;
            return m;
        }));
        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var m = RequireWeakMap(thisVal, "WeakMap.prototype.has");
            if (args.Count < 1 || args[0] is not JsObject keyObj) return false;
            return m.Entries.ContainsKey(keyObj);
        }));
        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var m = RequireWeakMap(thisVal, "WeakMap.prototype.delete");
            if (args.Count < 1 || args[0] is not JsObject keyObj) return false;
            return m.Entries.Remove(keyObj);
        }));

        var ctor = new JsFunction("WeakMap", (vm, thisVal, args) =>
        {
            var m = new JsWeakMap { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                // Populate via same protocol as Map, but
                // enforce object-key requirement.
                var iter = vm.GetIteratorFromIterable(args[0]);
                if (iter is JsObject iterObj)
                {
                    var nextFn = iterObj.Get("next") as JsFunction;
                    if (nextFn is null)
                    {
                        JsThrow.TypeError("WeakMap iterable's iterator has no next() method");
                    }
                    while (true)
                    {
                        var stepResult = vm.InvokeJsFunction(nextFn!, iterObj, Array.Empty<object?>());
                        if (stepResult is not JsObject step) break;
                        if (JsValue.ToBoolean(step.Get("done"))) break;
                        var pair = step.Get("value");
                        if (pair is not JsArray arr) continue;
                        var k = arr.Elements.Count > 0 ? arr.Elements[0] : null;
                        if (k is not JsObject ko)
                        {
                            JsThrow.TypeError("WeakMap key must be an object");
                            return m;
                        }
                        m.Entries[ko] = arr.Elements.Count > 1 ? arr.Elements[1] : JsValue.Undefined;
                    }
                }
            }
            return m;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["WeakMap"] = ctor;
    }

    private static JsWeakMap RequireWeakMap(object? thisVal, string name)
    {
        if (thisVal is not JsWeakMap m)
        {
            JsThrow.TypeError($"{name} called on non-WeakMap");
        }
        return (JsWeakMap)thisVal!;
    }

    // ------------------------------------------------------
    // WeakSet
    // ------------------------------------------------------

    private static void InstallWeakSet(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("add", new JsFunction("add", (thisVal, args) =>
        {
            var s = RequireWeakSet(thisVal, "WeakSet.prototype.add");
            if (args.Count < 1 || args[0] is not JsObject obj)
            {
                JsThrow.TypeError("WeakSet value must be an object");
            }
            s.Entries.Add((JsObject)args[0]!);
            return s;
        }));
        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var s = RequireWeakSet(thisVal, "WeakSet.prototype.has");
            if (args.Count < 1 || args[0] is not JsObject obj) return false;
            return s.Entries.Contains(obj);
        }));
        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var s = RequireWeakSet(thisVal, "WeakSet.prototype.delete");
            if (args.Count < 1 || args[0] is not JsObject obj) return false;
            return s.Entries.Remove(obj);
        }));

        var ctor = new JsFunction("WeakSet", (vm, thisVal, args) =>
        {
            var s = new JsWeakSet { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                var iter = vm.GetIteratorFromIterable(args[0]);
                if (iter is JsObject iterObj)
                {
                    var nextFn = iterObj.Get("next") as JsFunction;
                    if (nextFn is null)
                    {
                        JsThrow.TypeError("WeakSet iterable's iterator has no next() method");
                    }
                    while (true)
                    {
                        var stepResult = vm.InvokeJsFunction(nextFn!, iterObj, Array.Empty<object?>());
                        if (stepResult is not JsObject step) break;
                        if (JsValue.ToBoolean(step.Get("done"))) break;
                        var v = step.Get("value");
                        if (v is not JsObject vo)
                        {
                            JsThrow.TypeError("WeakSet value must be an object");
                            return s;
                        }
                        s.Entries.Add(vo);
                    }
                }
            }
            return s;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["WeakSet"] = ctor;
    }

    private static JsWeakSet RequireWeakSet(object? thisVal, string name)
    {
        if (thisVal is not JsWeakSet s)
        {
            JsThrow.TypeError($"{name} called on non-WeakSet");
        }
        return (JsWeakSet)thisVal!;
    }
}
