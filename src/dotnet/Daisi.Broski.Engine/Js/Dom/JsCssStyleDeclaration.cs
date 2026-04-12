using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// Script-visible <c>CSSStyleDeclaration</c> — the object
/// returned by <c>element.style</c>. Stores inline style
/// properties as a flat dictionary keyed by both the camelCase
/// JS form (<c>backgroundColor</c>) and the kebab-case CSS form
/// (<c>background-color</c>). Reads from the dictionary; writes
/// go to the dictionary AND update the backing
/// <see cref="Element"/>'s <c>style</c> attribute so the
/// serializer round-trips correctly.
///
/// <para>
/// This is NOT a computed-style resolver. <c>el.style.X</c>
/// returns only what was set via script or via the inline
/// <c>style=""</c> attribute — not what the cascade / inherited
/// styles would produce. <c>getComputedStyle</c> is wired
/// separately as a thin read-only wrapper.
/// </para>
/// </summary>
public sealed class JsCssStyleDeclaration : JsObject
{
    private readonly Element _element;
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);

    public JsCssStyleDeclaration(Element element)
    {
        _element = element;
        ParseFromAttribute();

        // Install the spec methods as non-enumerable properties.
        SetNonEnumerable("setProperty", new JsFunction("setProperty", (thisVal, args) =>
        {
            if (args.Count < 2) return JsValue.Undefined;
            var prop = JsValue.ToJsString(args[0]);
            var val = JsValue.ToJsString(args[1]);
            SetStyleProperty(prop, val);
            return JsValue.Undefined;
        }));
        SetNonEnumerable("getPropertyValue", new JsFunction("getPropertyValue", (thisVal, args) =>
        {
            if (args.Count == 0) return "";
            var prop = NormalizePropertyName(JsValue.ToJsString(args[0]));
            return _props.TryGetValue(prop, out var v) ? v : "";
        }));
        SetNonEnumerable("removeProperty", new JsFunction("removeProperty", (thisVal, args) =>
        {
            if (args.Count == 0) return "";
            var prop = NormalizePropertyName(JsValue.ToJsString(args[0]));
            if (_props.TryGetValue(prop, out var old))
            {
                _props.Remove(prop);
                SyncToAttribute();
                return old;
            }
            return "";
        }));
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "cssText") return BuildCssText();
        if (key == "length") return (double)_props.Count;
        // Check installed methods first (setProperty etc).
        if (Properties.ContainsKey(key)) return base.Get(key);
        // camelCase or kebab-case property read.
        var normalized = NormalizePropertyName(key);
        if (_props.TryGetValue(normalized, out var v)) return v;
        // Return empty string for unknown CSS properties —
        // matching browser behavior where el.style.X on an
        // unset property is "", not undefined.
        if (IsCssPropertyName(key)) return "";
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (key == "cssText")
        {
            _props.Clear();
            ParseCssText(JsValue.ToJsString(value));
            SyncToAttribute();
            return;
        }
        // If the key looks like a CSS property name (camelCase
        // or contains a hyphen), route it to the style bag.
        var normalized = NormalizePropertyName(key);
        if (IsCssPropertyName(key) || _props.ContainsKey(normalized))
        {
            SetStyleProperty(key, JsValue.ToJsString(value));
            return;
        }
        base.Set(key, value);
    }

    private void SetStyleProperty(string name, string value)
    {
        var normalized = NormalizePropertyName(name);
        if (string.IsNullOrEmpty(value))
        {
            _props.Remove(normalized);
        }
        else
        {
            _props[normalized] = value;
        }
        SyncToAttribute();
    }

    /// <summary>
    /// Parse the element's current <c>style</c> attribute
    /// into the property bag. Called at construction time
    /// and whenever <c>cssText</c> is set.
    /// </summary>
    private void ParseFromAttribute()
    {
        var attr = _element.GetAttribute("style");
        if (string.IsNullOrWhiteSpace(attr)) return;
        ParseCssText(attr);
    }

    private void ParseCssText(string css)
    {
        foreach (var decl in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = decl.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var prop = trimmed.Substring(0, colon).Trim();
            var val = trimmed.Substring(colon + 1).Trim();
            // Strip !important — we don't cascade, so we
            // just store the value.
            if (val.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            {
                val = val.Substring(0, val.Length - 10).Trim();
            }
            _props[prop] = val;
        }
    }

    /// <summary>
    /// Write the property bag back to the element's
    /// <c>style</c> attribute so the HTML serializer
    /// round-trips inline styles correctly.
    /// </summary>
    private void SyncToAttribute()
    {
        if (_props.Count == 0)
        {
            _element.RemoveAttribute("style");
            return;
        }
        _element.SetAttribute("style", BuildCssText());
    }

    private string BuildCssText()
    {
        if (_props.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var kv in _props)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(kv.Key).Append(": ").Append(kv.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalize a CSS property name to its kebab-case
    /// canonical form. JS camelCase names are converted:
    /// <c>backgroundColor</c> → <c>background-color</c>.
    /// Already-kebab or single-word names pass through.
    /// </summary>
    private static string NormalizePropertyName(string name)
    {
        // Fast path: if it contains a hyphen it's already
        // in CSS form.
        if (name.Contains('-')) return name.ToLowerInvariant();
        // camelCase → kebab-case: insert '-' before each
        // uppercase letter and lowercase the whole thing.
        var sb = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Heuristic: does this key look like a CSS property
    /// name (camelCase with a lowercase start, or contains
    /// a hyphen)? Used so <c>el.style.display = 'none'</c>
    /// routes to the style bag instead of the JsObject
    /// property bag.
    /// </summary>
    private static bool IsCssPropertyName(string key)
    {
        if (key.Length == 0) return false;
        if (key.Contains('-')) return true;
        // Common JS-side CSS properties start lowercase and
        // contain at least one uppercase letter (the camelCase
        // hump). Single-word properties like "display",
        // "color", "width", "height", "margin", "padding",
        // "position", "overflow", "opacity", "visibility",
        // "cursor", "float", "clear", "content" are also
        // CSS properties — hardcode the common ones.
        return key is "display" or "color" or "width" or "height"
            or "margin" or "padding" or "position" or "overflow"
            or "opacity" or "visibility" or "cursor" or "float"
            or "clear" or "content" or "border" or "outline"
            or "background" or "font" or "top" or "left"
            or "right" or "bottom" or "transform" or "transition"
            or "animation" or "flex" or "grid" or "gap"
            or "order" or "resize" or "appearance"
            || (key.Length > 1 && char.IsLower(key[0]) &&
                key.Any(char.IsUpper));
    }
}
