using Daisi.Broski.Engine.Dom.Selectors;

namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// Abstract base of the DOM node hierarchy. A <see cref="Node"/> is a
/// plain C# object graph — it knows nothing about JavaScript, nothing
/// about layout, nothing about styling. The JS engine (phase 3) will
/// wrap these in host objects; the tree builder (phase 1) produces
/// them directly from tokens.
///
/// The node maintains a doubly-linked sibling list plus a pointer to
/// its parent, so tree walks and sibling traversal are O(1). The
/// children are also exposed as an <see cref="IReadOnlyList{Node}"/>
/// for indexed access without walking the linked list.
///
/// Mutations go through <see cref="AppendChild"/>, <see cref="InsertBefore"/>,
/// and <see cref="RemoveChild"/> — never touch the linked-list pointers
/// directly from outside the Node class.
/// </summary>
public abstract class Node
{
    /// <summary>DOM <c>nodeType</c> constant — matches the numeric values
    /// script code reads from <c>Node.ELEMENT_NODE</c> et al.</summary>
    public abstract NodeType NodeType { get; }

    /// <summary>Node's uppercase tag name for elements, <c>#text</c> for
    /// text nodes, <c>#comment</c> for comments, <c>#document</c> for the
    /// document. Matches the script-visible <c>nodeName</c>.</summary>
    public abstract string NodeName { get; }

    /// <summary>The <see cref="Document"/> this node belongs to, or
    /// <c>null</c> if the node has not been adopted yet. Set during
    /// <see cref="Document.Adopt"/>.</summary>
    public Document? OwnerDocument { get; internal set; }

    public Node? ParentNode { get; private set; }

    public Node? PreviousSibling { get; private set; }

    public Node? NextSibling { get; private set; }

    private readonly List<Node> _children = [];

    public IReadOnlyList<Node> ChildNodes => _children;

    public Node? FirstChild => _children.Count == 0 ? null : _children[0];

    public Node? LastChild => _children.Count == 0 ? null : _children[^1];

    public bool HasChildNodes => _children.Count > 0;

    /// <summary>
    /// Concatenation of all descendant text. Elements and documents
    /// walk the tree; text and comment nodes return their data.
    /// </summary>
    public virtual string TextContent
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            CollectTextContent(this, sb);
            return sb.ToString();
        }
    }

    private static void CollectTextContent(Node node, System.Text.StringBuilder sb)
    {
        foreach (var child in node._children)
        {
            if (child is Text t) sb.Append(t.Data);
            else if (child is Comment) { /* comments are not part of textContent */ }
            else CollectTextContent(child, sb);
        }
    }

    /// <summary>
    /// Append <paramref name="child"/> as the last child of this node.
    /// If <paramref name="child"/> is already in a tree, it is first
    /// removed from its current parent (matching DOM semantics).
    /// </summary>
    /// <returns>The appended child (for call-chaining).</returns>
    public Node AppendChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ValidateNotAncestor(child);

        child.ParentNode?.RemoveChild(child);

        // Wire up sibling pointers.
        if (_children.Count > 0)
        {
            var last = _children[^1];
            last.NextSibling = child;
            child.PreviousSibling = last;
        }
        child.NextSibling = null;

        _children.Add(child);
        child.ParentNode = this;

        // Adopt into this node's owner document if it isn't already.
        var owner = OwnerDocument ?? this as Document;
        if (owner is not null) AdoptSubtree(child, owner);

        return child;
    }

    /// <summary>
    /// Insert <paramref name="newChild"/> immediately before
    /// <paramref name="referenceChild"/>. If <paramref name="referenceChild"/>
    /// is <c>null</c>, behaves like <see cref="AppendChild"/>.
    /// </summary>
    public Node InsertBefore(Node newChild, Node? referenceChild)
    {
        ArgumentNullException.ThrowIfNull(newChild);
        if (referenceChild is null) return AppendChild(newChild);
        if (referenceChild.ParentNode != this)
            throw new InvalidOperationException("referenceChild is not a child of this node");

        ValidateNotAncestor(newChild);
        newChild.ParentNode?.RemoveChild(newChild);

        int idx = _children.IndexOf(referenceChild);
        _children.Insert(idx, newChild);
        newChild.ParentNode = this;

        // Sibling pointer fixup.
        var prev = referenceChild.PreviousSibling;
        newChild.PreviousSibling = prev;
        newChild.NextSibling = referenceChild;
        referenceChild.PreviousSibling = newChild;
        if (prev is not null) prev.NextSibling = newChild;

        var owner = OwnerDocument ?? this as Document;
        if (owner is not null) AdoptSubtree(newChild, owner);

        return newChild;
    }

    /// <summary>
    /// Remove <paramref name="child"/> from this node's children.
    /// </summary>
    /// <returns>The removed child (for call-chaining).</returns>
    public Node RemoveChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.ParentNode != this)
            throw new InvalidOperationException("child is not a child of this node");

        int idx = _children.IndexOf(child);
        _children.RemoveAt(idx);

        var prev = child.PreviousSibling;
        var next = child.NextSibling;
        if (prev is not null) prev.NextSibling = next;
        if (next is not null) next.PreviousSibling = prev;

        child.ParentNode = null;
        child.PreviousSibling = null;
        child.NextSibling = null;

        return child;
    }

    /// <summary>Return true if this node is an ancestor of
    /// <paramref name="other"/> (or is <paramref name="other"/> itself).</summary>
    public bool Contains(Node? other)
    {
        for (var cur = other; cur is not null; cur = cur.ParentNode)
        {
            if (ReferenceEquals(cur, this)) return true;
        }
        return false;
    }

    /// <summary>Walk this subtree in document order, yielding every
    /// descendant node.</summary>
    public IEnumerable<Node> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
        }
    }

    /// <summary>Walk this subtree yielding only the element descendants
    /// (not the starting node itself, unless it is also an element).</summary>
    public IEnumerable<Element> DescendantElements()
    {
        foreach (var node in DescendantsAndSelf())
        {
            if (node is Element e) yield return e;
        }
    }

    /// <summary>
    /// Return the first element descendant (in document order) that
    /// matches <paramref name="selector"/>, or <c>null</c> if none.
    /// </summary>
    public Element? QuerySelector(string selector)
    {
        var list = SelectorParser.Parse(selector);
        foreach (var descendant in DescendantsAndSelf())
        {
            if (descendant is Element e && !ReferenceEquals(e, this) &&
                SelectorMatcher.Matches(e, list))
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>
    /// Return every element descendant (in document order) that
    /// matches <paramref name="selector"/>.
    /// </summary>
    public IReadOnlyList<Element> QuerySelectorAll(string selector)
    {
        var list = SelectorParser.Parse(selector);
        var result = new List<Element>();
        foreach (var descendant in DescendantsAndSelf())
        {
            if (descendant is Element e && !ReferenceEquals(e, this) &&
                SelectorMatcher.Matches(e, list))
            {
                result.Add(e);
            }
        }
        return result;
    }

    private void ValidateNotAncestor(Node candidateChild)
    {
        // A node cannot be inserted into its own subtree — that would
        // create a cycle and explode every traversal.
        if (candidateChild.Contains(this))
        {
            throw new InvalidOperationException(
                "Refusing to insert a node into its own descendant (cycle).");
        }
    }

    private static void AdoptSubtree(Node root, Document owner)
    {
        root.OwnerDocument = owner;
        foreach (var child in root._children)
        {
            AdoptSubtree(child, owner);
        }
    }
}
