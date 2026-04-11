using Daisi.Broski.Engine.Dom;

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
            case "tagName":
            case "localName":
            case "attributes":
            case "children":
            case "classList":
            case "childElementCount":
            case "firstElementChild":
                // Read-only accessors.
                return;
        }
        base.Set(key, value);
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
}
