using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// JS-side wrapper around a <see cref="Document"/>. A document
/// is a <see cref="Node"/> (not an <see cref="Element"/> in our
/// DOM), so it extends <see cref="JsDomNode"/> directly but
/// installs the query + factory methods script code expects on
/// <c>document</c>: <c>createElement</c>, <c>createTextNode</c>,
/// <c>createComment</c>, <c>getElementById</c>,
/// <c>getElementsByTagName</c>, <c>getElementsByClassName</c>,
/// <c>querySelector</c> / <c>querySelectorAll</c>.
/// </summary>
public sealed class JsDomDocument : JsDomNode
{
    private readonly Document _document;

    public JsDomDocument(JsDomBridge bridge, Document document) : base(bridge, document)
    {
        _document = document;
        InstallDocumentMethods();
    }

    private void InstallDocumentMethods()
    {
        SetNonEnumerable("createElement", new JsFunction("createElement", (thisVal, args) =>
        {
            var name = args.Count > 0 ? JsValue.ToJsString(args[0]).ToLowerInvariant() : "div";
            return Bridge.Wrap(_document.CreateElement(name));
        }));
        SetNonEnumerable("createTextNode", new JsFunction("createTextNode", (thisVal, args) =>
        {
            var data = args.Count > 0 ? JsValue.ToJsString(args[0]) : "";
            return Bridge.Wrap(_document.CreateTextNode(data));
        }));
        SetNonEnumerable("createComment", new JsFunction("createComment", (thisVal, args) =>
        {
            var data = args.Count > 0 ? JsValue.ToJsString(args[0]) : "";
            return Bridge.Wrap(_document.CreateComment(data));
        }));
        SetNonEnumerable("getElementById", new JsFunction("getElementById", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Null;
            var id = JsValue.ToJsString(args[0]);
            var match = _document.GetElementById(id);
            return (object?)Bridge.WrapOrNull(match) ?? JsValue.Null;
        }));
        SetNonEnumerable("getElementsByTagName", new JsFunction("getElementsByTagName", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var tag = JsValue.ToJsString(args[0]).ToLowerInvariant();
            return Bridge.WrapElements(_document.GetElementsByTagName(tag));
        }));
        SetNonEnumerable("getElementsByClassName", new JsFunction("getElementsByClassName", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var name = JsValue.ToJsString(args[0]);
            return Bridge.WrapElements(_document.GetElementsByClassName(name));
        }));
        SetNonEnumerable("querySelector", new JsFunction("querySelector", (thisVal, args) =>
        {
            if (args.Count == 0) return JsValue.Null;
            var selector = JsValue.ToJsString(args[0]);
            var match = _document.QuerySelector(selector);
            return (object?)Bridge.WrapOrNull(match) ?? JsValue.Null;
        }));
        SetNonEnumerable("querySelectorAll", new JsFunction("querySelectorAll", (thisVal, args) =>
        {
            if (args.Count == 0) return Bridge.EmptyArray();
            var selector = JsValue.ToJsString(args[0]);
            return Bridge.WrapElements(_document.QuerySelectorAll(selector));
        }));
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        switch (key)
        {
            case "documentElement":
                return (object?)Bridge.WrapOrNull(_document.DocumentElement) ?? JsValue.Null;
            case "head":
                return (object?)Bridge.WrapOrNull(_document.Head) ?? JsValue.Null;
            case "body":
                return (object?)Bridge.WrapOrNull(_document.Body) ?? JsValue.Null;
            case "doctype":
                return (object?)Bridge.WrapOrNull(_document.Doctype) ?? JsValue.Null;
        }
        return base.Get(key);
    }
}
