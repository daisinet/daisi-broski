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

    public JsDomNode(JsDomBridge bridge, Node node)
    {
        Bridge = bridge;
        BackingNode = node;
        InstallNodeMethods();
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
