using Daisi.Broski.Engine.Css;

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

    /// <summary>Per-document mutation fan-out. Lazily allocated
    /// on first access, so documents that never have a
    /// MutationObserver attached pay zero cost. Mutations on any
    /// node in this document's tree route through this
    /// dispatcher when it exists; ignore otherwise.</summary>
    public MutationDispatcher MutationDispatcher
    {
        get
        {
            _mutationDispatcher ??= new MutationDispatcher();
            return _mutationDispatcher;
        }
    }
    private MutationDispatcher? _mutationDispatcher;

    /// <summary>True when at least one MutationObserver is
    /// registered against any node in this document. Mutation
    /// methods consult this before doing the (cheap) walk into
    /// the dispatcher so the no-observers case is just one
    /// boolean test.</summary>
    internal bool HasMutationObservers =>
        _mutationDispatcher is { RegistrationCount: > 0 };

    /// <summary>Parsed stylesheets attached to this document.
    /// Populated lazily by <see cref="StyleSheets"/> on first
    /// access — walks every <c>&lt;style&gt;</c> descendant
    /// once and caches the result. Calling
    /// <see cref="InvalidateStyleSheets"/> after a script-side
    /// mutation forces a re-parse on the next read.</summary>
    public IReadOnlyList<Stylesheet> StyleSheets
    {
        get
        {
            if (_styleSheets is null) RecomputeStyleSheets();
            return _styleSheets!;
        }
    }
    private IReadOnlyList<Stylesheet>? _styleSheets;

    /// <summary>Drop the cached <see cref="StyleSheets"/> so
    /// the next read re-parses every <c>&lt;style&gt;</c> in
    /// the tree. Cheap; the cache is per-document and the
    /// per-style-block parse is itself fast.</summary>
    public void InvalidateStyleSheets()
    {
        _styleSheets = null;
        _styleCache?.Clear();
    }

    /// <summary>Per-document memoization of resolved
    /// computed-style results, keyed by (element, viewport
    /// width, viewport height). The cascade is expensive —
    /// each Resolve walks every rule in every stylesheet
    /// and recursively resolves ancestors for inheritance —
    /// so without this cache a layout pass re-runs the
    /// cascade O(depth^2) times per element. Cleared on
    /// stylesheet invalidation.</summary>
    public Dictionary<(Element Element, int W, int H), object>? StyleCache
    {
        get => _styleCache;
        set => _styleCache = value;
    }
    private Dictionary<(Element, int, int), object>? _styleCache;

    /// <summary>Get-or-create the style cache. Lazy because
    /// engines that never run the cascade (parse-only
    /// callers) shouldn't pay the dictionary allocation.</summary>
    internal Dictionary<(Element, int, int), object> EnsureStyleCache() =>
        _styleCache ??= new Dictionary<(Element, int, int), object>();

    /// <summary>Decoded raster pixel buffers for every
    /// <c>&lt;img src&gt;</c> the page loader successfully
    /// fetched + decoded. Keyed by the <c>&lt;img&gt;</c>
    /// element so the painter can look up "the picture for
    /// this element" directly. Untouched <c>&lt;img&gt;</c>
    /// elements (broken URL, unsupported format) just don't
    /// have an entry — paint falls back to the placeholder
    /// rect.</summary>
    public Dictionary<Element, object>? Images
    {
        get => _images;
        set => _images = value;
    }
    private Dictionary<Element, object>? _images;

    public void AttachImage(Element imgElement, object decoded)
    {
        ArgumentNullException.ThrowIfNull(imgElement);
        ArgumentNullException.ThrowIfNull(decoded);
        _images ??= new Dictionary<Element, object>(ReferenceEqualityComparer.Instance);
        _images[imgElement] = decoded;
    }

    /// <summary>Stylesheets fetched ahead of time from
    /// <c>&lt;link rel="stylesheet"&gt;</c> by
    /// <c>PageLoader</c>. Inserted into <see cref="StyleSheets"/>
    /// at their corresponding source-order positions
    /// (interleaved with inline <c>&lt;style&gt;</c> blocks)
    /// so the cascade respects HTML document order. The
    /// keys are the <c>&lt;link&gt;</c> elements, so the
    /// insertion order preserves their tree order.</summary>
    private Dictionary<Element, Stylesheet>? _externalStylesheets;

    /// <summary>Attach a stylesheet that was fetched by the
    /// host (typically from a <c>&lt;link rel="stylesheet"&gt;</c>
    /// element) so the cascade picks it up alongside inline
    /// <c>&lt;style&gt;</c> blocks. The <paramref name="link"/>
    /// element identifies the source so source-order
    /// interleaving is exact. Calling this invalidates the
    /// cached stylesheet list.</summary>
    public void AttachExternalStylesheet(Element link, Stylesheet sheet)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(sheet);
        _externalStylesheets ??= new Dictionary<Element, Stylesheet>(
            ReferenceEqualityComparer.Instance);
        _externalStylesheets[link] = sheet;
        _styleSheets = null;
    }

    private void RecomputeStyleSheets()
    {
        var list = new List<Stylesheet>();
        foreach (var el in DescendantElements())
        {
            if (el.TagName == "style")
            {
                var css = el.TextContent;
                if (!string.IsNullOrEmpty(css))
                {
                    list.Add(CssParser.Parse(css));
                }
            }
            else if (el.TagName == "link" && _externalStylesheets is { } externals
                && externals.TryGetValue(el, out var external))
            {
                // Source-order interleaving: the <link> sits
                // wherever the parser placed it; we insert
                // its fetched sheet at the same point in the
                // list so author rules cascade in the same
                // order browsers see.
                list.Add(external);
            }
        }
        _styleSheets = list;
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
