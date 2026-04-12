using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// JS-side wrapper around a <see cref="Node"/>. Subclass of
/// <see cref="JsObject"/> so the VM treats it like any other
/// object — member access, assignment, <c>typeof</c>, and
/// <c>instanceof</c> all work without special casing.
///
/// Property resolution is split in two:
/// <list type="bullet">
/// <item>Read-through DOM accessors (tagName, id, childNodes, ...)
///   are handled by overriding <see cref="Get"/>, so every read
///   reflects the current DOM state — no caching, no snapshot.</item>
/// <item>Method-like properties (appendChild, getAttribute, ...)
///   are installed as <see cref="JsFunction"/> instances on the
///   wrapper's own property bag at construction time. They
///   capture the wrapper and the backing <see cref="Node"/>, so
///   calling <c>el.getAttribute('id')</c> from script reaches
///   the real DOM.</item>
/// </list>
///
/// The engine maintains a per-node wrapper cache
/// (<see cref="JsDomBridge.Wrap"/>) so identity is stable — if
/// the same <see cref="Node"/> is exposed to script twice, the
/// same wrapper is returned both times and <c>el === el</c> holds.
/// </summary>
public class JsDomNode : JsObject
{
    public Node BackingNode { get; }
    protected readonly JsDomBridge Bridge;

    /// <summary>
    /// Event listeners registered on this wrapper, keyed by
    /// event type. Allocated lazily so the common case —
    /// nodes the script touches but never subscribes to —
    /// doesn't pay the dictionary cost. Iteration order
    /// matches registration order, matching the spec's
    /// "same event listener list in the order they were
    /// added" requirement.
    /// </summary>
    internal Dictionary<string, List<EventListenerEntry>>? Listeners;

    public JsDomNode(JsDomBridge bridge, Node node)
    {
        Bridge = bridge;
        BackingNode = node;
        InstallNodeMethods();
    }

    /// <summary>
    /// One registered event listener: the script callback,
    /// its capture / once / passive flags, and a sentinel
    /// used during dispatch to mark listeners that have
    /// already fired (so a nested dispatch doesn't re-fire
    /// a one-shot listener).
    /// </summary>
    internal sealed class EventListenerEntry
    {
        public JsFunction Callback { get; }
        public bool Capture { get; }
        public bool Once { get; }
        public bool Removed { get; set; }

        public EventListenerEntry(JsFunction callback, bool capture, bool once)
        {
            Callback = callback;
            Capture = capture;
            Once = once;
        }
    }

    /// <summary>
    /// Install the methods shared by every node kind. Each
    /// method is a <see cref="JsFunction"/> whose native body
    /// closure-captures this wrapper, so <c>var f = el.appendChild; f(child)</c>
    /// still targets the correct element.
    /// </summary>
    private void InstallNodeMethods()
    {
        SetNonEnumerable("appendChild", new JsFunction("appendChild", (thisVal, args) =>
        {
            var child = RequireNodeArg(args, 0, "appendChild");
            BackingNode.AppendChild(child.BackingNode);
            return child;
        }));
        SetNonEnumerable("removeChild", new JsFunction("removeChild", (thisVal, args) =>
        {
            var child = RequireNodeArg(args, 0, "removeChild");
            BackingNode.RemoveChild(child.BackingNode);
            return child;
        }));
        SetNonEnumerable("insertBefore", new JsFunction("insertBefore", (thisVal, args) =>
        {
            var newChild = RequireNodeArg(args, 0, "insertBefore");
            JsDomNode? reference = null;
            if (args.Count > 1 && args[1] is JsDomNode r) reference = r;
            else if (args.Count > 1 && args[1] is not JsNull && args[1] is not JsUndefined)
            {
                JsThrow.TypeError("insertBefore: reference is not a Node");
            }
            BackingNode.InsertBefore(newChild.BackingNode, reference?.BackingNode);
            return newChild;
        }));
        SetNonEnumerable("contains", new JsFunction("contains", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsDomNode other) return false;
            return BackingNode.Contains(other.BackingNode);
        }));
        SetNonEnumerable("hasChildNodes", new JsFunction("hasChildNodes", (thisVal, args) =>
        {
            return BackingNode.HasChildNodes;
        }));

        // ParentNode mixin — `append` and `prepend` both
        // accept a variadic list of Node or string values
        // and insert them as children. Strings become text
        // nodes at the point they're inserted.
        SetNonEnumerable("append", new JsFunction("append", (thisVal, args) =>
        {
            foreach (var a in args)
            {
                BackingNode.AppendChild(CoerceChild(a));
            }
            return JsValue.Undefined;
        }));
        SetNonEnumerable("prepend", new JsFunction("prepend", (thisVal, args) =>
        {
            var first = BackingNode.FirstChild;
            foreach (var a in args)
            {
                BackingNode.InsertBefore(CoerceChild(a), first);
            }
            return JsValue.Undefined;
        }));
        SetNonEnumerable("replaceChildren", new JsFunction("replaceChildren", (thisVal, args) =>
        {
            while (BackingNode.FirstChild is not null)
            {
                BackingNode.RemoveChild(BackingNode.FirstChild);
            }
            foreach (var a in args)
            {
                BackingNode.AppendChild(CoerceChild(a));
            }
            return JsValue.Undefined;
        }));

        // ChildNode mixin — `remove` detaches this node
        // from its parent (no-op if already detached);
        // `before` / `after` insert siblings relative to
        // this node. All three accept Node or string
        // arguments like `append`.
        SetNonEnumerable("remove", new JsFunction("remove", (thisVal, args) =>
        {
            var parent = BackingNode.ParentNode;
            parent?.RemoveChild(BackingNode);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("before", new JsFunction("before", (thisVal, args) =>
        {
            var parent = BackingNode.ParentNode;
            if (parent is null) return JsValue.Undefined;
            foreach (var a in args)
            {
                parent.InsertBefore(CoerceChild(a), BackingNode);
            }
            return JsValue.Undefined;
        }));
        SetNonEnumerable("after", new JsFunction("after", (thisVal, args) =>
        {
            var parent = BackingNode.ParentNode;
            if (parent is null) return JsValue.Undefined;
            var next = BackingNode.NextSibling;
            foreach (var a in args)
            {
                parent.InsertBefore(CoerceChild(a), next);
            }
            return JsValue.Undefined;
        }));

        // ----- Event target surface (slice 3c-4) -----
        SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (thisVal, args) =>
        {
            if (args.Count < 2) return JsValue.Undefined;
            var type = JsValue.ToJsString(args[0]);
            if (args[1] is not JsFunction cb) return JsValue.Undefined;
            var (capture, once) = ParseListenerOptions(args.Count > 2 ? args[2] : JsValue.Undefined);
            AddEventListener(type, cb, capture, once);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (thisVal, args) =>
        {
            if (args.Count < 2) return JsValue.Undefined;
            var type = JsValue.ToJsString(args[0]);
            if (args[1] is not JsFunction cb) return JsValue.Undefined;
            var (capture, _) = ParseListenerOptions(args.Count > 2 ? args[2] : JsValue.Undefined);
            RemoveEventListener(type, cb, capture);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("dispatchEvent", new JsFunction("dispatchEvent", (vm, thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsDomEvent evt)
            {
                JsThrow.TypeError("dispatchEvent: argument is not an Event");
                return false;
            }
            return DispatchEvent(vm, evt);
        }));
    }

    /// <summary>
    /// Parse the options argument to <c>addEventListener</c> /
    /// <c>removeEventListener</c>. Historical forms: a plain
    /// boolean (legacy "useCapture") or an object with
    /// <c>capture</c> / <c>once</c> / <c>passive</c> keys.
    /// </summary>
    private static (bool capture, bool once) ParseListenerOptions(object? options)
    {
        if (options is bool b) return (b, false);
        if (options is JsObject obj)
        {
            bool capture = JsValue.ToBoolean(obj.Get("capture"));
            bool once = JsValue.ToBoolean(obj.Get("once"));
            return (capture, once);
        }
        return (false, false);
    }

    /// <summary>
    /// Add an event listener. Per the spec, adding an
    /// already-registered (type, callback, capture) triple is
    /// a no-op — the dedup matches so users can register
    /// idempotently without accidentally firing a listener
    /// twice.
    /// </summary>
    public void AddEventListener(string type, JsFunction callback, bool capture, bool once)
    {
        Listeners ??= new Dictionary<string, List<EventListenerEntry>>();
        if (!Listeners.TryGetValue(type, out var list))
        {
            list = new List<EventListenerEntry>();
            Listeners[type] = list;
        }
        foreach (var existing in list)
        {
            if (ReferenceEquals(existing.Callback, callback) &&
                existing.Capture == capture && !existing.Removed)
            {
                return; // dedup
            }
        }
        list.Add(new EventListenerEntry(callback, capture, once));
    }

    /// <summary>
    /// Remove a previously-added listener. Match is by
    /// <c>(type, callback, capture)</c> triple — the
    /// <c>once</c> flag is not part of the match because the
    /// spec allows removing a one-shot before it fires.
    /// </summary>
    public void RemoveEventListener(string type, JsFunction callback, bool capture)
    {
        if (Listeners is null) return;
        if (!Listeners.TryGetValue(type, out var list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (!e.Removed && ReferenceEquals(e.Callback, callback) && e.Capture == capture)
            {
                e.Removed = true;
                list.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// Dispatch <paramref name="evt"/> at this node, running
    /// the full capturing → target → bubbling phases per
    /// DOM spec. Returns <c>true</c> if no listener called
    /// <c>preventDefault()</c>, <c>false</c> otherwise.
    /// </summary>
    public bool DispatchEvent(JsVM vm, JsDomEvent evt)
    {
        evt.Target = this;
        evt.CurrentTarget = null;
        evt.EventPhase = JsDomEvent.PhaseCapturing;
        evt.PropagationStopped = false;
        evt.ImmediatePropagationStopped = false;

        // Build the propagation path from target up to the
        // root. Snapshot the wrappers so mid-dispatch
        // removals don't change the walk.
        var path = new List<JsDomNode>();
        path.Add(this);
        for (var p = BackingNode.ParentNode; p is not null; p = p.ParentNode)
        {
            path.Add(Bridge.Wrap(p));
        }

        // Capture phase: walk from root → target-excluded.
        for (int i = path.Count - 1; i >= 1; i--)
        {
            if (evt.PropagationStopped) break;
            evt.CurrentTarget = path[i];
            FireListeners(vm, path[i], evt, captureOnly: true);
        }

        // Target phase: fire both capture and non-capture
        // listeners registered directly on the target, in
        // registration order.
        if (!evt.PropagationStopped)
        {
            evt.EventPhase = JsDomEvent.PhaseAtTarget;
            evt.CurrentTarget = this;
            FireListeners(vm, this, evt, captureOnly: null);
        }

        // Bubble phase: only if the event bubbles.
        if (evt.Bubbles && !evt.PropagationStopped)
        {
            evt.EventPhase = JsDomEvent.PhaseBubbling;
            for (int i = 1; i < path.Count; i++)
            {
                if (evt.PropagationStopped) break;
                evt.CurrentTarget = path[i];
                FireListeners(vm, path[i], evt, captureOnly: false);
            }
        }

        evt.EventPhase = JsDomEvent.PhaseNone;
        evt.CurrentTarget = null;
        return !evt.DefaultPrevented;
    }

    /// <summary>
    /// Fire every listener on <paramref name="node"/> that
    /// matches the current phase. A <c>null</c>
    /// <paramref name="captureOnly"/> means target-phase —
    /// fire both capture and non-capture listeners in
    /// registration order. A non-null value means fire only
    /// the matching subset.
    /// </summary>
    private static void FireListeners(JsVM vm, JsDomNode node, JsDomEvent evt, bool? captureOnly)
    {
        if (node.Listeners is null) return;
        if (!node.Listeners.TryGetValue(evt.Type, out var list)) return;
        // Snapshot the list so a mid-dispatch addEventListener
        // doesn't cause new listeners to fire in the same
        // dispatch — matching the spec.
        var snapshot = new List<EventListenerEntry>(list);
        foreach (var entry in snapshot)
        {
            if (entry.Removed) continue;
            if (captureOnly is bool c && entry.Capture != c) continue;
            if (evt.ImmediatePropagationStopped) break;
            try
            {
                vm.InvokeJsFunction(entry.Callback, node, new object?[] { evt });
            }
            catch (JsThrowSignal)
            {
                // An uncaught throw from a listener surfaces
                // back to us as a JsThrowSignal (the
                // cross-boundary escape sentinel). Swallow
                // it so one bad listener doesn't abort
                // dispatch — real browsers would report it
                // via window.onerror. `InvokeJsFunction`
                // already restored the VM state before
                // re-raising, so the engine is clean.
            }
            catch (JsRuntimeException)
            {
                // Same treatment if the throw bubbled
                // higher and was wrapped.
            }
            if (entry.Once)
            {
                entry.Removed = true;
                list.Remove(entry);
            }
        }
    }

    /// <summary>
    /// Coerce an argument at <paramref name="index"/> into a
    /// <see cref="JsDomNode"/>, throwing a script-visible
    /// <c>TypeError</c> when the caller passed a non-node. The
    /// guard mirrors the way browser DOM methods reject
    /// non-node arguments.
    /// </summary>
    protected JsDomNode RequireNodeArg(IReadOnlyList<object?> args, int index, string method)
    {
        if (args.Count <= index || args[index] is not JsDomNode node)
        {
            JsThrow.TypeError($"{method}: argument {index} is not a Node");
            return null!; // unreachable
        }
        return node;
    }

    /// <summary>
    /// Coerce an <c>append</c> / <c>prepend</c> / <c>before</c>
    /// / <c>after</c> argument into a real <see cref="Node"/>.
    /// Per spec these methods accept either Node or string
    /// values; strings are converted to <see cref="Text"/>
    /// nodes at the point they're inserted.
    /// </summary>
    private Node CoerceChild(object? arg)
    {
        if (arg is JsDomNode node) return node.BackingNode;
        var owner = BackingNode.OwnerDocument ?? BackingNode as Document;
        if (owner is null)
        {
            JsThrow.TypeError("Cannot insert child into detached node");
            return null!;
        }
        return owner.CreateTextNode(JsValue.ToJsString(arg));
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        // Read-through DOM accessors take priority over the
        // property bag so a user can't shadow them with a
        // normal assignment — DOM state is authoritative.
        switch (key)
        {
            case "nodeType": return (double)(int)BackingNode.NodeType;
            case "nodeName": return BackingNode.NodeName;
            case "parentNode": return Bridge.WrapOrNull(BackingNode.ParentNode);
            case "parentElement": return Bridge.WrapOrNull(BackingNode.ParentNode as Element);
            case "childNodes": return BuildChildNodesArray();
            case "firstChild": return Bridge.WrapOrNull(BackingNode.FirstChild);
            case "lastChild": return Bridge.WrapOrNull(BackingNode.LastChild);
            case "previousSibling": return Bridge.WrapOrNull(BackingNode.PreviousSibling);
            case "nextSibling": return Bridge.WrapOrNull(BackingNode.NextSibling);
            case "textContent": return BackingNode.TextContent;
            case "ownerDocument": return Bridge.WrapOrNull(BackingNode.OwnerDocument);
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        switch (key)
        {
            case "textContent":
                SetTextContent(JsValue.ToJsString(value));
                return;
            case "nodeType":
            case "nodeName":
            case "parentNode":
            case "childNodes":
            case "firstChild":
            case "lastChild":
            case "previousSibling":
            case "nextSibling":
                // Silently ignore writes to read-only accessors,
                // matching browser behavior in non-strict mode.
                return;
        }
        base.Set(key, value);
    }

    /// <summary>
    /// Replace every child of this node with a single text
    /// node carrying <paramref name="text"/>. Mirrors
    /// <c>Node.textContent = ...</c>: clears all descendants,
    /// then inserts one fresh text node with the given content.
    /// Assigning an empty string clears the node.
    /// </summary>
    private void SetTextContent(string text)
    {
        // Drop all existing children first.
        while (BackingNode.FirstChild is not null)
        {
            BackingNode.RemoveChild(BackingNode.FirstChild);
        }
        if (text.Length == 0) return;
        var owner = BackingNode.OwnerDocument ?? BackingNode as Document;
        if (owner is null) return;
        BackingNode.AppendChild(owner.CreateTextNode(text));
    }

    /// <summary>
    /// Build a live-ish snapshot of the node's children as a
    /// JS array of wrappers. Not a true live NodeList — every
    /// property read produces a fresh array — but the wrappers
    /// themselves are cached so identity is stable across
    /// snapshots. The "live" part is deferred to a future
    /// slice where a real <c>NodeList</c> proxy matters.
    /// </summary>
    private JsArray BuildChildNodesArray()
    {
        var arr = new JsArray { Prototype = Bridge.Engine.ArrayPrototype };
        foreach (var child in BackingNode.ChildNodes)
        {
            arr.Elements.Add(Bridge.Wrap(child));
        }
        return arr;
    }

}
