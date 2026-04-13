using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js.Dom;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>MutationObserver</c> — the WHATWG DOM mutation API,
/// real implementation backed by
/// <see cref="MutationDispatcher"/>. Phase 3 shipped a stub
/// constructor that accepted observe/disconnect calls but
/// never fired; phase 5d wires it through the live mutation
/// dispatcher every <see cref="Document"/> now lazily owns,
/// so handler callbacks receive accurate
/// <c>MutationRecord</c> arrays after each microtask.
///
/// <para>
/// Per spec, records are queued on the observer and the
/// observer's callback is invoked once per microtask
/// drain — even if a hundred mutations happened, the handler
/// runs once with all records batched together. We schedule
/// the drain via the engine's microtask queue, matching the
/// spec's "compound microtask" model.
/// </para>
///
/// <para>
/// Records carry their JS-visible shape directly:
/// <c>type</c> (<c>"childList"</c> / <c>"attributes"</c> /
/// <c>"characterData"</c>), <c>target</c>, <c>addedNodes</c>,
/// <c>removedNodes</c>, <c>previousSibling</c>,
/// <c>nextSibling</c>, <c>attributeName</c>, <c>oldValue</c>.
/// Wrapped DOM nodes route through the engine's
/// <see cref="JsDomBridge"/> so identity is stable across
/// callback invocations — the same JS-side wrapper a script
/// got from <c>document.querySelector</c> shows up in the
/// record's <c>target</c>.
/// </para>
/// </summary>
internal static class BuiltinMutationObserver
{
    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("observe", new JsFunction("observe", (vm, thisVal, args) =>
        {
            var obs = RequireObserver(thisVal, "MutationObserver.prototype.observe");
            if (args.Count < 1)
            {
                JsThrow.TypeError("observe: target is required");
            }
            var target = ResolveNode(args[0]);
            if (target is null)
            {
                JsThrow.TypeError("observe: target is not a Node");
            }
            var opts = args.Count > 1 ? args[1] as JsObject : null;
            obs.Observe(target!, ParseOptions(opts));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("disconnect", new JsFunction("disconnect", (thisVal, args) =>
        {
            var obs = RequireObserver(thisVal, "MutationObserver.prototype.disconnect");
            obs.Disconnect();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("takeRecords", new JsFunction("takeRecords", (thisVal, args) =>
        {
            var obs = RequireObserver(thisVal, "MutationObserver.prototype.takeRecords");
            return obs.TakeRecords();
        }));

        var ctor = new JsFunction("MutationObserver", (thisVal, args) =>
        {
            if (args.Count < 1 || args[0] is not JsFunction cb)
            {
                JsThrow.TypeError("MutationObserver requires a callback function");
            }
            return new JsMutationObserver(engine, (JsFunction)args[0]!) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["MutationObserver"] = ctor;
    }

    private static JsMutationObserver RequireObserver(object? thisVal, string name)
    {
        if (thisVal is not JsMutationObserver obs)
        {
            JsThrow.TypeError($"{name} called on non-MutationObserver");
        }
        return (JsMutationObserver)thisVal!;
    }

    /// <summary>Resolve the script-supplied target argument into a
    /// backing <see cref="Node"/>. Both raw DOM nodes (from
    /// <c>document</c> directly) and bridge-wrapped
    /// <see cref="JsDomNode"/> instances are accepted.</summary>
    internal static Node? ResolveNode(object? value) => value switch
    {
        JsDomNode wrap => wrap.BackingNode,
        Node raw => raw,
        _ => null,
    };

    /// <summary>Translate the script-side <c>MutationObserverInit</c>
    /// bag into a <see cref="MutationObserveOptions"/>. Per spec,
    /// passing <c>attributeOldValue</c> / <c>attributeFilter</c>
    /// implies <c>attributes: true</c>; same for
    /// <c>characterDataOldValue</c> implying
    /// <c>characterData: true</c>.</summary>
    private static MutationObserveOptions ParseOptions(JsObject? opts)
    {
        if (opts is null)
        {
            return new MutationObserveOptions { ChildList = true };
        }
        bool childList = JsValue.ToBoolean(opts.Get("childList"));
        bool attributes = JsValue.ToBoolean(opts.Get("attributes"));
        bool characterData = JsValue.ToBoolean(opts.Get("characterData"));
        bool subtree = JsValue.ToBoolean(opts.Get("subtree"));
        bool attributeOldValue = JsValue.ToBoolean(opts.Get("attributeOldValue"));
        bool characterDataOldValue = JsValue.ToBoolean(opts.Get("characterDataOldValue"));
        IReadOnlyCollection<string>? attributeFilter = null;
        if (opts.Get("attributeFilter") is JsArray arr)
        {
            var filter = new HashSet<string>(arr.Elements.Count);
            foreach (var e in arr.Elements) filter.Add(JsValue.ToJsString(e));
            attributeFilter = filter;
        }
        if (attributeOldValue || attributeFilter is not null) attributes = true;
        if (characterDataOldValue) characterData = true;
        return new MutationObserveOptions
        {
            ChildList = childList,
            Attributes = attributes,
            CharacterData = characterData,
            Subtree = subtree,
            AttributeOldValue = attributeOldValue,
            CharacterDataOldValue = characterDataOldValue,
            AttributeFilter = attributeFilter,
        };
    }
}

/// <summary>
/// Instance state for a JS <c>MutationObserver</c>. Tracks
/// the script callback, every active <c>observe</c>
/// registration, and the queued records waiting to be
/// delivered.
/// </summary>
public sealed class JsMutationObserver : JsObject
{
    private readonly JsEngine _engine;
    private readonly JsFunction _callback;
    private readonly List<MutationDispatcher.Registration> _registrations = new();
    private readonly List<MutationRecord> _queue = new();
    private bool _drainScheduled;

    public JsMutationObserver(JsEngine engine, JsFunction callback)
    {
        _engine = engine;
        _callback = callback;
    }

    /// <summary>Register this observer with the target
    /// document's dispatcher. Called by <c>observe()</c>.</summary>
    public void Observe(Node target, MutationObserveOptions opts)
    {
        var doc = target.OwnerDocument ?? target as Document;
        if (doc is null)
        {
            // Disconnected node — no document to attach to.
            // The spec treats this as "no records will fire";
            // we honor that by quietly succeeding.
            return;
        }
        var reg = doc.MutationDispatcher.Register(target, opts, OnRecord);
        _registrations.Add(reg);
    }

    /// <summary>Drop every active registration. Called by
    /// <c>disconnect()</c>.</summary>
    public void Disconnect()
    {
        foreach (var reg in _registrations)
        {
            // We reach the dispatcher through the registration's
            // target node — the same node we passed to Register.
            var doc = reg.Target.OwnerDocument ?? reg.Target as Document;
            doc?.MutationDispatcher.Unregister(reg);
        }
        _registrations.Clear();
        _queue.Clear();
    }

    /// <summary>Drain pending records, returning them as a
    /// JS array. Called by <c>takeRecords()</c>.</summary>
    public JsArray TakeRecords()
    {
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var rec in _queue) arr.Elements.Add(WrapRecord(rec));
        _queue.Clear();
        return arr;
    }

    /// <summary>Invoked by the dispatcher for each matching
    /// mutation. Queues the record and schedules a microtask
    /// to deliver if one isn't already pending.</summary>
    private void OnRecord(MutationRecord record)
    {
        _queue.Add(record);
        if (_drainScheduled) return;
        _drainScheduled = true;
        _engine.EventLoop.QueueMicrotask(Drain);
    }

    private void Drain()
    {
        _drainScheduled = false;
        if (_queue.Count == 0) return;
        var snapshot = _queue.ToArray();
        _queue.Clear();
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var rec in snapshot) arr.Elements.Add(WrapRecord(rec));
        _engine.Vm.InvokeJsFunction(_callback, this, new object?[] { arr, this });
    }

    private object WrapRecord(MutationRecord record)
    {
        var obj = new JsObject { Prototype = _engine.ObjectPrototype };
        obj.Set("type", record.Type switch
        {
            MutationRecordType.ChildList => "childList",
            MutationRecordType.Attributes => "attributes",
            MutationRecordType.CharacterData => "characterData",
            _ => "unknown",
        });
        obj.Set("target", WrapNode(record.Target));
        obj.Set("addedNodes", WrapNodeList(record.AddedNodes));
        obj.Set("removedNodes", WrapNodeList(record.RemovedNodes));
        obj.Set("previousSibling", record.PreviousSibling is null ? JsValue.Null : WrapNode(record.PreviousSibling));
        obj.Set("nextSibling", record.NextSibling is null ? JsValue.Null : WrapNode(record.NextSibling));
        obj.Set("attributeName", (object?)record.AttributeName ?? JsValue.Null);
        obj.Set("attributeNamespace", (object?)record.AttributeNamespace ?? JsValue.Null);
        obj.Set("oldValue", (object?)record.OldValue ?? JsValue.Null);
        return obj;
    }

    private object WrapNode(Node node)
    {
        // Prefer the engine's bridge wrapper so identity is
        // stable across script-visible callbacks. When no
        // bridge is attached (engine without AttachDocument),
        // fall back to the raw node.
        return _engine.DomBridge?.Wrap(node) ?? (object)node;
    }

    private JsArray WrapNodeList(IReadOnlyList<Node> nodes)
    {
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var n in nodes) arr.Elements.Add(WrapNode(n));
        return arr;
    }
}
