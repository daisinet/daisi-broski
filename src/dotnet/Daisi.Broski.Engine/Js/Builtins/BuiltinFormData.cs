namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>FormData</c> — an ordered multi-map of name → (string|Blob|File).
/// Real pages produce these by calling
/// <c>new FormData(formEl)</c> and then hand them to
/// <c>fetch</c> as a body; we expose the shape + the full
/// mutation / iteration surface so script code can construct
/// and inspect them, with the <c>fetch</c> upload integration
/// left for the slice that wires multipart encoding.
///
/// <para>
/// Entries are insertion-ordered, and
/// <c>append(name, value)</c> keeps every value per key
/// (multi-valued by design — repeated checkboxes round-trip
/// correctly). <c>set(name, value)</c> replaces every prior
/// entry with a fresh one at the original insertion position,
/// so field order is stable across edits. Iteration yields
/// <c>[name, value]</c> pairs via the entries/keys/values
/// trio and <c>[Symbol.iterator]</c>.
/// </para>
///
/// <para>
/// <c>new FormData(formEl)</c> is accepted but currently
/// ignored — the form-element walk (collect named inputs,
/// apply <c>type=file</c> handling) lands in a follow-up slice
/// once the DOM form surface supports enumeration of named
/// controls. Passing any other argument is ignored.
/// </para>
/// </summary>
internal static class BuiltinFormData
{
    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("append", new JsFunction("append", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.append");
            if (args.Count < 2)
            {
                JsThrow.TypeError("FormData.prototype.append requires 2 arguments");
            }
            f.Append(JsValue.ToJsString(args[0]), CoerceValue(args[1], args.Count > 2 ? args[2] : null));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.set");
            if (args.Count < 2)
            {
                JsThrow.TypeError("FormData.prototype.set requires 2 arguments");
            }
            f.Set(JsValue.ToJsString(args[0]), CoerceValue(args[1], args.Count > 2 ? args[2] : null));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.get");
            if (args.Count == 0) return JsValue.Null;
            return f.Lookup(JsValue.ToJsString(args[0])) ?? JsValue.Null;
        }));

        proto.SetNonEnumerable("getAll", new JsFunction("getAll", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.getAll");
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            if (args.Count == 0) return arr;
            var name = JsValue.ToJsString(args[0]);
            foreach (var v in f.GetAll(name)) arr.Elements.Add(v);
            return arr;
        }));

        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.has");
            if (args.Count == 0) return false;
            return f.Has(JsValue.ToJsString(args[0]));
        }));

        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.delete");
            if (args.Count == 0) return JsValue.Undefined;
            f.DeleteEntries(JsValue.ToJsString(args[0]));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction cb) return JsValue.Undefined;
            // Snapshot so handler mutation doesn't throw off
            // iteration — WHATWG spec requires stable iteration
            // over the pre-forEach list.
            foreach (var (name, value) in f.Entries().ToArray())
            {
                vm.InvokeJsFunction(cb, JsValue.Undefined,
                    new object?[] { value, name, f });
            }
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.entries");
            return CreateFormDataIterator(engine, f, FormDataIterKind.Entries);
        }));
        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.keys");
            return CreateFormDataIterator(engine, f, FormDataIterKind.Keys);
        }));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var f = RequireFormData(thisVal, "FormData.prototype.values");
            return CreateFormDataIterator(engine, f, FormDataIterKind.Values);
        }));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction(
            "[Symbol.iterator]", (thisVal, args) =>
            {
                var f = RequireFormData(thisVal, "FormData.prototype[Symbol.iterator]");
                return CreateFormDataIterator(engine, f, FormDataIterKind.Entries);
            }));

        var ctor = new JsFunction("FormData", (thisVal, args) =>
        {
            var f = new JsFormData { Prototype = proto };
            // The optional form argument is silently ignored —
            // see the class doc comment for the rationale.
            return f;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["FormData"] = ctor;
    }

    /// <summary>Coerce a raw JS-side value into something the
    /// FormData store can keep: strings stay strings; Blob /
    /// File pass through; anything else stringifies. When a
    /// third arg is supplied alongside a Blob, the spec turns
    /// that Blob into a File with the given filename so the
    /// upload stage has a name to emit.</summary>
    private static object CoerceValue(object? raw, object? filenameArg)
    {
        string? filename = filenameArg is null or JsUndefined
            ? null
            : JsValue.ToJsString(filenameArg);

        if (raw is JsFile f && filename is null) return f;
        if (raw is JsBlob b)
        {
            if (filename is not null)
            {
                // Re-box the Blob into a File so the filename
                // rides through FormData.entries(). Keeps the
                // spec-visible type ("File" vs. "Blob") in step.
                return new JsFile(b.Data, filename, b.Type,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                { Prototype = b.Prototype };
            }
            return b;
        }
        return raw is null ? "" : JsValue.ToJsString(raw);
    }

    private static JsFormData RequireFormData(object? thisVal, string name)
    {
        if (thisVal is not JsFormData f)
        {
            JsThrow.TypeError($"{name} called on non-FormData");
        }
        return (JsFormData)thisVal!;
    }

    private enum FormDataIterKind { Keys, Values, Entries }

    private static JsObject CreateFormDataIterator(
        JsEngine engine, JsFormData f, FormDataIterKind kind)
    {
        var snapshot = f.Entries().ToArray();
        int i = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (i >= snapshot.Length)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            var (name, value) = snapshot[i++];
            object? v = kind switch
            {
                FormDataIterKind.Keys => (object)name,
                FormDataIterKind.Values => value,
                _ => PairArray(engine, name, value),
            };
            result.Set("value", v);
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction(
            "[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static JsArray PairArray(JsEngine engine, string name, object value)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        arr.Elements.Add(name);
        arr.Elements.Add(value);
        return arr;
    }
}

/// <summary>
/// Instance state for a JS <c>FormData</c>. Insertion-ordered
/// multi-map of name → (string | <see cref="JsBlob"/> |
/// <see cref="JsFile"/>). Mutation methods keep insertion order
/// stable even across <c>set</c> (replace-in-place), matching
/// the WHATWG spec.
/// </summary>
public sealed class JsFormData : JsObject
{
    // Flat entries list: parallel name/value arrays preserve
    // insertion order plus allow O(N) multi-value read. FormData
    // is typically small (< 20 entries); no hash index needed.
    private readonly List<(string Name, object Value)> _entries = new();

    public void Append(string name, object value) =>
        _entries.Add((name, value));

    public void Set(string name, object value)
    {
        // WHATWG: replace every prior entry with this name,
        // keeping the insertion slot of the *first* prior entry
        // when present; otherwise append.
        int firstIdx = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name == name)
            {
                if (firstIdx < 0) firstIdx = i;
            }
        }
        // Scrub existing entries in reverse so indices hold.
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Name == name) _entries.RemoveAt(i);
        }
        if (firstIdx < 0)
        {
            _entries.Add((name, value));
        }
        else
        {
            _entries.Insert(Math.Min(firstIdx, _entries.Count), (name, value));
        }
    }

    /// <summary>Return the value of the first entry with the
    /// given name, or <c>null</c> when absent. Named <c>Lookup</c>
    /// rather than <c>Get</c> so it doesn't collide with the
    /// property-get path on the <see cref="JsObject"/> base.</summary>
    public object? Lookup(string name)
    {
        foreach (var (n, v) in _entries)
        {
            if (n == name) return v;
        }
        return null;
    }

    public IEnumerable<object> GetAll(string name)
    {
        foreach (var (n, v) in _entries)
        {
            if (n == name) yield return v;
        }
    }

    public bool Has(string name)
    {
        foreach (var (n, _) in _entries)
        {
            if (n == name) return true;
        }
        return false;
    }

    /// <summary>Remove every entry with the given name.
    /// Named <c>DeleteEntries</c> to sidestep a hide of
    /// <see cref="JsObject.Delete"/> (the JS <c>delete</c>
    /// property operator).</summary>
    public void DeleteEntries(string name)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Name == name) _entries.RemoveAt(i);
        }
    }

    public IEnumerable<(string Name, object Value)> Entries() => _entries;
}
