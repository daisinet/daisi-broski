using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// JS-side wrapper around a <see cref="Text"/> node. Exposes
/// <c>data</c> / <c>length</c> / <c>nodeValue</c>, which are
/// the properties scripts actually read off text nodes in
/// practice. Writing <c>data</c> or <c>nodeValue</c> mutates
/// the underlying <see cref="Text.Data"/>.
/// </summary>
public sealed class JsDomText : JsDomNode
{
    private readonly Text _text;

    public JsDomText(JsDomBridge bridge, Text text) : base(bridge, text)
    {
        _text = text;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        switch (key)
        {
            case "data":
            case "nodeValue":
                return _text.Data;
            case "length":
                return (double)_text.Data.Length;
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        switch (key)
        {
            case "data":
            case "nodeValue":
                _text.Data = JsValue.ToJsString(value);
                return;
        }
        base.Set(key, value);
    }
}
