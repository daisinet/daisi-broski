using Daisi.Broski.Engine.Dom.Selectors;

namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// An HTML element. Tag names are stored lowercase because the HTML
/// tokenizer lowercases them before handing tokens to the tree builder.
/// Attribute names are likewise stored lowercase.
///
/// Attribute storage is a <see cref="List{T}"/> rather than a dictionary
/// because:
///   - attribute iteration order must be preserved (observable to JS
///     via <c>element.attributes</c> in phase 3)
///   - element attribute counts are almost always small (&lt; 10), so
///     linear scans beat dictionary hashing
///   - duplicate-attribute rejection already happened in the tokenizer
/// </summary>
public class Element : Node
{
    public override NodeType NodeType => NodeType.Element;

    public override string NodeName => TagName.ToUpperInvariant();

    /// <summary>Lowercase tag name (e.g. <c>"div"</c>, <c>"a"</c>).</summary>
    public string TagName { get; }

    private readonly List<KeyValuePair<string, string>> _attributes = [];

    public IReadOnlyList<KeyValuePair<string, string>> Attributes => _attributes;

    internal Element(string tagName)
    {
        TagName = tagName;
    }

    /// <summary>Get an attribute value, or <c>null</c> if the attribute
    /// is not set.</summary>
    public string? GetAttribute(string name)
    {
        foreach (var a in _attributes)
        {
            if (a.Key == name) return a.Value;
        }
        return null;
    }

    /// <summary>Set an attribute. Replaces an existing attribute of the
    /// same name; otherwise appends.</summary>
    public void SetAttribute(string name, string value)
    {
        for (int i = 0; i < _attributes.Count; i++)
        {
            if (_attributes[i].Key == name)
            {
                _attributes[i] = new KeyValuePair<string, string>(name, value);
                return;
            }
        }
        _attributes.Add(new KeyValuePair<string, string>(name, value));
    }

    public bool HasAttribute(string name) => GetAttribute(name) is not null;

    public bool RemoveAttribute(string name)
    {
        for (int i = 0; i < _attributes.Count; i++)
        {
            if (_attributes[i].Key == name)
            {
                _attributes.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Convenience: the <c>id</c> attribute, or empty string
    /// if not set. Matches the script-visible <c>element.id</c>.</summary>
    public string Id
    {
        get => GetAttribute("id") ?? string.Empty;
        set => SetAttribute("id", value);
    }

    /// <summary>Convenience: the <c>class</c> attribute, or empty string
    /// if not set. Matches the script-visible <c>element.className</c>.</summary>
    public string ClassName
    {
        get => GetAttribute("class") ?? string.Empty;
        set => SetAttribute("class", value);
    }

    /// <summary>Whitespace-split class tokens. Empty if there's no
    /// <c>class</c> attribute. Produces a fresh array each call —
    /// callers should cache the result if they iterate repeatedly.</summary>
    public string[] ClassList
    {
        get
        {
            var cls = GetAttribute("class");
            if (string.IsNullOrEmpty(cls)) return [];
            return cls.Split(
                [' ', '\t', '\n', '\r', '\f'],
                StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <summary>Element-only child collection. Cheaper than filtering
    /// <see cref="Node.ChildNodes"/> at every caller site.</summary>
    public IEnumerable<Element> Children
    {
        get
        {
            foreach (var n in ChildNodes)
            {
                if (n is Element e) yield return e;
            }
        }
    }

    public Element? FirstElementChild
    {
        get
        {
            foreach (var n in ChildNodes)
            {
                if (n is Element e) return e;
            }
            return null;
        }
    }

    /// <summary>
    /// Return <c>true</c> if this element would be matched by
    /// <paramref name="selector"/>.
    /// </summary>
    public bool Matches(string selector)
    {
        var list = SelectorParser.Parse(selector);
        return SelectorMatcher.Matches(this, list);
    }

    /// <summary>
    /// Return the innermost ancestor (including this element) that
    /// matches <paramref name="selector"/>, or <c>null</c> if none.
    /// </summary>
    public Element? Closest(string selector)
    {
        var list = SelectorParser.Parse(selector);
        for (Node? cur = this; cur is not null; cur = cur.ParentNode)
        {
            if (cur is Element e && SelectorMatcher.Matches(e, list))
                return e;
        }
        return null;
    }
}
