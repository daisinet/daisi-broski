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

    /// <summary>
    /// In-memory cookie string (set / read via
    /// <c>document.cookie</c>). Not per-origin — one jar
    /// per document — but good enough for scripts that
    /// read back what they just wrote.
    /// </summary>
    private string _cookieJar = "";

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
            case "readyState":
                // Phase-1 parses synchronously, so by the time
                // script runs the document is always fully
                // loaded. Scripts that gate behavior on
                // DOMContentLoaded can just check this.
                return "complete";
            case "cookie":
                return _cookieJar;
            case "URL":
            case "documentURI":
            case "baseURI":
                // Matches the location.href default — will
                // read through once navigation is wired.
                return "about:blank";
            case "referrer":
                return "";
            case "currentScript":
                // The host driver sets engine.ExecutingScript
                // before each script runs, so SSR bootstraps
                // that mount next to their own <script> tag can
                // find their parent via document.currentScript.
                var cur = Bridge.Engine.ExecutingScript;
                return cur is not null ? Bridge.Wrap(cur) : (object)JsValue.Null;
            case "contentType":
                return "text/html";
            case "characterSet":
            case "charset":
                return "UTF-8";
            case "compatMode":
                return "CSS1Compat";
            case "hidden":
                return false;
            case "visibilityState":
                return "visible";
            case "defaultView":
                // Return window if the engine has one; null
                // otherwise. Scripts commonly check this to
                // see if they're running in a full browser.
                if (Bridge.Engine.Globals.TryGetValue("window", out var w))
                {
                    return w;
                }
                return JsValue.Null;
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (key == "cookie")
        {
            // Per spec, setting document.cookie appends (not
            // replaces). Parse off the name=value up to the
            // first `;`, then merge into the existing jar
            // replacing any earlier same-name entry.
            AppendCookie(JsValue.ToJsString(value));
            return;
        }
        // title / documentElement / readyState / etc. are
        // read-only — silently ignore writes.
        if (key is "readyState" or "URL" or "documentURI" or
            "baseURI" or "referrer" or "currentScript" or
            "contentType" or "characterSet" or "charset" or
            "compatMode" or "hidden" or "visibilityState" or
            "defaultView" or "documentElement" or "head" or
            "body" or "doctype")
        {
            return;
        }
        base.Set(key, value);
    }

    /// <summary>
    /// Merge a new <c>name=value; attr=...</c> cookie into
    /// <see cref="_cookieJar"/>, replacing any earlier
    /// entry under the same name. The attribute suffix
    /// (path, expires, domain, ...) is stripped — the
    /// in-memory jar stores only the name=value pair.
    /// </summary>
    private void AppendCookie(string input)
    {
        // Extract the leading name=value (up to first ';').
        int semi = input.IndexOf(';');
        var pair = semi < 0 ? input.Trim() : input.Substring(0, semi).Trim();
        int eq = pair.IndexOf('=');
        if (eq < 0) return;
        var name = pair.Substring(0, eq);

        // Rebuild the jar without any existing entry for this name.
        var parts = _cookieJar.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length + 1);
        foreach (var p in parts)
        {
            var trimmed = p.Trim();
            int peq = trimmed.IndexOf('=');
            if (peq < 0) continue;
            var existingName = trimmed.Substring(0, peq);
            if (existingName == name) continue;
            kept.Add(trimmed);
        }
        kept.Add(pair);
        _cookieJar = string.Join("; ", kept);
    }
}
