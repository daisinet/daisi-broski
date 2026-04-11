namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Error</c> and its subclasses (<c>TypeError</c>,
/// <c>RangeError</c>, <c>SyntaxError</c>, <c>ReferenceError</c>,
/// <c>EvalError</c>, <c>URIError</c>). Each is a constructor
/// plus a prototype that inherits from <c>Error.prototype</c>,
/// matching the ECMA §15.11 hierarchy. Every prototype carries
/// <c>name</c> and <c>message</c> (default <c>""</c>) and a
/// <c>toString</c> that renders as <c>"Name: message"</c>.
///
/// The engine records the prototype objects on its
/// <c>ErrorPrototypes</c> so the VM's <c>RaiseError</c> can tag
/// internal errors with the right prototype, letting script code
/// use <c>e instanceof TypeError</c> to narrow.
/// </summary>
internal static class BuiltinError
{
    public static void Install(JsEngine engine)
    {
        // Base Error first — its prototype is the parent of every
        // other error prototype.
        var errorProto = new JsObject { Prototype = engine.ObjectPrototype };
        errorProto.Set("name", "Error");
        errorProto.Set("message", "");
        Builtins.Method(errorProto, "toString", ToStringMethod);

        var errorCtor = MakeErrorCtor(engine, "Error", errorProto);
        engine.ErrorPrototype = errorProto;
        engine.Globals["Error"] = errorCtor;

        engine.TypeErrorPrototype = InstallSubclass(engine, "TypeError", errorProto);
        engine.RangeErrorPrototype = InstallSubclass(engine, "RangeError", errorProto);
        engine.SyntaxErrorPrototype = InstallSubclass(engine, "SyntaxError", errorProto);
        engine.ReferenceErrorPrototype = InstallSubclass(engine, "ReferenceError", errorProto);
        InstallSubclass(engine, "EvalError", errorProto);
        InstallSubclass(engine, "URIError", errorProto);
    }

    private static JsObject InstallSubclass(JsEngine engine, string name, JsObject errorProto)
    {
        var proto = new JsObject { Prototype = errorProto };
        proto.Set("name", name);
        proto.Set("message", "");
        var ctor = MakeErrorCtor(engine, name, proto);
        engine.Globals[name] = ctor;
        return proto;
    }

    /// <summary>
    /// Construct an <c>ErrorConstructor</c>-shaped function. When
    /// called with or without <c>new</c>, it creates an object
    /// inheriting from <paramref name="proto"/> and initializes
    /// <c>message</c> from the first argument (if present).
    /// </summary>
    private static JsFunction MakeErrorCtor(JsEngine engine, string name, JsObject proto)
    {
        var ctor = new JsFunction(name, (thisVal, args) =>
        {
            // When invoked via `new`, thisVal is the allocated
            // instance — DoNew pre-linked its prototype to
            // fn.prototype, which we'll set to `proto` below.
            // When invoked as a plain call, thisVal is undefined;
            // the spec says Error() returns a fresh error object.
            JsObject target = thisVal is JsObject o ? o : new JsObject { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined)
            {
                target.Set("message", JsValue.ToJsString(args[0]));
            }
            return target;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        return ctor;
    }

    /// <summary>
    /// ECMA §15.11.4.4 — <c>Error.prototype.toString()</c>. If
    /// both <c>name</c> and <c>message</c> are non-empty,
    /// returns <c>"name: message"</c>; otherwise whichever one is
    /// non-empty; empty string if both are empty.
    /// </summary>
    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsObject err)
        {
            return JsThrow.TypeError("Error.prototype.toString called on non-object");
        }
        string name = JsValue.ToJsString(err.Get("name"));
        string message = JsValue.ToJsString(err.Get("message"));
        if (string.IsNullOrEmpty(name)) return message;
        if (string.IsNullOrEmpty(message)) return name;
        return $"{name}: {message}";
    }
}
