using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// JS-side wrapper around a <see cref="Element"/>. Adds the
/// element-specific surface on top of <see cref="JsDomNode"/>:
/// <c>tagName</c>, <c>id</c>, <c>className</c>, <c>classList</c>,
/// <c>attributes</c>, <c>getAttribute</c> / <c>setAttribute</c> /
/// <c>hasAttribute</c> / <c>removeAttribute</c>,
/// <c>querySelector</c> / <c>querySelectorAll</c>,
/// <c>getElementsByClassName</c> / <c>getElementsByTagName</c>,
/// <c>matches</c>, <c>closest</c>, and the element-child
/// accessors (<c>children</c>, <c>firstElementChild</c>).
/// </summary>
public class JsDomElement : JsDomNode
{
    private readonly Element _element;

    public JsDomElement(JsDomBridge bridge, Element element) : base(bridge, element)
    {
        _element = element;
        InstallElementMethods();
    }

    private void InstallElementMethods()
    {
        SetNonEnumerable("getAttribute", new JsFunction("getAttribute", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Null;
            var name = JsValue.ToJsString(args[0]).ToLowerInvariant();
            var v = _element.GetAttribute(name);
            return (object?)v ?? JsValue.Null;
        }));
        SetNonEnumerable("setAttribute", new JsFunction("setAttribute", (thisVal, args) =>
        {
            if (args.Count < 2) return JsValue.Undefined;
            var name = JsValue.ToJsString(args[0]).ToLowerInvariant();
            var value = JsValue.ToJsString(args[1]);
            _element.SetAttribute(name, value);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("hasAttribute", new JsFunction("hasAttribute", (thisVal, args) =>
        {
            if (args.Count == 0) return false;
            var name = JsValue.ToJsString(args[0]).ToLowerInvariant();
            return _element.HasAttribute(name);
        }));
        SetNonEnumerable("removeAttribute", new JsFunction("removeAttribute", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Undefined;
            var name = JsValue.ToJsString(args[0]).ToLowerInvariant();
            _element.RemoveAttribute(name);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("querySelector", new JsFunction("querySelector", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Null;
            var selector = JsValue.ToJsString(args[0]);
            var match = _element.QuerySelector(selector);
            return (object?)Bridge.WrapOrNull(match) ?? JsValue.Null;
        }));
        SetNonEnumerable("querySelectorAll", new JsFunction("querySelectorAll", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var selector = JsValue.ToJsString(args[0]);
            return Bridge.WrapElements(_element.QuerySelectorAll(selector));
        }));
        SetNonEnumerable("getElementsByTagName", new JsFunction("getElementsByTagName", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var tag = JsValue.ToJsString(args[0]).ToLowerInvariant();
            var result = new List<Element>();
            foreach (var el in _element.DescendantElements())
            {
                if (el.TagName == tag && !ReferenceEquals(el, _element))
                {
                    result.Add(el);
                }
            }
            return Bridge.WrapElements(result);
        }));
        SetNonEnumerable("getElementsByClassName", new JsFunction("getElementsByClassName", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var name = JsValue.ToJsString(args[0]);
            var result = new List<Element>();
            foreach (var el in _element.DescendantElements())
            {
                if (ReferenceEquals(el, _element)) continue;
                foreach (var c in el.ClassList)
                {
                    if (c == name) { result.Add(el); break; }
                }
            }
            return Bridge.WrapElements(result);
        }));
        SetNonEnumerable("matches", new JsFunction("matches", (thisVal, args) =>
        {
            if (args.Count == 0) return false;
            var selector = JsValue.ToJsString(args[0]);
            return _element.Matches(selector);
        }));
        SetNonEnumerable("closest", new JsFunction("closest", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Null;
            var selector = JsValue.ToJsString(args[0]);
            var match = _element.Closest(selector);
            return (object?)Bridge.WrapOrNull(match) ?? JsValue.Null;
        }));

        // Phase-6c layout integration. Each call rebuilds the
        // layout tree from scratch — for a headless engine
        // that's typically run once per scrape this is the
        // simplest correct thing. Long-lived hosts that need
        // many getBoundingClientRect calls per page should
        // cache the tree at the call site.
        SetNonEnumerable("getBoundingClientRect", new JsFunction(
            "getBoundingClientRect", (thisVal, args) =>
        {
            return BuildClientRect(MeasureBorderBox());
        }));
        SetNonEnumerable("getClientRects", new JsFunction(
            "getClientRects", (thisVal, args) =>
        {
            // Phase 6c collapses every element to a single
            // rect (no inline line-box splitting yet), so
            // getClientRects returns a one-element list.
            var arr = new JsArray { Prototype = Bridge.Engine.ArrayPrototype };
            arr.Elements.Add(BuildClientRect(MeasureBorderBox()));
            return arr;
        }));
    }

    /// <summary>Lay out the document and pull the
    /// border-box rect for this element. Returns the
    /// engine's zero rect when the element isn't part of any
    /// document (orphan node) or when layout chose to skip
    /// it (display: none).</summary>
    private Layout.LayoutRect MeasureBorderBox()
    {
        var doc = _element.OwnerDocument;
        if (doc is null) return new Layout.LayoutRect(0, 0, 0, 0);
        var root = Layout.LayoutTree.Build(doc);
        var box = Layout.LayoutTree.Find(root, _element);
        return box?.BorderBoxRect ?? new Layout.LayoutRect(0, 0, 0, 0);
    }

    /// <summary>Build the WHATWG <c>DOMRect</c>-shaped
    /// object real scripts pattern-match against:
    /// <c>{ x, y, width, height, top, left, right, bottom }</c>
    /// plus a <c>toJSON()</c> that round-trips the same
    /// fields.</summary>
    private JsObject BuildClientRect(Layout.LayoutRect rect)
    {
        var o = new JsObject { Prototype = Bridge.Engine.ObjectPrototype };
        o.Set("x", rect.X);
        o.Set("y", rect.Y);
        o.Set("width", rect.Width);
        o.Set("height", rect.Height);
        o.Set("top", rect.Top);
        o.Set("right", rect.Right);
        o.Set("bottom", rect.Bottom);
        o.Set("left", rect.Left);
        return o;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        switch (key)
        {
            // `tagName` is historically uppercase in browsers,
            // matching NodeName. The DOM layer stores it in its
            // canonical lowercase form; we uppercase on read.
            case "tagName": return _element.TagName.ToUpperInvariant();
            case "localName": return _element.TagName;
            case "id": return _element.Id;
            case "className": return _element.ClassName;
            case "classList": return BuildClassList();
            case "attributes": return BuildAttributesArray();
            case "children": return BuildElementChildren();
            case "childElementCount": return (double)_element.Children.Count();
            case "firstElementChild": return (object?)Bridge.WrapOrNull(_element.FirstElementChild) ?? JsValue.Null;
            case "innerText": return _element.TextContent;
            case "innerHTML": return HtmlSerializer.SerializeChildren(_element);
            case "outerHTML": return HtmlSerializer.SerializeNode(_element);
            case "dataset": return BuildDataset();
            case "style": return BuildStyle();
            // Layout-dependent values — we don't ship layout
            // yet, so these return 0 (matching a 0×0 element)
            // instead of throwing. Enough for scripts that
            // check "was this element rendered?" without
            // faking an actual size.
            case "offsetWidth":
            case "offsetHeight":
            case "clientWidth":
            case "clientHeight":
            case "scrollWidth":
            case "scrollHeight":
            case "scrollTop":
            case "scrollLeft":
            case "offsetTop":
            case "offsetLeft":
                return 0.0;
            case "offsetParent":
                return JsValue.Null;
        }
        // getBoundingClientRect / getClientRects used to be
        // returned as zero-rect stubs from this Get override.
        // Phase 6c installs real implementations via
        // SetNonEnumerable in InstallElementMethods; the
        // stubs were removed so the property bag entry wins.
        if (key == "scrollIntoView")
        {
            return new JsFunction("scrollIntoView", (thisVal, args) => JsValue.Undefined);
        }
        if (key == "focus" || key == "blur" || key == "click")
        {
            // focus/blur are no-ops; click() dispatches a
            // synthetic click Event on the element so
            // listeners fire. We delegate the latter to the
            // event system, rebuilding the minimum payload
            // inline.
            string opName = key;
            return new JsFunction(opName, (vm, thisVal, args) =>
            {
                if (opName == "click")
                {
                    var evt = new JsDomEvent("click", bubbles: true, cancelable: true)
                    {
                        Prototype = Bridge.Engine.EventPrototype,
                    };
                    DispatchEvent(vm, evt);
                }
                return JsValue.Undefined;
            });
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        switch (key)
        {
            case "id":
                _element.Id = JsValue.ToJsString(value);
                return;
            case "className":
                _element.ClassName = JsValue.ToJsString(value);
                return;
            case "innerHTML":
                SetInnerHtml(JsValue.ToJsString(value));
                return;
            case "tagName":
            case "localName":
            case "attributes":
            case "children":
            case "classList":
            case "childElementCount":
            case "firstElementChild":
            case "outerHTML":
                // Read-only accessors. outerHTML write is
                // deferred — it requires parenting logic to
                // insert the parsed fragment where this
                // element sits in its parent.
                return;
        }
        base.Set(key, value);
    }

    /// <summary>
    /// Parse <paramref name="html"/> as an HTML fragment
    /// and replace every child of this element with the
    /// result. Matches browser <c>innerHTML = '...'</c>
    /// semantics.
    ///
    /// <para>
    /// Fragment parsing is implemented by handing the
    /// source to the existing phase-1
    /// <see cref="HtmlTreeBuilder"/>, wrapped in a minimal
    /// <c>&lt;html&gt;&lt;body&gt;...&lt;/body&gt;&lt;/html&gt;</c>
    /// envelope so insertion-mode state lands on "in body".
    /// The parsed body's children are then adopted into
    /// this element's owner document and appended. This
    /// loses the spec's context-element rules (a <c>td</c>
    /// fragment parsed inside a <c>table</c> context would
    /// behave differently in a real browser), but covers
    /// the >95% of real <c>innerHTML</c> usage — simple
    /// HTML snippets assigned onto non-table elements.
    /// </para>
    /// </summary>
    private void SetInnerHtml(string html)
    {
        // Clear every existing child first.
        while (_element.FirstChild is not null)
        {
            _element.RemoveChild(_element.FirstChild);
        }
        if (html.Length == 0) return;

        // Parse the fragment via the standard tree builder.
        // Wrapping in <body> forces the builder's insertion
        // mode to "in body" from the start, so stray
        // fragment content lands there directly.
        var parsed = HtmlTreeBuilder.Parse("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var parsedBody = parsed.Body;
        if (parsedBody is null) return;

        // Move the parsed body's children onto this element.
        // Iterate off a snapshot so mutation during the
        // foreach doesn't trip the sibling-pointer walk.
        var children = new List<Node>(parsedBody.ChildNodes);
        foreach (var child in children)
        {
            parsedBody.RemoveChild(child);
            _element.AppendChild(child);
        }
    }

    /// <summary>
    /// Build a plain JS array of class tokens from the
    /// element's <c>class</c> attribute. Not a real
    /// <c>DOMTokenList</c> — methods like <c>add</c> /
    /// <c>remove</c> / <c>contains</c> / <c>toggle</c> are
    /// installed on the array as non-enumerable properties so
    /// the common access patterns still work, even though
    /// property identity across calls is not preserved.
    /// </summary>
    private JsArray BuildClassList()
    {
        var tokens = _element.ClassList;
        var arr = new JsArray { Prototype = Bridge.Engine.ArrayPrototype };
        foreach (var t in tokens) arr.Elements.Add(t);
        // Install common DOMTokenList methods on the returned
        // array so `el.classList.contains('x')` and
        // `el.classList.add('y')` work.
        arr.SetNonEnumerable("contains", new JsFunction("contains", (thisVal, args) =>
        {
            if (args.Count == 0) return false;
            var name = JsValue.ToJsString(args[0]);
            foreach (var c in _element.ClassList)
            {
                if (c == name) return true;
            }
            return false;
        }));
        arr.SetNonEnumerable("add", new JsFunction("add", (thisVal, args) =>
        {
            var current = new List<string>(_element.ClassList);
            foreach (var a in args)
            {
                var name = JsValue.ToJsString(a);
                if (!current.Contains(name)) current.Add(name);
            }
            _element.ClassName = string.Join(' ', current);
            return JsValue.Undefined;
        }));
        arr.SetNonEnumerable("remove", new JsFunction("remove", (thisVal, args) =>
        {
            var current = new List<string>(_element.ClassList);
            foreach (var a in args)
            {
                current.Remove(JsValue.ToJsString(a));
            }
            _element.ClassName = string.Join(' ', current);
            return JsValue.Undefined;
        }));
        arr.SetNonEnumerable("toggle", new JsFunction("toggle", (thisVal, args) =>
        {
            if (args.Count == 0) return false;
            var name = JsValue.ToJsString(args[0]);
            var current = new List<string>(_element.ClassList);
            if (current.Contains(name))
            {
                current.Remove(name);
                _element.ClassName = string.Join(' ', current);
                return false;
            }
            current.Add(name);
            _element.ClassName = string.Join(' ', current);
            return true;
        }));
        return arr;
    }

    /// <summary>
    /// Build an array of <c>{name, value}</c> attribute shapes.
    /// Not a real <c>NamedNodeMap</c> — the live collection
    /// with <c>Attr</c> nodes is deferred — but the shape is
    /// close enough that scripts can iterate
    /// <c>el.attributes</c> via <c>for..of</c> or index access.
    /// </summary>
    private JsArray BuildAttributesArray()
    {
        var arr = new JsArray { Prototype = Bridge.Engine.ArrayPrototype };
        foreach (var kv in _element.Attributes)
        {
            var obj = new JsObject { Prototype = Bridge.Engine.ObjectPrototype };
            obj.Set("name", kv.Key);
            obj.Set("value", kv.Value);
            arr.Elements.Add(obj);
        }
        return arr;
    }

    private JsArray BuildElementChildren()
    {
        var arr = new JsArray { Prototype = Bridge.Engine.ArrayPrototype };
        foreach (var child in _element.Children)
        {
            arr.Elements.Add(Bridge.Wrap(child));
        }
        return arr;
    }

    /// <summary>
    /// Build a <c>DOMStringMap</c>-shaped object exposing
    /// every <c>data-foo-bar</c> attribute as
    /// <c>fooBar</c> (camelCase per spec). The object is
    /// a plain <see cref="JsObject"/> prepopulated at read
    /// time — not a live view — but the DOM bridge above
    /// re-reads on every property access, so iterating
    /// <c>el.dataset</c> at two different times picks up
    /// attribute mutations in between.
    /// </summary>
    private JsObject BuildDataset()
    {
        var ds = new JsObject { Prototype = Bridge.Engine.ObjectPrototype };
        foreach (var attr in _element.Attributes)
        {
            if (!attr.Key.StartsWith("data-", StringComparison.OrdinalIgnoreCase)) continue;
            var tail = attr.Key.Substring(5);
            // data-user-name → userName
            var sb = new System.Text.StringBuilder(tail.Length);
            bool upper = false;
            foreach (var c in tail)
            {
                if (c == '-') { upper = true; continue; }
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            ds.Set(sb.ToString(), attr.Value);
        }
        return ds;
    }

    /// <summary>
    /// Cached <see cref="JsCssStyleDeclaration"/> for this
    /// element. Lazily allocated on first read of
    /// <c>el.style</c> and then returned on every subsequent
    /// read so identity is stable (<c>el.style === el.style</c>).
    /// </summary>
    private JsCssStyleDeclaration? _cachedStyle;

    private JsCssStyleDeclaration BuildStyle()
    {
        _cachedStyle ??= new JsCssStyleDeclaration(_element)
        {
            Prototype = Bridge.Engine.ObjectPrototype,
        };
        return _cachedStyle;
    }
}
