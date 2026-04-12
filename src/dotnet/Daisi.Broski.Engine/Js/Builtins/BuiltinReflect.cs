namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2015 <c>Reflect</c> namespace — exposes the default
/// meta-object operations that <see cref="JsProxy"/> traps
/// over as plain static functions, so user code can forward
/// an unhandled trap back to the spec default via
/// <c>Reflect.get(target, key, receiver)</c>.
///
/// <para>
/// The surface shipped here is the intersection of what
/// frameworks actually call: <c>get</c>, <c>set</c>,
/// <c>has</c>, <c>deleteProperty</c>, <c>ownKeys</c>,
/// <c>getPrototypeOf</c>, <c>setPrototypeOf</c>,
/// <c>apply</c>, <c>construct</c>. Deferred:
/// <c>getOwnPropertyDescriptor</c> / <c>defineProperty</c>
/// (needs a full descriptor model), <c>isExtensible</c> /
/// <c>preventExtensions</c>.
/// </para>
/// </summary>
internal static class BuiltinReflect
{
    public static void Install(JsEngine engine)
    {
        var reflect = new JsObject { Prototype = engine.ObjectPrototype };

        reflect.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var (target, key) = RequireTargetKey(args, "Reflect.get");
            return target.Get(key);
        }));

        reflect.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            if (args.Count < 3)
            {
                JsThrow.TypeError("Reflect.set: expected (target, key, value)");
                return false;
            }
            var (target, key) = RequireTargetKey(args, "Reflect.set");
            target.Set(key, args[2]);
            return true;
        }));

        reflect.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var (target, key) = RequireTargetKey(args, "Reflect.has");
            return target.Has(key);
        }));

        reflect.SetNonEnumerable("deleteProperty", new JsFunction("deleteProperty", (thisVal, args) =>
        {
            var (target, key) = RequireTargetKey(args, "Reflect.deleteProperty");
            return target.Delete(key);
        }));

        reflect.SetNonEnumerable("ownKeys", new JsFunction("ownKeys", (thisVal, args) =>
        {
            var target = RequireTarget(args, "Reflect.ownKeys");
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            foreach (var k in target.OwnKeys())
            {
                arr.Elements.Add(k);
            }
            return arr;
        }));

        reflect.SetNonEnumerable("getPrototypeOf", new JsFunction("getPrototypeOf", (thisVal, args) =>
        {
            var target = RequireTarget(args, "Reflect.getPrototypeOf");
            return (object?)target.Prototype ?? JsValue.Null;
        }));

        reflect.SetNonEnumerable("setPrototypeOf", new JsFunction("setPrototypeOf", (thisVal, args) =>
        {
            var target = RequireTarget(args, "Reflect.setPrototypeOf");
            if (args.Count < 2)
            {
                JsThrow.TypeError("Reflect.setPrototypeOf: expected (target, prototype)");
                return false;
            }
            if (args[1] is JsObject proto)
            {
                target.Prototype = proto;
            }
            else if (args[1] is JsNull)
            {
                target.Prototype = null;
            }
            else
            {
                JsThrow.TypeError("Reflect.setPrototypeOf: prototype must be object or null");
            }
            return true;
        }));

        // apply(target, thisArg, argsArray) — calls target with
        // the given this + spread args. Used by frameworks that
        // wrap a function and want to forward the original call.
        reflect.SetNonEnumerable("apply", new JsFunction("apply", (vm, thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsFunction fn)
            {
                JsThrow.TypeError("Reflect.apply: target must be a function");
                return null;
            }
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var callArgs = Array.Empty<object?>();
            if (args.Count > 2 && args[2] is JsArray a)
            {
                callArgs = a.Elements.ToArray();
            }
            return vm.InvokeJsFunction(fn, thisArg, callArgs);
        }));

        // construct(target, argsArray) — equivalent to
        // `new target(...args)`. Used by decorator / factory
        // patterns and by Reflect-as-trap-forwarder handlers
        // that wrap a class.
        reflect.SetNonEnumerable("construct", new JsFunction("construct", (vm, thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsFunction fn)
            {
                JsThrow.TypeError("Reflect.construct: target must be a function");
                return null;
            }
            var ctorArgs = Array.Empty<object?>();
            if (args.Count > 1 && args[1] is JsArray a)
            {
                ctorArgs = a.Elements.ToArray();
            }
            return vm.ConstructJsFunction(fn, ctorArgs);
        }));

        engine.Globals["Reflect"] = reflect;
    }

    private static JsObject RequireTarget(IReadOnlyList<object?> args, string name)
    {
        if (args.Count == 0 || args[0] is not JsObject target)
        {
            JsThrow.TypeError($"{name}: target must be an object");
            return null!;
        }
        return target;
    }

    private static (JsObject target, string key) RequireTargetKey(IReadOnlyList<object?> args, string name)
    {
        var target = RequireTarget(args, name);
        string key = args.Count > 1 ? JsValue.ToJsString(args[1]) : "undefined";
        return (target, key);
    }
}
