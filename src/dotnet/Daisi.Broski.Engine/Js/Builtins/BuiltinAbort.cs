using Daisi.Broski.Engine.Js.Dom;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>AbortController</c> + <c>AbortSignal</c> — the web
/// platform's standard cancellation primitive. A signal
/// is the shared cancellation token scripts pass to
/// cancellable operations; a controller is the handle the
/// caller uses to flip that token on.
///
/// <para>
/// This slice ships the core types but does not yet wire
/// them into <c>fetch</c>: fetch currently runs
/// synchronously, so there's no window during which
/// cancellation would actually interrupt anything.
/// Scripts can register <c>signal.addEventListener('abort', ...)</c>
/// and have the listener fire on <c>controller.abort()</c>,
/// which covers the common use case of coordinating
/// cancellation across multiple subsystems (user hits
/// stop → propagate to all pending operations).
/// </para>
///
/// <para>
/// Deferred: <c>AbortSignal.timeout(ms)</c>,
/// <c>AbortSignal.any(signals)</c>, <c>signal.throwIfAborted()</c>
/// — all straightforward additions once we need them.
/// </para>
/// </summary>
internal static class BuiltinAbort
{
    public static void Install(JsEngine engine)
    {
        var signalProto = InstallSignal(engine);
        InstallController(engine, signalProto);
    }

    // =======================================================
    // AbortSignal
    // =======================================================

    private static JsObject InstallSignal(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (thisVal, args) =>
        {
            var sig = RequireSignal(thisVal, "AbortSignal.prototype.addEventListener");
            if (args.Count < 2) return JsValue.Undefined;
            var type = JsValue.ToJsString(args[0]);
            if (args[1] is not JsFunction cb) return JsValue.Undefined;
            bool once = false;
            if (args.Count > 2 && args[2] is JsObject opts)
            {
                once = JsValue.ToBoolean(opts.Get("once"));
            }
            sig.AddListener(type, cb, once);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (thisVal, args) =>
        {
            var sig = RequireSignal(thisVal, "AbortSignal.prototype.removeEventListener");
            if (args.Count < 2) return JsValue.Undefined;
            var type = JsValue.ToJsString(args[0]);
            if (args[1] is not JsFunction cb) return JsValue.Undefined;
            sig.RemoveListener(type, cb);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("dispatchEvent", new JsFunction("dispatchEvent", (vm, thisVal, args) =>
        {
            var sig = RequireSignal(thisVal, "AbortSignal.prototype.dispatchEvent");
            if (args.Count == 0 || args[0] is not JsDomEvent evt)
            {
                JsThrow.TypeError("dispatchEvent: argument is not an Event");
                return false;
            }
            sig.FireEvent(vm, evt);
            return !evt.DefaultPrevented;
        }));

        proto.SetNonEnumerable("throwIfAborted", new JsFunction("throwIfAborted", (thisVal, args) =>
        {
            var sig = RequireSignal(thisVal, "AbortSignal.prototype.throwIfAborted");
            if (sig.Aborted)
            {
                JsThrow.Raise(sig.Reason);
            }
            return JsValue.Undefined;
        }));

        // Static factory: AbortSignal.abort(reason?) returns
        // a signal that is already in the aborted state.
        var ctor = new JsFunction("AbortSignal", (thisVal, args) =>
        {
            // Direct construction via `new AbortSignal()` is
            // illegal per spec. The canonical way is
            // `new AbortController().signal`.
            JsThrow.TypeError("AbortSignal is not directly constructible");
            return null;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);

        ctor.SetNonEnumerable("abort", new JsFunction("abort", (vm, thisVal, args) =>
        {
            var sig = new JsAbortSignal(engine) { Prototype = proto };
            var reason = args.Count > 0 ? args[0] : JsValue.Undefined;
            sig.Abort(vm, reason);
            return sig;
        }));

        engine.Globals["AbortSignal"] = ctor;
        return proto;
    }

    // =======================================================
    // AbortController
    // =======================================================

    private static void InstallController(JsEngine engine, JsObject signalProto)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("abort", new JsFunction("abort", (vm, thisVal, args) =>
        {
            var ctrl = RequireController(thisVal, "AbortController.prototype.abort");
            var reason = args.Count > 0 ? args[0] : JsValue.Undefined;
            ctrl.Signal.Abort(vm, reason);
            return JsValue.Undefined;
        }));

        var ctor = new JsFunction("AbortController", (thisVal, args) =>
        {
            var signal = new JsAbortSignal(engine) { Prototype = signalProto };
            var controller = new JsAbortController(signal) { Prototype = proto };
            return controller;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["AbortController"] = ctor;
    }

    private static JsAbortSignal RequireSignal(object? thisVal, string name)
    {
        if (thisVal is not JsAbortSignal s)
        {
            JsThrow.TypeError($"{name} called on non-AbortSignal");
        }
        return (JsAbortSignal)thisVal!;
    }

    private static JsAbortController RequireController(object? thisVal, string name)
    {
        if (thisVal is not JsAbortController c)
        {
            JsThrow.TypeError($"{name} called on non-AbortController");
        }
        return (JsAbortController)thisVal!;
    }
}

/// <summary>
/// Instance state for a JS <c>AbortSignal</c>. Stores its
/// listener list separately from <see cref="Dom.JsDomNode"/>
/// because signals are not DOM nodes — there's no parent
/// chain to walk, so the dispatch is flat: every registered
/// listener gets fired at the target, no capture / bubble
/// phases.
/// </summary>
public sealed class JsAbortSignal : JsObject
{
    public bool Aborted { get; private set; }
    public object? Reason { get; private set; } = JsValue.Undefined;
    private readonly JsEngine _engine;
    private readonly List<Listener> _listeners = new();

    internal sealed class Listener
    {
        public string Type { get; }
        public JsFunction Callback { get; }
        public bool Once { get; }
        public bool Removed { get; set; }

        public Listener(string type, JsFunction callback, bool once)
        {
            Type = type;
            Callback = callback;
            Once = once;
        }
    }

    public JsAbortSignal(JsEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "aborted" => Aborted,
        "reason" => Reason,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        // aborted / reason are read-only — the only way to
        // change them is via controller.abort(). Writes from
        // script are silently ignored (non-strict mode).
        if (key is "aborted" or "reason") return;
        base.Set(key, value);
    }

    public void AddListener(string type, JsFunction callback, bool once)
    {
        foreach (var existing in _listeners)
        {
            if (!existing.Removed && existing.Type == type &&
                ReferenceEquals(existing.Callback, callback))
            {
                return; // dedup
            }
        }
        _listeners.Add(new Listener(type, callback, once));
    }

    public void RemoveListener(string type, JsFunction callback)
    {
        for (int i = 0; i < _listeners.Count; i++)
        {
            var e = _listeners[i];
            if (!e.Removed && e.Type == type && ReferenceEquals(e.Callback, callback))
            {
                e.Removed = true;
                _listeners.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// Flip the signal to the aborted state and fire every
    /// registered 'abort' listener exactly once. Subsequent
    /// calls are no-ops — once aborted, always aborted.
    /// </summary>
    public void Abort(JsVM vm, object? reason)
    {
        if (Aborted) return;
        Aborted = true;
        Reason = reason is JsUndefined
            ? BuildAbortError()
            : reason;
        var evt = new JsDomEvent("abort", bubbles: false, cancelable: false)
        {
            Prototype = _engine.EventPrototype,
            Target = this,
            CurrentTarget = this,
            EventPhase = JsDomEvent.PhaseAtTarget,
        };
        FireEvent(vm, evt);
    }

    /// <summary>
    /// Fire <paramref name="evt"/> at this signal — a flat
    /// dispatch (no capture / bubble walk; signals aren't
    /// in a tree). Listeners are snapshotted before
    /// iteration so a mid-dispatch add doesn't fire on the
    /// same event.
    /// </summary>
    internal void FireEvent(JsVM vm, JsDomEvent evt)
    {
        evt.CurrentTarget = this;
        var snapshot = new List<Listener>(_listeners);
        foreach (var entry in snapshot)
        {
            if (entry.Removed) continue;
            if (entry.Type != evt.Type) continue;
            if (evt.ImmediatePropagationStopped) break;
            try
            {
                vm.InvokeJsFunction(entry.Callback, this, new object?[] { evt });
            }
            catch (JsThrowSignal)
            {
                // Swallow like the DOM event dispatch does.
            }
            catch (JsRuntimeException) { }
            if (entry.Once)
            {
                entry.Removed = true;
                _listeners.Remove(entry);
            }
        }
    }

    /// <summary>
    /// Spec: when <c>abort()</c> is called without a reason,
    /// the reason defaults to a fresh <c>DOMException</c>
    /// with name <c>AbortError</c>. We don't ship DOMException
    /// yet, so we synthesize a plain Error-shaped object
    /// with the right name / message — enough for
    /// <c>signal.reason.name === 'AbortError'</c> checks.
    /// </summary>
    private JsObject BuildAbortError()
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", "AbortError");
        err.Set("message", "The operation was aborted");
        return err;
    }
}

/// <summary>
/// Instance state for a JS <c>AbortController</c>. Holds a
/// single <see cref="JsAbortSignal"/> that is created at
/// construction and exposed via the <c>signal</c>
/// read-through accessor.
/// </summary>
public sealed class JsAbortController : JsObject
{
    public JsAbortSignal Signal { get; }

    public JsAbortController(JsAbortSignal signal)
    {
        Signal = signal;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "signal") return Signal;
        return base.Get(key);
    }
}
