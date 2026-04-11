namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Promise</c> global — constructor, prototype, and the
/// four most-used statics. Scope for slice 3b-9:
///
/// <list type="bullet">
/// <item><c>new Promise(executor)</c> — calls the executor
///   synchronously with two native callbacks. Any throw
///   from the executor rejects the new promise.</item>
/// <item><c>.then(onFulfilled, onRejected)</c> — returns a
///   chained promise whose outcome depends on the handler.</item>
/// <item><c>.catch(onRejected)</c> — sugar for
///   <c>.then(undefined, onRejected)</c>.</item>
/// <item><c>Promise.resolve(v)</c> — wraps a value in a
///   fulfilled promise, with short-circuit for values that
///   are already promises.</item>
/// <item><c>Promise.reject(r)</c> — a rejected promise.</item>
/// <item><c>Promise.all(iterable)</c> — fulfilled with an
///   array of results when every input fulfills; rejected
///   on the first input rejection.</item>
/// <item><c>Promise.race(iterable)</c> — settles with the
///   first input to settle, in either direction.</item>
/// </list>
///
/// Deferred: <c>Promise.allSettled</c>, <c>Promise.any</c>,
/// <c>Promise.prototype.finally</c>, unhandled-rejection
/// tracking.
/// </summary>
internal static class BuiltinPromise
{
    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        engine.PromisePrototype = proto;

        proto.SetNonEnumerable("then", new JsFunction("then", (thisVal, args) =>
        {
            var p = RequirePromise(thisVal, "Promise.prototype.then");
            var onF = args.Count > 0 && args[0] is JsFunction fF ? fF : null;
            var onR = args.Count > 1 && args[1] is JsFunction fR ? fR : null;
            return p.Then(onF, onR);
        }));

        proto.SetNonEnumerable("catch", new JsFunction("catch", (thisVal, args) =>
        {
            var p = RequirePromise(thisVal, "Promise.prototype.catch");
            var onR = args.Count > 0 && args[0] is JsFunction fR ? fR : null;
            return p.Then(null, onR);
        }));

        // Constructor. Executor runs synchronously — standard
        // pattern: `new Promise((resolve, reject) => { ... })`.
        var ctor = new JsFunction("Promise", (vm, thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsFunction executor)
            {
                JsThrow.TypeError("Promise resolver is not a function");
            }
            var p = new JsPromise(engine);
            var resolve = new JsFunction("resolve", (t, a) =>
            {
                p.Resolve(a.Count > 0 ? a[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });
            var reject = new JsFunction("reject", (t, a) =>
            {
                p.Reject(a.Count > 0 ? a[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });
            try
            {
                vm.InvokeJsFunction((JsFunction)args[0]!, JsValue.Undefined, new object?[] { resolve, reject });
            }
            catch (JsThrowSignal sig)
            {
                p.Reject(sig.JsValue);
            }
            catch (JsRuntimeException rex)
            {
                p.Reject(rex.JsValue);
            }
            return p;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);

        // Static Promise.resolve — fulfilled promise with the
        // given value. Short-circuits for values that are
        // already promises (returns them unchanged).
        ctor.SetNonEnumerable("resolve", new JsFunction("resolve", (thisVal, args) =>
        {
            var v = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (v is JsPromise existing) return existing;
            var p = new JsPromise(engine);
            p.Resolve(v);
            return p;
        }));

        // Static Promise.reject — rejected promise.
        ctor.SetNonEnumerable("reject", new JsFunction("reject", (thisVal, args) =>
        {
            var p = new JsPromise(engine);
            p.Reject(args.Count > 0 ? args[0] : JsValue.Undefined);
            return p;
        }));

        // Static Promise.all — fulfilled with an array of
        // all results once every input has fulfilled.
        // Rejected immediately on the first input rejection.
        ctor.SetNonEnumerable("all", new JsFunction("all", (vm, thisVal, args) =>
            PromiseAll(vm, engine, args.Count > 0 ? args[0] : JsValue.Undefined)));

        // Static Promise.race — settles with whichever input
        // settles first, in either direction.
        ctor.SetNonEnumerable("race", new JsFunction("race", (vm, thisVal, args) =>
            PromiseRace(vm, engine, args.Count > 0 ? args[0] : JsValue.Undefined)));

        // ES2020 Promise.allSettled — always fulfills with an
        // array of status descriptors: {status, value} or
        // {status, reason}. Never rejects.
        ctor.SetNonEnumerable("allSettled", new JsFunction("allSettled", (vm, thisVal, args) =>
            PromiseAllSettled(vm, engine, args.Count > 0 ? args[0] : JsValue.Undefined)));

        // ES2021 Promise.any — fulfills with the first input
        // to fulfill; only rejects (with an AggregateError-
        // shaped object) when every input rejects.
        ctor.SetNonEnumerable("any", new JsFunction("any", (vm, thisVal, args) =>
            PromiseAny(vm, engine, args.Count > 0 ? args[0] : JsValue.Undefined)));

        // ES2018 Promise.prototype.finally(onFinally) — runs
        // the callback regardless of settlement kind and
        // passes the original settlement through.
        proto.SetNonEnumerable("finally", new JsFunction("finally", (vm, thisVal, args) =>
        {
            var p = RequirePromise(thisVal, "Promise.prototype.finally");
            var cb = args.Count > 0 && args[0] is JsFunction fn ? fn : null;
            // onFulfilled: call cb(), then return the value.
            var onF = new JsFunction("<finally-onF>", (vm2, t, a) =>
            {
                if (cb is not null) vm2.InvokeJsFunction(cb, JsValue.Undefined, Array.Empty<object?>());
                return a.Count > 0 ? a[0] : JsValue.Undefined;
            });
            // onRejected: call cb(), then re-throw the reason
            // so it propagates down the chain.
            var onR = new JsFunction("<finally-onR>", (vm2, t, a) =>
            {
                if (cb is not null) vm2.InvokeJsFunction(cb, JsValue.Undefined, Array.Empty<object?>());
                JsThrow.Raise(a.Count > 0 ? a[0] : JsValue.Undefined);
                return JsValue.Undefined; // unreachable
            });
            return p.Then(onF, onR);
        }));

        engine.Globals["Promise"] = ctor;
    }

    private static JsPromise PromiseAllSettled(JsVM vm, JsEngine engine, object? iterable)
    {
        var result = new JsPromise(engine);
        var inputs = CollectIterable(vm, iterable, out var errorThrown);
        if (errorThrown is not null)
        {
            result.Reject(errorThrown);
            return result;
        }

        if (inputs.Count == 0)
        {
            result.Resolve(new JsArray { Prototype = engine.ArrayPrototype });
            return result;
        }

        var statuses = new object?[inputs.Count];
        int remaining = inputs.Count;
        for (int i = 0; i < inputs.Count; i++)
        {
            int idx = i;
            JsPromise inner = inputs[i] is JsPromise p ? p : ResolveValue(engine, inputs[i]);
            inner.Then(
                new JsFunction("<allSettled-onF>", (t, a) =>
                {
                    var desc = new JsObject { Prototype = engine.ObjectPrototype };
                    desc.Set("status", "fulfilled");
                    desc.Set("value", a.Count > 0 ? a[0] : JsValue.Undefined);
                    statuses[idx] = desc;
                    remaining--;
                    if (remaining == 0)
                    {
                        var arr = new JsArray { Prototype = engine.ArrayPrototype };
                        foreach (var s in statuses) arr.Elements.Add(s);
                        result.Resolve(arr);
                    }
                    return JsValue.Undefined;
                }),
                new JsFunction("<allSettled-onR>", (t, a) =>
                {
                    var desc = new JsObject { Prototype = engine.ObjectPrototype };
                    desc.Set("status", "rejected");
                    desc.Set("reason", a.Count > 0 ? a[0] : JsValue.Undefined);
                    statuses[idx] = desc;
                    remaining--;
                    if (remaining == 0)
                    {
                        var arr = new JsArray { Prototype = engine.ArrayPrototype };
                        foreach (var s in statuses) arr.Elements.Add(s);
                        result.Resolve(arr);
                    }
                    return JsValue.Undefined;
                }));
        }
        return result;
    }

    private static JsPromise PromiseAny(JsVM vm, JsEngine engine, object? iterable)
    {
        var result = new JsPromise(engine);
        var inputs = CollectIterable(vm, iterable, out var errorThrown);
        if (errorThrown is not null)
        {
            result.Reject(errorThrown);
            return result;
        }

        if (inputs.Count == 0)
        {
            result.Reject(MakeAggregateError(engine, "All promises were rejected", Array.Empty<object?>()));
            return result;
        }

        var errors = new object?[inputs.Count];
        int remaining = inputs.Count;
        for (int i = 0; i < inputs.Count; i++)
        {
            int idx = i;
            JsPromise inner = inputs[i] is JsPromise p ? p : ResolveValue(engine, inputs[i]);
            inner.Then(
                new JsFunction("<any-onF>", (t, a) =>
                {
                    result.Resolve(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }),
                new JsFunction("<any-onR>", (t, a) =>
                {
                    errors[idx] = a.Count > 0 ? a[0] : JsValue.Undefined;
                    remaining--;
                    if (remaining == 0)
                    {
                        result.Reject(MakeAggregateError(
                            engine,
                            "All promises were rejected",
                            errors));
                    }
                    return JsValue.Undefined;
                }));
        }
        return result;
    }

    private static JsObject MakeAggregateError(JsEngine engine, string message, IReadOnlyList<object?> errors)
    {
        // Minimal AggregateError shape — Error-like with a
        // message + errors array. We don't yet have a full
        // AggregateError prototype; this matches what real
        // code usually tests for.
        var err = new JsObject { Prototype = engine.ErrorPrototype };
        err.Set("name", "AggregateError");
        err.Set("message", message);
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        foreach (var e in errors) arr.Elements.Add(e);
        err.Set("errors", arr);
        return err;
    }

    private static JsPromise RequirePromise(object? thisVal, string name)
    {
        if (thisVal is not JsPromise p)
        {
            JsThrow.TypeError($"{name} called on non-Promise");
        }
        return (JsPromise)thisVal!;
    }

    private static JsPromise PromiseAll(JsVM vm, JsEngine engine, object? iterable)
    {
        var result = new JsPromise(engine);

        // Materialize the iterable into a list of values.
        // Each value gets turned into a promise (via
        // Promise.resolve semantics) and we wait for all of
        // them. The results array is populated in the same
        // order as the input, not the order of settlement.
        var inputs = CollectIterable(vm, iterable, out var errorThrown);
        if (errorThrown is not null)
        {
            result.Reject(errorThrown);
            return result;
        }

        if (inputs.Count == 0)
        {
            // Synchronous resolve with an empty array is
            // spec-legal for Promise.all([]).
            var empty = new JsArray { Prototype = engine.ArrayPrototype };
            result.Resolve(empty);
            return result;
        }

        var results = new object?[inputs.Count];
        int remaining = inputs.Count;
        for (int i = 0; i < inputs.Count; i++)
        {
            int idx = i;
            JsPromise inner = inputs[i] is JsPromise p ? p : ResolveValue(engine, inputs[i]);
            inner.Then(
                new JsFunction("<all-onF>", (t, a) =>
                {
                    results[idx] = a.Count > 0 ? a[0] : JsValue.Undefined;
                    remaining--;
                    if (remaining == 0)
                    {
                        var arr = new JsArray { Prototype = engine.ArrayPrototype };
                        foreach (var r in results) arr.Elements.Add(r);
                        result.Resolve(arr);
                    }
                    return JsValue.Undefined;
                }),
                new JsFunction("<all-onR>", (t, a) =>
                {
                    result.Reject(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }));
        }
        return result;
    }

    private static JsPromise PromiseRace(JsVM vm, JsEngine engine, object? iterable)
    {
        var result = new JsPromise(engine);
        var inputs = CollectIterable(vm, iterable, out var errorThrown);
        if (errorThrown is not null)
        {
            result.Reject(errorThrown);
            return result;
        }
        foreach (var input in inputs)
        {
            JsPromise inner = input is JsPromise p ? p : ResolveValue(engine, input);
            inner.Then(
                new JsFunction("<race-onF>", (t, a) =>
                {
                    result.Resolve(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }),
                new JsFunction("<race-onR>", (t, a) =>
                {
                    result.Reject(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }));
        }
        return result;
    }

    private static JsPromise ResolveValue(JsEngine engine, object? value)
    {
        var p = new JsPromise(engine);
        p.Resolve(value);
        return p;
    }

    private static List<object?> CollectIterable(JsVM vm, object? iterable, out object? errorThrown)
    {
        errorThrown = null;
        var list = new List<object?>();
        if (iterable is null || iterable is JsUndefined || iterable is JsNull)
        {
            errorThrown = MakeTypeError(vm.Engine, "Promise iterable is not iterable");
            return list;
        }

        var iter = vm.GetIteratorFromIterable(iterable);
        if (iter is not JsObject iterObj) return list;
        var nextFn = iterObj.Get("next") as JsFunction;
        if (nextFn is null)
        {
            errorThrown = MakeTypeError(vm.Engine, "Promise iterator has no next()");
            return list;
        }
        while (true)
        {
            object? stepResult;
            try
            {
                stepResult = vm.InvokeJsFunction(nextFn, iterObj, Array.Empty<object?>());
            }
            catch (JsThrowSignal sig)
            {
                errorThrown = sig.JsValue;
                return list;
            }
            if (stepResult is not JsObject step)
            {
                errorThrown = MakeTypeError(vm.Engine, "Iterator result is not an object");
                return list;
            }
            if (JsValue.ToBoolean(step.Get("done"))) return list;
            list.Add(step.Get("value"));
        }
    }

    private static JsObject MakeTypeError(JsEngine engine, string message)
    {
        var err = new JsObject { Prototype = engine.TypeErrorPrototype };
        err.Set("message", message);
        return err;
    }
}
