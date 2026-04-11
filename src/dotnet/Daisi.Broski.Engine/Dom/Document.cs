namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// The root of a parsed DOM tree. Produced by the HTML tree builder.
///
/// Factory methods (<see cref="CreateElement"/>, <see cref="CreateTextNode"/>,
/// <see cref="CreateComment"/>) are the *only* way new nodes enter the
/// tree — constructors on the node types themselves are internal for
/// exactly this reason. That way every node's <see cref="Node.OwnerDocument"/>
/// is correctly set at birth and we don't rely on callers remembering
/// to adopt.
/// </summary>
public sealed class Document : Node
{
    public override NodeType NodeType => NodeType.Document;

    public override string NodeName => "#document";

    public Document()
    {
        OwnerDocument = this;
    }

    /// <summary>
    /// The root element of the document (typically <c>&lt;html&gt;</c>).
    /// Returns <c>null</c> until the tree builder has inserted one.
    /// </summary>
    public Element? DocumentElement
    {
        get
        {
            foreach (var child in ChildNodes)
            {
                if (child is Element e) return e;
            }
            return null;
        }
    }

    /// <summary>The <c>&lt;head&gt;</c> child of the document element,
    /// or <c>null</c> if the tree builder hasn't reached it yet.</summary>
    public Element? Head => FindChild(DocumentElement, "head");

    /// <summary>The <c>&lt;body&gt;</c> child of the document element,
    /// or <c>null</c>.</summary>
    public Element? Body => FindChild(DocumentElement, "body");

    /// <summary>The <see cref="DocumentType"/> child if one was parsed;
    /// otherwise <c>null</c>.</summary>
    public DocumentType? Doctype
    {
        get
        {
            foreach (var child in ChildNodes)
            {
                if (child is DocumentType dt) return dt;
            }
            return null;
        }
    }

    public Element CreateElement(string tagName)
    {
        var e = new Element(tagName);
        e.OwnerDocument = this;
        return e;
    }

    public Text CreateTextNode(string data)
    {
        var t = new Text(data);
        t.OwnerDocument = this;
        return t;
    }

    public Comment CreateComment(string data)
    {
        var c = new Comment(data);
        c.OwnerDocument = this;
        return c;
    }

    public DocumentType CreateDocumentType(string name)
    {
        var dt = new DocumentType(name);
        dt.OwnerDocument = this;
        return dt;
    }

    /// <summary>
    /// Look up an element by its <c>id</c> attribute. Linear walk in
    /// phase 1 — phase 2 adds an id index maintained on mutation if the
    /// linear cost becomes visible.
    /// </summary>
    public Element? GetElementById(string id)
    {
        foreach (var el in DescendantElements())
        {
            if (el.GetAttribute("id") == id) return el;
        }
        return null;
    }

    /// <summary>All elements with the given tag name (lowercased).</summary>
    public IEnumerable<Element> GetElementsByTagName(string tagName)
    {
        foreach (var el in DescendantElements())
        {
            if (el.TagName == tagName) yield return el;
        }
    }

    /// <summary>All elements that have <paramref name="className"/>
    /// in their whitespace-separated <c>class</c> attribute.</summary>
    public IEnumerable<Element> GetElementsByClassName(string className)
    {
        foreach (var el in DescendantElements())
        {
            foreach (var c in el.ClassList)
            {
                if (c == className)
                {
                    yield return el;
                    break;
                }
            }
        }
    }

    private static Element? FindChild(Element? parent, string tagName)
    {
        if (parent is null) return null;
        foreach (var n in parent.ChildNodes)
        {
            if (n is Element e && e.TagName == tagName) return e;
        }
        return null;
    }
}
