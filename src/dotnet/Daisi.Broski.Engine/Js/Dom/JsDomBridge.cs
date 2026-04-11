using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// Factory and cache for JS-side DOM wrappers. The engine
/// holds one <see cref="JsDomBridge"/> per attached document
/// (via <see cref="JsEngine.AttachDocument"/>); it issues one
/// wrapper per <see cref="Node"/> so identity is stable
/// across repeated property reads. Call <see cref="Wrap"/>
/// whenever a C# side emits a node into JS-visible position
/// — the bridge handles the subtype dispatch and caching.
/// </summary>
public sealed class JsDomBridge
{
    public JsEngine Engine { get; }

    private readonly Dictionary<Node, JsDomNode> _cache =
        new(ReferenceEqualityComparer.Instance);

    public JsDomBridge(JsEngine engine)
    {
        Engine = engine;
    }

    /// <summary>
    /// Return the wrapper for <paramref name="node"/>, creating
    /// one on first use. Identity is cached, so the same
    /// <see cref="Node"/> always resolves to the same wrapper
    /// instance (important for <c>===</c> to work across
    /// repeated DOM reads — e.g. <c>el.parentNode === el.parentNode</c>).
    /// </summary>
    public JsDomNode Wrap(Node node)
    {
        if (_cache.TryGetValue(node, out var existing)) return existing;
        JsDomNode wrapper = node switch
        {
            Document doc => new JsDomDocument(this, doc),
            Element el => new JsDomElement(this, el),
            Text text => new JsDomText(this, text),
            _ => new JsDomNode(this, node),
        };
        _cache[node] = wrapper;
        return wrapper;
    }

    /// <summary>
    /// Same as <see cref="Wrap"/> but returns <see cref="JsValue.Null"/>
    /// when <paramref name="node"/> is <c>null</c>. Used by
    /// accessors that may legitimately be null (parentNode on
    /// a detached node, nextSibling on a last child, ...).
    /// </summary>
    public object? WrapOrNull(Node? node)
    {
        if (node is null) return JsValue.Null;
        return Wrap(node);
    }

    /// <summary>
    /// Wrap a collection of elements as a fresh
    /// <see cref="JsArray"/>. Every element is resolved through
    /// <see cref="Wrap"/>, preserving identity for any caller
    /// that has already seen the same DOM nodes.
    /// </summary>
    public JsArray WrapElements(IEnumerable<Element> elements)
    {
        var arr = new JsArray { Prototype = Engine.ArrayPrototype };
        foreach (var el in elements)
        {
            arr.Elements.Add(Wrap(el));
        }
        return arr;
    }

    /// <summary>
    /// Empty JS array used by query methods that return no
    /// matches. A fresh array per call so mutations don't
    /// leak across calls.
    /// </summary>
    public JsArray EmptyArray()
    {
        return new JsArray { Prototype = Engine.ArrayPrototype };
    }
}
