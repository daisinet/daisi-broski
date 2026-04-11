namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Function.prototype.call</c>, <c>apply</c>, and <c>bind</c>.
/// All three thunk through <see cref="JsVM.InvokeJsFunction"/>,
/// which is why they need the <c>NativeCallable</c> delegate
/// form that receives a VM reference.
///
/// <c>bind</c> returns a fresh native function that closes over
/// the target function, the bound <c>this</c>, and any bound
/// prefix arguments, concatenating them with whatever runtime
/// arguments the bound function is eventually called with. This
/// is the common-case ES5 semantics; the corner cases
/// (<c>new</c>-through-a-bound function's prototype chain,
/// <c>length</c> adjustment) are deferred.
/// </summary>
internal static class BuiltinFunction
{
    public static void Install(JsEngine engine)
    {
        var proto = engine.FunctionPrototype;

        proto.SetNonEnumerable("call",
            new JsFunction("call", (vm, t, a) => Call(vm, t, a)));
        proto.SetNonEnumerable("apply",
            new JsFunction("apply", (vm, t, a) => Apply(vm, t, a)));
        proto.SetNonEnumerable("bind",
            new JsFunction("bind", (vm, t, a) => Bind(engine, t, a)));
        Builtins.Method(proto, "toString", ToStringMethod);
    }

    /// <summary>
    /// <c>fn.call(thisArg, arg1, arg2, ...)</c> — invoke
    /// <c>fn</c> with the supplied <c>this</c> binding and the
    /// remaining arguments spread in order.
    /// </summary>
    private static object? Call(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsFunction fn)
        {
            return JsThrow.TypeError("Function.prototype.call called on non-function");
        }
        object? boundThis = args.Count > 0 ? args[0] : JsValue.Undefined;
        var forwarded = new object?[Math.Max(0, args.Count - 1)];
        for (int i = 1; i < args.Count; i++) forwarded[i - 1] = args[i];
        return vm.InvokeJsFunction(fn, boundThis, forwarded);
    }

    /// <summary>
    /// <c>fn.apply(thisArg, argsArray)</c> — invoke <c>fn</c>
    /// with the supplied <c>this</c> and the args drawn from
    /// <c>argsArray</c>'s dense elements. <c>null</c> or
    /// <c>undefined</c> for <c>argsArray</c> is treated as no
    /// args, per spec.
    /// </summary>
    private static object? Apply(JsVM vm, object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsFunction fn)
        {
            return JsThrow.TypeError("Function.prototype.apply called on non-function");
        }
        object? boundThis = args.Count > 0 ? args[0] : JsValue.Undefined;

        object?[] forwarded;
        if (args.Count < 2 || args[1] is JsNull || args[1] is JsUndefined)
        {
            forwarded = Array.Empty<object?>();
        }
        else if (args[1] is JsArray arr)
        {
            forwarded = new object?[arr.Elements.Count];
            for (int i = 0; i < arr.Elements.Count; i++) forwarded[i] = arr.Elements[i];
        }
        else
        {
            return JsThrow.TypeError("Function.prototype.apply second argument must be an array");
        }
        return vm.InvokeJsFunction(fn, boundThis, forwarded);
    }

    /// <summary>
    /// <c>fn.bind(thisArg, ...preArgs)</c> — return a new
    /// function that, when called, invokes <c>fn</c> with
    /// <c>this = thisArg</c> and the arguments
    /// <c>[preArgs..., runtimeArgs...]</c>. The bound function
    /// inherits from <see cref="JsEngine.FunctionPrototype"/>
    /// like any other function value.
    /// </summary>
    private static object? Bind(JsEngine engine, object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsFunction target)
        {
            return JsThrow.TypeError("Function.prototype.bind called on non-function");
        }

        object? boundThis = args.Count > 0 ? args[0] : JsValue.Undefined;
        var preArgs = new object?[Math.Max(0, args.Count - 1)];
        for (int i = 1; i < args.Count; i++) preArgs[i - 1] = args[i];

        // Closure over target, boundThis, and preArgs.
        var bound = new JsFunction("bound", (vm, _, runtimeArgs) =>
        {
            var combined = new object?[preArgs.Length + runtimeArgs.Count];
            Array.Copy(preArgs, combined, preArgs.Length);
            for (int i = 0; i < runtimeArgs.Count; i++)
            {
                combined[preArgs.Length + i] = runtimeArgs[i];
            }
            return vm.InvokeJsFunction(target, boundThis, combined);
        });
        bound.Prototype = engine.FunctionPrototype;
        return bound;
    }

    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        if (thisVal is not JsFunction fn)
        {
            return JsThrow.TypeError("Function.prototype.toString called on non-function");
        }
        return $"function {fn.Name ?? ""}() {{ [native code] }}";
    }
}
