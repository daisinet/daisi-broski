namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Three-state life cycle of an ES2015 <see cref="JsPromise"/>.
/// Transitions only flow from <see cref="Pending"/> to either
/// <see cref="Fulfilled"/> or <see cref="Rejected"/>; settled
/// promises are immutable.
/// </summary>
public enum PromiseState
{
    Pending,
    Fulfilled,
    Rejected,
}

/// <summary>
/// ES2015 <c>Promise</c> object. Implemented on top of the
/// engine's existing microtask queue: every <c>.then</c>
/// callback runs as a microtask, matching the spec's
/// "PromiseJobs queue" semantics and the observable
/// "code after <c>.then</c> runs before the callback does"
/// behavior real engines have.
///
/// State transitions:
///
/// <list type="bullet">
/// <item><b>Pending → Fulfilled</b> when <c>resolve(v)</c>
///   is invoked with a non-thenable value.</item>
/// <item><b>Pending → (follows v)</b> when <c>resolve(v)</c>
///   is invoked with another promise or a thenable — the
///   current promise adopts <c>v</c>'s eventual state.</item>
/// <item><b>Pending → Rejected</b> when <c>reject(r)</c> is
///   invoked. Calling reject on a non-pending promise is a
///   no-op, matching spec.</item>
/// </list>
///
/// Each queued then-callback is a 4-tuple of
/// <c>(onFulfilled, onRejected, resolveNext, rejectNext)</c>
/// — the first two are user functions from <c>.then(...)</c>
/// and the latter two are the settler functions of the
/// promise that <c>.then</c> returned. Whichever of
/// fulfill/reject fires matches on one handler; if that
/// handler returns a value, the chained promise settles with
/// it (and follows thenables). If the handler throws, the
/// chained promise rejects with the thrown value.
///
/// Deferred from this slice:
/// <list type="bullet">
/// <item>Native adoption of user-defined <c>Symbol.toStringTag</c>.</item>
/// <item>Unhandled-rejection tracking / host notification.</item>
/// <item><c>Promise.any</c>, <c>Promise.allSettled</c>,
///   <c>Promise.prototype.finally</c>.</item>
/// </list>
/// </summary>
public sealed class JsPromise : JsObject
{
    private readonly JsEngine _engine;

    /// <summary>Current settled state.</summary>
    public PromiseState State { get; private set; } = PromiseState.Pending;

    /// <summary>
    /// Fulfillment value (when <see cref="State"/> is
    /// <see cref="PromiseState.Fulfilled"/>) or rejection
    /// reason (when <see cref="State"/> is
    /// <see cref="PromiseState.Rejected"/>). Undefined when
    /// still pending.
    /// </summary>
    public object? Value { get; private set; } = JsValue.Undefined;

    /// <summary>
    /// Callbacks that are waiting for this promise to
    /// settle. Each is a 4-tuple of user-provided fulfillment
    /// handler, user-provided rejection handler, and the
    /// settler pair for the downstream chained promise.
    /// Drained into the microtask queue when the promise
    /// settles, then cleared.
    /// </summary>
    internal readonly List<PendingThen> PendingThens = new();

    internal readonly struct PendingThen
    {
        public JsFunction? OnFulfilled { get; }
        public JsFunction? OnRejected { get; }
        public JsPromise Next { get; }

        public PendingThen(JsFunction? onFulfilled, JsFunction? onRejected, JsPromise next)
        {
            OnFulfilled = onFulfilled;
            OnRejected = onRejected;
            Next = next;
        }
    }

    public JsPromise(JsEngine engine)
    {
        _engine = engine;
        Prototype = engine.PromisePrototype;
    }

    /// <summary>
    /// Transition from <see cref="PromiseState.Pending"/> to
    /// <see cref="PromiseState.Fulfilled"/> with the given
    /// value, or — if the value is itself a thenable —
    /// begin adopting that thenable's eventual state. Calling
    /// resolve on a non-pending promise is silently ignored.
    /// </summary>
    public void Resolve(object? value)
    {
        if (State != PromiseState.Pending) return;

        // Promise resolution procedure — if value is another
        // promise, adopt its state. If value is a generic
        // thenable (has a .then that is callable), subscribe
        // to it.
        if (ReferenceEquals(value, this))
        {
            Reject(MakeTypeError("Chaining cycle detected for promise"));
            return;
        }

        if (value is JsPromise innerPromise)
        {
            // Adopt innerPromise's outcome when it settles.
            innerPromise.Then(
                new JsFunction("<adopt-fulfill>", (t, a) =>
                {
                    Resolve(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }),
                new JsFunction("<adopt-reject>", (t, a) =>
                {
                    Reject(a.Count > 0 ? a[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                }));
            return;
        }

        if (value is JsObject jo && jo.Get("then") is JsFunction thenFn)
        {
            // Generic thenable adoption: call then(resolve, reject)
            // on the thenable, with resolve/reject bound to this
            // promise. Any throws from the thenable's then() method
            // reject this promise.
            var resolver = new JsFunction("<thenable-resolve>", (t, a) =>
            {
                Resolve(a.Count > 0 ? a[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });
            var rejecter = new JsFunction("<thenable-reject>", (t, a) =>
            {
                Reject(a.Count > 0 ? a[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });
            _engine.EventLoop.QueueMicrotask(() =>
            {
                try
                {
                    _engine.Vm.InvokeJsFunction(thenFn, jo, new object?[] { resolver, rejecter });
                }
                catch (JsThrowSignal sig)
                {
                    Reject(sig.JsValue);
                }
                catch (JsRuntimeException rex)
                {
                    Reject(rex.JsValue);
                }
            });
            return;
        }

        State = PromiseState.Fulfilled;
        Value = value;
        FlushPending();
    }

    /// <summary>
    /// Transition this promise to <see cref="PromiseState.Rejected"/>
    /// with the given reason. A no-op if already settled.
    /// </summary>
    public void Reject(object? reason)
    {
        if (State != PromiseState.Pending) return;
        State = PromiseState.Rejected;
        Value = reason;
        FlushPending();
    }

    private void FlushPending()
    {
        // Each pending `.then` was registered while we were
        // still pending. Now that we've settled, schedule
        // exactly one of its handlers as a microtask.
        var snapshot = PendingThens.ToArray();
        PendingThens.Clear();
        foreach (var pt in snapshot)
        {
            Schedule(pt);
        }
    }

    private void Schedule(PendingThen pt)
    {
        _engine.EventLoop.QueueMicrotask(() => RunHandler(pt));
    }

    private void RunHandler(PendingThen pt)
    {
        if (State == PromiseState.Fulfilled)
        {
            if (pt.OnFulfilled is null)
            {
                // Pass-through: the next promise mirrors our state.
                pt.Next.Resolve(Value);
                return;
            }
            try
            {
                var result = _engine.Vm.InvokeJsFunction(pt.OnFulfilled, JsValue.Undefined, new[] { Value });
                pt.Next.Resolve(result);
            }
            catch (JsThrowSignal sig)
            {
                pt.Next.Reject(sig.JsValue);
            }
            catch (JsRuntimeException rex)
            {
                pt.Next.Reject(rex.JsValue);
            }
        }
        else // Rejected
        {
            if (pt.OnRejected is null)
            {
                pt.Next.Reject(Value);
                return;
            }
            try
            {
                var result = _engine.Vm.InvokeJsFunction(pt.OnRejected, JsValue.Undefined, new[] { Value });
                pt.Next.Resolve(result);
            }
            catch (JsThrowSignal sig)
            {
                pt.Next.Reject(sig.JsValue);
            }
            catch (JsRuntimeException rex)
            {
                pt.Next.Reject(rex.JsValue);
            }
        }
    }

    /// <summary>
    /// Register a pair of settlement handlers and return a
    /// new promise that reflects the outcome of whichever
    /// handler runs. If the current promise is already
    /// settled, the handler is scheduled immediately as a
    /// microtask; otherwise it joins the pending queue.
    /// </summary>
    public JsPromise Then(JsFunction? onFulfilled, JsFunction? onRejected)
    {
        var next = new JsPromise(_engine);
        var pt = new PendingThen(onFulfilled, onRejected, next);
        if (State == PromiseState.Pending)
        {
            PendingThens.Add(pt);
        }
        else
        {
            Schedule(pt);
        }
        return next;
    }

    private JsObject MakeTypeError(string message)
    {
        var err = new JsObject { Prototype = _engine.TypeErrorPrototype };
        err.Set("message", message);
        return err;
    }
}
