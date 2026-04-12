namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2015 <c>Proxy</c> built-in — a minimum-viable subset
/// that covers the traps framework authors actually use
/// for reactivity: <c>get</c> / <c>set</c> / <c>has</c> /
/// <c>deleteProperty</c> / <c>ownKeys</c>. That subset is
/// enough for Vue 3 reactive / MobX / Preact Signals to
/// install tracking code on a script object graph without
/// throwing.
///
/// <para>
/// Deferred: <c>apply</c> / <c>construct</c> traps (proxy
/// around a function), <c>getPrototypeOf</c> /
/// <c>setPrototypeOf</c>, <c>isExtensible</c> /
/// <c>preventExtensions</c>, <c>getOwnPropertyDescriptor</c>
/// / <c>defineProperty</c>. We don't have a full property
/// descriptor model yet so the descriptor traps would be
/// lossy.
/// </para>
///
/// <para>
/// Design note: <see cref="JsProxy"/> is a subclass of
/// <see cref="JsObject"/> that overrides the five virtual
/// methods the VM already consults for property access
/// (<c>Get</c> / <c>Set</c> / <c>Has</c> / <c>Delete</c> /
/// <c>OwnKeys</c>). The VM's bytecode handlers call those
/// virtuals unchanged, so Proxy support lands without any
/// opcode-level changes — the trap dispatch happens inside
/// each override via <see cref="JsVM.InvokeJsFunction"/>.
/// </para>
/// </summary>
internal static class BuiltinProxy
{
    public static void Install(JsEngine engine)
    {
        var ctor = new JsFunction("Proxy", (thisVal, args) =>
        {
            if (args.Count < 2)
            {
                JsThrow.TypeError("Proxy constructor requires (target, handler)");
            }
            if (args[0] is not JsObject target)
            {
                JsThrow.TypeError("Proxy: target must be an object");
                return null;
            }
            if (args[1] is not JsObject handler)
            {
                JsThrow.TypeError("Proxy: handler must be an object");
                return null;
            }
            return new JsProxy(engine, target, handler);
        });
        engine.Globals["Proxy"] = ctor;
    }
}

/// <summary>
/// Instance state for a JS <c>Proxy</c>. Holds a reference
/// to the engine so its virtual property-access overrides
/// can invoke handler trap functions through the VM's
/// re-entrant <see cref="JsVM.InvokeJsFunction"/> dispatch.
///
/// When a trap is absent from the handler object, each
/// override falls through to the default behavior on the
/// target — the same "unset trap" semantics ES2015 defines
/// via <c>Reflect.*</c>.
/// </summary>
public sealed class JsProxy : JsObject
{
    public JsObject Target { get; }
    public JsObject Handler { get; }
    private readonly JsEngine _engine;

    public JsProxy(JsEngine engine, JsObject target, JsObject handler)
    {
        _engine = engine;
        Target = target;
        Handler = handler;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (Handler.Get("get") is JsFunction trap)
        {
            return _engine.Vm.InvokeJsFunction(trap, Handler, new object?[] { Target, key, this });
        }
        return Target.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (Handler.Get("set") is JsFunction trap)
        {
            _engine.Vm.InvokeJsFunction(trap, Handler, new object?[] { Target, key, value, this });
            return;
        }
        Target.Set(key, value);
    }

    /// <inheritdoc />
    public override bool Has(string key)
    {
        if (Handler.Get("has") is JsFunction trap)
        {
            return JsValue.ToBoolean(
                _engine.Vm.InvokeJsFunction(trap, Handler, new object?[] { Target, key }));
        }
        return Target.Has(key);
    }

    /// <inheritdoc />
    public override bool Delete(string key)
    {
        if (Handler.Get("deleteProperty") is JsFunction trap)
        {
            return JsValue.ToBoolean(
                _engine.Vm.InvokeJsFunction(trap, Handler, new object?[] { Target, key }));
        }
        return Target.Delete(key);
    }

    /// <inheritdoc />
    public override IEnumerable<string> OwnKeys()
    {
        if (Handler.Get("ownKeys") is JsFunction trap)
        {
            var result = _engine.Vm.InvokeJsFunction(trap, Handler, new object?[] { Target });
            if (result is JsArray arr)
            {
                var keys = new List<string>(arr.Elements.Count);
                foreach (var k in arr.Elements)
                {
                    if (k is string s) keys.Add(s);
                }
                return keys;
            }
        }
        return Target.OwnKeys();
    }
}
