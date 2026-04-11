namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Web API timer globals: <c>setTimeout</c>, <c>clearTimeout</c>,
/// <c>setInterval</c>, <c>clearInterval</c>, and the ES2015
/// <c>queueMicrotask</c>. All are thin shims over
/// <see cref="JsEventLoop"/>.
///
/// Each scheduler captures extra arguments after the delay and
/// passes them to the callback at invocation time — matching
/// the WHATWG timers spec (HTML §8.7). <c>clearTimeout</c> and
/// <c>clearInterval</c> are interchangeable (the spec allows
/// clearing either kind with either function).
/// </summary>
internal static class BuiltinTimers
{
    public static void Install(JsEngine engine)
    {
        engine.Globals["setTimeout"] =
            new JsFunction("setTimeout", (vm, t, a) => SetTimeout(engine, a, isInterval: false));
        engine.Globals["setInterval"] =
            new JsFunction("setInterval", (vm, t, a) => SetTimeout(engine, a, isInterval: true));
        engine.Globals["clearTimeout"] =
            new JsFunction("clearTimeout", (vm, t, a) => ClearTimer(engine, a));
        engine.Globals["clearInterval"] =
            new JsFunction("clearInterval", (vm, t, a) => ClearTimer(engine, a));
        engine.Globals["queueMicrotask"] =
            new JsFunction("queueMicrotask", (vm, t, a) => QueueMicrotask(engine, a));
    }

    private static object? SetTimeout(
        JsEngine engine,
        IReadOnlyList<object?> args,
        bool isInterval)
    {
        if (args.Count == 0 || args[0] is not JsFunction cb)
        {
            return JsThrow.TypeError(
                isInterval
                    ? "setInterval requires a function callback"
                    : "setTimeout requires a function callback");
        }
        double delay = args.Count > 1 ? JsValue.ToNumber(args[1]) : 0;

        // Args 2+ are passed to the callback at invocation time.
        var forwarded = Array.Empty<object?>();
        if (args.Count > 2)
        {
            forwarded = new object?[args.Count - 2];
            for (int i = 2; i < args.Count; i++)
            {
                forwarded[i - 2] = args[i];
            }
        }

        int id = engine.EventLoop.ScheduleTimer(delay, cb, forwarded, isInterval);
        return (double)id;
    }

    private static object? ClearTimer(JsEngine engine, IReadOnlyList<object?> args)
    {
        if (args.Count > 0)
        {
            int id = (int)JsValue.ToInt32(args[0]);
            engine.EventLoop.ClearTimer(id);
        }
        return JsValue.Undefined;
    }

    private static object? QueueMicrotask(JsEngine engine, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || args[0] is not JsFunction cb)
        {
            return JsThrow.TypeError("queueMicrotask requires a function callback");
        }
        engine.EventLoop.QueueMicrotask(() =>
        {
            engine.Vm.InvokeJsFunction(cb, JsValue.Undefined, Array.Empty<object?>());
        });
        return JsValue.Undefined;
    }
}
