using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// Minimum-viable implementation of the DOM Traversal
/// <c>NodeIterator</c> interface. Built for Blazor Web's
/// auto-start path, which scans the document for
/// <c>&lt;!--Blazor:{...}--&gt;</c> circuit markers via:
/// <code>
///   var it = document.createNodeIterator(
///       document.body, NodeFilter.SHOW_COMMENT);
///   var node;
///   while ((node = it.nextNode())) { ... }
/// </code>
///
/// <para>
/// Iteration order is depth-first, pre-order, matching the
/// spec. The <c>whatToShow</c> bitmask filters by node type
/// (bit index = nodeType - 1, per spec §6.1). An optional
/// <see cref="JsFunction"/> filter can further reject
/// candidates by returning anything other than
/// <c>NodeFilter.FILTER_ACCEPT</c> (value 1).
/// </para>
///
/// <para>
/// Deliberately deferred: live DOM mutation tracking, the
/// mysterious "pointerBeforeReferenceNode" spec gymnastics,
/// and <c>previousNode()</c>'s reverse walk. The forward
/// walk is the only one Blazor (and most real consumers)
/// use.
/// </para>
/// </summary>
internal sealed class JsNodeIterator : JsObject
{
    private readonly JsDomBridge _bridge;
    private readonly Node _root;
    private readonly long _whatToShow;
    private readonly JsFunction? _filter;
    private Node? _current; // null before first call to nextNode
    private bool _done;

    public JsNodeIterator(
        JsDomBridge bridge, Node root, long whatToShow, JsFunction? filter)
    {
        _bridge = bridge;
        _root = root;
        _whatToShow = whatToShow;
        _filter = filter;

        SetNonEnumerable("root", _bridge.Wrap(root));
        SetNonEnumerable("whatToShow", (double)whatToShow);
        SetNonEnumerable("filter", (object?)filter ?? JsValue.Null);

        // nextNode() — forward walk; returns the next node
        // that passes the whatToShow bitmask + filter
        // callback, or null when the tree is exhausted.
        SetNonEnumerable("nextNode", new JsFunction("nextNode", (vm, thisVal, args) =>
        {
            if (_done) return JsValue.Null;
            var next = _current is null
                ? _root // first call: start at root
                : Advance(_current, _root);
            while (next is not null)
            {
                _current = next;
                if (TypeMatches(next) && FilterAccepts(vm, next))
                {
                    return _bridge.Wrap(next);
                }
                next = Advance(next, _root);
            }
            _done = true;
            return JsValue.Null;
        }));

        // previousNode() — reverse walk. Not implemented;
        // returns null so scripts that try it degrade to
        // "iterator empty" rather than throwing.
        SetNonEnumerable("previousNode", new JsFunction("previousNode",
            (_, _) => JsValue.Null));

        // detach() — historically released the iterator's
        // hold on the DOM. Modern spec: no-op.
        SetNonEnumerable("detach", new JsFunction("detach",
            (_, _) => JsValue.Undefined));
    }

    /// <summary>Depth-first pre-order step. Given the current
    /// node, return the next node in the walk, stopping at
    /// <paramref name="root"/> boundary (never crosses
    /// upward past root).</summary>
    private static Node? Advance(Node from, Node root)
    {
        // Descend into first child when possible.
        if (from.FirstChild is not null)
        {
            return from.FirstChild;
        }
        // Otherwise, advance to the next sibling; if none,
        // walk up until we find one or we've crossed the
        // root boundary.
        var cursor = from;
        while (cursor is not null && !ReferenceEquals(cursor, root))
        {
            if (cursor.NextSibling is not null) return cursor.NextSibling;
            cursor = cursor.ParentNode;
        }
        return null;
    }

    /// <summary>True when the node's type bit is set in
    /// <see cref="_whatToShow"/>. Per DOM Traversal §6.1 the
    /// bit is (nodeType - 1).</summary>
    private bool TypeMatches(Node n)
    {
        int type = (int)n.NodeType;
        if (type < 1 || type > 32) return false;
        long bit = 1L << (type - 1);
        return (_whatToShow & bit) != 0;
    }

    /// <summary>Invoke the optional filter callback. Returns
    /// true (keep the node) when the filter returns
    /// <c>NodeFilter.FILTER_ACCEPT</c> (1), or when no
    /// filter was provided.</summary>
    private bool FilterAccepts(JsVM vm, Node n)
    {
        if (_filter is null) return true;
        try
        {
            var wrapped = _bridge.Wrap(n);
            var verdict = vm.InvokeJsFunction(_filter, JsValue.Undefined,
                new object?[] { wrapped });
            var d = JsValue.ToNumber(verdict);
            return d == 1.0; // FILTER_ACCEPT
        }
        catch
        {
            // A filter that throws is treated as a reject,
            // keeping the walk alive. Matches the Chrome
            // behavior of swallowing filter exceptions.
            return false;
        }
    }
}
