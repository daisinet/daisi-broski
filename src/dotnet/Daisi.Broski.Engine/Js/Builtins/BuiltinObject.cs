namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Object</c> constructor plus the phase-3a subset of its
/// static and prototype methods:
///
/// Static: <c>Object.keys</c>, <c>Object.create</c>,
/// <c>Object.getPrototypeOf</c>.
///
/// Prototype: <c>hasOwnProperty</c>, <c>isPrototypeOf</c>,
/// <c>propertyIsEnumerable</c>, <c>toString</c>, <c>valueOf</c>.
///
/// Deferred to slice 6c or later: <c>Object.defineProperty</c>,
/// <c>Object.getOwnPropertyDescriptor</c>, <c>Object.freeze</c>,
/// <c>Object.seal</c>, <c>Object.getOwnPropertyNames</c>
/// (distinct from <c>keys</c> because it includes
/// non-enumerable properties — which phase 3a tracks but
/// doesn't expose via a public API yet).
/// </summary>
internal static class BuiltinObject
{
    public static void Install(JsEngine engine)
    {
        var proto = engine.ObjectPrototype;

        // Prototype methods.
        Builtins.Method(proto, "hasOwnProperty", HasOwnProperty);
        Builtins.Method(proto, "isPrototypeOf", IsPrototypeOf);
        Builtins.Method(proto, "propertyIsEnumerable", PropertyIsEnumerable);
        Builtins.Method(proto, "toString", ToStringMethod);
        Builtins.Method(proto, "valueOf", ValueOfMethod);

        // Object constructor.
        var ctor = new JsFunction("Object", (thisVal, args) =>
        {
            // Object(value) — coerce to object. For slice 6b,
            // we only handle the no-argument case (return a
            // fresh empty object) and the object pass-through
            // case. Primitive boxing is a slice-6c refinement.
            if (args.Count == 0 || args[0] is JsUndefined || args[0] is JsNull)
            {
                return new JsObject { Prototype = engine.ObjectPrototype };
            }
            if (args[0] is JsObject existing) return existing;
            // Primitive — phase 3a doesn't implement wrapper objects.
            return new JsObject { Prototype = engine.ObjectPrototype };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);

        // Static methods.
        Builtins.Method(ctor, "keys", (t, a) => Keys(engine, t, a));
        Builtins.Method(ctor, "create", (t, a) => Create(engine, t, a));
        Builtins.Method(ctor, "getPrototypeOf", GetPrototypeOf);

        engine.Globals["Object"] = ctor;
    }

    // -------------------------------------------------------------------
    // Static methods
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §15.2.3.14 — <c>Object.keys(obj)</c>. Returns an
    /// array of the object's own enumerable string-keyed
    /// property names, in insertion order. Does not walk the
    /// prototype chain.
    /// </summary>
    private static object? Keys(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || args[0] is not JsObject obj)
        {
            return JsThrow.TypeError("Object.keys called on non-object");
        }
        var result = new JsArray { Prototype = engine.ArrayPrototype };
        foreach (var k in obj.OwnKeys())
        {
            result.Elements.Add(k);
        }
        return result;
    }

    /// <summary>
    /// ECMA §15.2.3.5 — <c>Object.create(proto, propsDescriptor)</c>.
    /// Slice 6b ignores the second argument (property
    /// descriptors); slice 6c will wire it up when
    /// <c>Object.defineProperty</c> lands.
    /// </summary>
    private static object? Create(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        if (args.Count == 0)
        {
            return JsThrow.TypeError("Object.create requires at least one argument");
        }
        JsObject? proto;
        if (args[0] is JsNull)
        {
            proto = null;
        }
        else if (args[0] is JsObject o)
        {
            proto = o;
        }
        else
        {
            return JsThrow.TypeError("Object.create prototype must be an object or null");
        }
        return new JsObject { Prototype = proto };
    }

    /// <summary>
    /// ECMA §15.2.3.2 — <c>Object.getPrototypeOf(obj)</c>.
    /// Returns the object's <c>[[Prototype]]</c>, or <c>null</c>
    /// if it has none.
    /// </summary>
    private static object? GetPrototypeOf(object? thisVal, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || args[0] is not JsObject obj)
        {
            return JsThrow.TypeError("Object.getPrototypeOf called on non-object");
        }
        return (object?)obj.Prototype ?? JsValue.Null;
    }

    // -------------------------------------------------------------------
    // Prototype methods
    // -------------------------------------------------------------------

    private static object? HasOwnProperty(object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsObject obj) return false;
        if (args.Count == 0) return false;
        var key = JsValue.ToJsString(args[0]);
        return obj.Properties.ContainsKey(key);
    }

    /// <summary>
    /// ECMA §15.2.4.6 — <c>isPrototypeOf(obj)</c>. Walks
    /// <c>obj</c>'s prototype chain looking for <c>this</c>.
    /// </summary>
    private static object? IsPrototypeOf(object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsObject self) return false;
        if (args.Count == 0 || args[0] is not JsObject target) return false;
        var walker = target.Prototype;
        while (walker is not null)
        {
            if (ReferenceEquals(walker, self)) return true;
            walker = walker.Prototype;
        }
        return false;
    }

    private static object? PropertyIsEnumerable(object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsObject obj) return false;
        if (args.Count == 0) return false;
        var key = JsValue.ToJsString(args[0]);
        if (!obj.Properties.ContainsKey(key)) return false;
        return obj.IsEnumerable(key);
    }

    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        // Slice-6b subset of §15.2.4.2: [[Class]] would drive
        // the bracket content (<c>[object Array]</c> etc.) but
        // phase 3a doesn't track [[Class]]. We return a shape
        // that's close enough for logging.
        if (thisVal is null || thisVal is JsUndefined) return "[object Undefined]";
        if (thisVal is JsNull) return "[object Null]";
        if (thisVal is JsArray) return "[object Array]";
        if (thisVal is JsFunction) return "[object Function]";
        return "[object Object]";
    }

    private static object? ValueOfMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        // ECMA §15.2.4.4 — by default, valueOf returns this.
        return thisVal;
    }
}
