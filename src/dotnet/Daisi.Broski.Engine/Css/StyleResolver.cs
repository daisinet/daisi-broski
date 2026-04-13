using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Dom.Selectors;

namespace Daisi.Broski.Engine.Css;

/// <summary>
/// Phase-6b cascade engine. Given a <see cref="Document"/>'s
/// stylesheets and a target <see cref="Element"/>, computes
/// the cascaded property values (specificity sort,
/// <c>!important</c> precedence, source order) plus
/// inheritance for the spec-defined inheritable properties.
/// Inline <c>style=""</c> attributes layer on top per spec
/// (treated as a higher-than-author-rules origin).
///
/// <para>
/// Returns a flat <see cref="ComputedStyle"/> dictionary of
/// kebab-case property → string value. Values aren't parsed
/// into typed units yet — that's a layout (slice 6c)
/// concern. Today's consumer is
/// <c>getComputedStyle().getPropertyValue('color')</c>; the
/// raw string is what real scripts read back.
/// </para>
///
/// <para>
/// Deliberately deferred:
/// <list type="bullet">
/// <item><c>var()</c> resolution — values containing
///   <c>var(--name)</c> pass through as-is.</item>
/// <item>Shorthand expansion (<c>margin: 1px 2px</c> →
///   <c>margin-top</c> / etc.) — the cascade keeps
///   shorthand declarations as declared; layout will
///   expand on demand.</item>
/// <item>Pseudo-element styling (<c>::before</c>,
///   <c>::after</c>) — selectors that target them parse
///   into the AST but don't match against any element
///   today.</item>
/// <item>User-agent default stylesheet — the cascade
///   only applies author rules (and inline). Layout
///   will need defaults; we'll layer them in 6c.</item>
/// </list>
/// </para>
/// </summary>
public static class StyleResolver
{
    /// <summary>Compute the full set of declarations that
    /// apply to <paramref name="element"/> after the cascade
    /// runs. The returned table includes:
    /// <list type="bullet">
    /// <item>Author declarations from
    ///   <see cref="Document.StyleSheets"/> whose selectors
    ///   match, sorted per CSS Cascade Level 4.</item>
    /// <item>Inline <c>style=""</c> declarations on the
    ///   element (a higher-precedence layer).</item>
    /// <item>Inherited values from the nearest ancestor that
    ///   sets an inheritable property — applied after the
    ///   element's own cascade so the element's own value
    ///   wins when both exist.</item>
    /// </list>
    /// Pass <paramref name="viewport"/> to enable
    /// <c>@media</c> filtering; null defaults to
    /// 1280×720 — matching the engine's
    /// <c>window.innerWidth</c> shim.</summary>
    public static ComputedStyle Resolve(Element element, Viewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var doc = element.OwnerDocument;
        if (doc is null)
        {
            return ComputedStyle.Empty;
        }
        viewport ??= Viewport.Default;

        // Per-document cache: each element resolves once per
        // (element, viewport) pair within a layout/render
        // pass. Without this cache the recursive ancestor
        // resolves below blow up to O(depth^2) cascade
        // walks per leaf.
        var cache = doc.EnsureStyleCache();
        var key = (element, viewport.Width, viewport.Height);
        if (cache.TryGetValue(key, out var existing) && existing is ComputedStyle hit)
        {
            return hit;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1) Author rules — collect every (declaration,
        // specificity, source-order, important) tuple that
        // matches, then sort and apply.
        var matches = new List<MatchEntry>();
        int sourceOrder = 0;
        foreach (var sheet in doc.StyleSheets)
        {
            CollectMatches(element, sheet, viewport, matches, ref sourceOrder);
        }
        ApplyCascade(matches, values);

        // 2) Inline style — overrides author rules at the
        // same importance level.
        ApplyInlineStyle(element, values);

        // 3) Inheritance — for each inheritable property the
        // element does NOT already set, walk up to the
        // nearest ancestor that does and adopt that value.
        ApplyInheritance(element, viewport, values);

        var result = new ComputedStyle(values);
        cache[key] = result;
        return result;
    }

    private static void CollectMatches(
        Element element, Stylesheet sheet, Viewport viewport,
        List<MatchEntry> matches, ref int sourceOrder)
    {
        foreach (var rule in sheet.Rules)
        {
            if (rule is StyleRule sr)
            {
                AddIfMatches(element, sr, matches, ref sourceOrder);
            }
            else if (rule is AtRule ar
                && ar.Name is "media" or "supports"
                && MediaQueryMatches(ar, viewport))
            {
                foreach (var nested in ar.Rules)
                {
                    if (nested is StyleRule nsr)
                    {
                        AddIfMatches(element, nsr, matches, ref sourceOrder);
                    }
                }
            }
        }
    }

    private static void AddIfMatches(
        Element element, StyleRule rule,
        List<MatchEntry> matches, ref int sourceOrder)
    {
        if (rule.Selectors is null) return;
        // A selector list matches if any of its complex
        // selectors match. Per CSS, the specificity used is
        // the most-specific matching selector.
        Specificity? bestSpec = null;
        foreach (var complex in rule.Selectors.Selectors)
        {
            if (SelectorMatcher.MatchesComplex(element, complex))
            {
                var spec = ComputeSpecificity(complex);
                if (bestSpec is null || spec.CompareTo(bestSpec.Value) > 0)
                {
                    bestSpec = spec;
                }
            }
        }
        if (bestSpec is null) return;
        int order = sourceOrder++;
        foreach (var decl in rule.Declarations)
        {
            matches.Add(new MatchEntry
            {
                Property = decl.Property,
                Value = decl.Value,
                Important = decl.Important,
                Specificity = bestSpec.Value,
                SourceOrder = order,
            });
        }
    }

    private static void ApplyCascade(List<MatchEntry> matches, Dictionary<string, string> into)
    {
        // CSS Cascade: not-important first (sorted by
        // specificity then source order, last-wins), then
        // !important (same sort) overrides on top.
        matches.Sort(static (a, b) =>
        {
            // Important loses to not-important here so we
            // apply normal first. Wait — we want normal then
            // important. Sort all entries together: normal
            // comes first, then important. So compare by
            // Important LAST: false < true.
            int impCmp = a.Important.CompareTo(b.Important);
            if (impCmp != 0) return impCmp;
            int specCmp = a.Specificity.CompareTo(b.Specificity);
            if (specCmp != 0) return specCmp;
            return a.SourceOrder.CompareTo(b.SourceOrder);
        });
        foreach (var m in matches)
        {
            into[m.Property] = m.Value;
        }
    }

    private static void ApplyInlineStyle(Element element, Dictionary<string, string> into)
    {
        var styleAttr = element.GetAttribute("style");
        if (string.IsNullOrEmpty(styleAttr)) return;
        var inline = CssParser.ParseDeclarationList(styleAttr);
        // Inline style sits in a higher origin than author
        // rules per spec; for non-important declarations it
        // beats anything at any author specificity. We model
        // this by applying inline last, so its values win.
        // Inline !important also beats author !important per
        // the spec — same "last write wins" rule applies.
        foreach (var d in inline)
        {
            into[d.Property] = d.Value;
        }
    }

    private static void ApplyInheritance(
        Element element, Viewport viewport,
        Dictionary<string, string> into)
    {
        // For each inheritable property, if the element
        // doesn't have an own value yet, walk up the parent
        // chain and adopt the first ancestor's value. Cache
        // ancestor cascades along the way to avoid O(N²)
        // for deep trees with many inheritable lookups.
        Dictionary<Element, ComputedStyle>? ancestorCache = null;

        foreach (var prop in InheritableProperties)
        {
            if (into.ContainsKey(prop)) continue;
            for (var anc = element.ParentNode as Element; anc is not null; anc = anc.ParentNode as Element)
            {
                ancestorCache ??= new Dictionary<Element, ComputedStyle>();
                if (!ancestorCache.TryGetValue(anc, out var ancStyle))
                {
                    ancStyle = Resolve(anc, viewport);
                    ancestorCache[anc] = ancStyle;
                }
                if (ancStyle.TryGet(prop, out var inheritedValue))
                {
                    into[prop] = inheritedValue;
                    break;
                }
            }
        }
    }

    /// <summary>Per CSS Selectors Level 4 §16. The triple
    /// (a, b, c) compares lexicographically — a id-selector
    /// always wins over any number of classes, etc.</summary>
    public static Specificity ComputeSpecificity(ComplexSelector selector)
    {
        int a = 0, b = 0, c = 0;
        foreach (var compound in selector.Compounds)
        {
            foreach (var simple in compound.Simples)
            {
                AddSimple(simple, ref a, ref b, ref c);
            }
        }
        return new Specificity(a, b, c);
    }

    private static void AddSimple(SimpleSelector simple, ref int a, ref int b, ref int c)
    {
        switch (simple)
        {
            case IdSelector: a++; break;
            case ClassSelector: b++; break;
            case AttributeSelector: b++; break;
            case PseudoClassSelector pc:
                if (pc.Kind == PseudoClassKind.Not && pc.Argument is { } notArg)
                {
                    // :not contributes the spec of its argument
                    // — most-specific selector inside it.
                    Specificity? best = null;
                    foreach (var inner in notArg.Selectors)
                    {
                        var s = ComputeSpecificity(inner);
                        if (best is null || s.CompareTo(best.Value) > 0) best = s;
                    }
                    if (best is { } bs)
                    {
                        a += bs.A; b += bs.B; c += bs.C;
                    }
                }
                else
                {
                    b++;
                }
                break;
            case TypeSelector ts when !ts.IsUniversal:
                c++;
                break;
        }
    }

    /// <summary>Evaluate the prelude of an <c>@media</c> /
    /// <c>@supports</c> at-rule against the given viewport.
    /// The matcher handles the four common shapes seen in
    /// real CSS: bare media types (<c>screen</c>,
    /// <c>print</c>, <c>all</c>), <c>(min-width: ...)</c>,
    /// <c>(max-width: ...)</c>, and combinations joined by
    /// <c>and</c>. Anything else (arbitrary <c>@supports</c>
    /// conditions, complex media features) is treated as
    /// matching, on the principle that emitting more rules
    /// is safer than dropping potentially relevant
    /// ones.</summary>
    private static bool MediaQueryMatches(AtRule rule, Viewport viewport)
    {
        if (rule.Name == "supports") return true; // permissive
        var prelude = rule.Prelude.ToLowerInvariant().Trim();
        if (prelude.Length == 0) return true;
        // Split on `,` to honor a comma-separated list — any
        // matching disjunct counts.
        foreach (var raw in prelude.Split(','))
        {
            if (MediaQueryListMatches(raw.Trim(), viewport)) return true;
        }
        return false;
    }

    private static bool MediaQueryListMatches(string query, Viewport viewport)
    {
        if (query.Length == 0) return true;
        bool negate = false;
        if (query.StartsWith("not ", StringComparison.Ordinal))
        {
            negate = true;
            query = query.Substring(4).Trim();
        }
        else if (query.StartsWith("only ", StringComparison.Ordinal))
        {
            query = query.Substring(5).Trim();
        }

        bool matches = true;
        foreach (var part in query.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!MediaQueryFeatureMatches(part.Trim(), viewport))
            {
                matches = false;
                break;
            }
        }
        return negate ? !matches : matches;
    }

    private static bool MediaQueryFeatureMatches(string part, Viewport viewport)
    {
        // Bare type token.
        if (!part.StartsWith('('))
        {
            return part is "all" or "screen";
        }
        // Feature: `(name)` or `(name: value)`.
        if (!part.EndsWith(')')) return true;
        var inner = part.Substring(1, part.Length - 2).Trim();
        var colon = inner.IndexOf(':');
        if (colon < 0)
        {
            // Bare feature name — treat as "exists, accept".
            return true;
        }
        var name = inner.Substring(0, colon).Trim();
        var value = inner.Substring(colon + 1).Trim();
        if (!TryParseLength(value, out int px))
        {
            return true; // unknown unit — permissive
        }
        return name switch
        {
            "min-width" => viewport.Width >= px,
            "max-width" => viewport.Width <= px,
            "min-height" => viewport.Height >= px,
            "max-height" => viewport.Height <= px,
            "width" => viewport.Width == px,
            "height" => viewport.Height == px,
            _ => true, // unknown feature — permissive
        };
    }

    private static bool TryParseLength(string value, out int px)
    {
        px = 0;
        // Accept "Npx", "N", "Nem" → assume 16px/em.
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]) && value[i] != '.' && value[i] != '-')
            {
                if (!double.TryParse(
                    value.AsSpan(0, i), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    return false;
                }
                var unit = value.Substring(i).Trim();
                px = unit switch
                {
                    "px" => (int)n,
                    "em" or "rem" => (int)(n * 16),
                    "pt" => (int)(n * 1.333),
                    "" => (int)n,
                    _ => 0,
                };
                return px != 0 || unit != "";
            }
        }
        if (double.TryParse(
            value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            px = (int)raw;
            return true;
        }
        return false;
    }

    /// <summary>The CSS-spec-defined inheritable properties
    /// that real cascade walks need. Not exhaustive — covers
    /// what real pages style on the body / containers and
    /// expect to flow down. Layout-affecting properties
    /// (margin, padding, width, position) are deliberately
    /// not inheritable per spec.</summary>
    private static readonly string[] InheritableProperties = new[]
    {
        "color", "font", "font-family", "font-size", "font-weight",
        "font-style", "font-variant", "line-height", "text-align",
        "text-indent", "text-transform", "white-space",
        "letter-spacing", "word-spacing", "direction", "visibility",
        "cursor", "list-style", "list-style-type",
        "list-style-position", "list-style-image",
        "border-collapse", "border-spacing", "caption-side",
        "empty-cells", "quotes", "tab-size",
    };

    private struct MatchEntry
    {
        public string Property;
        public string Value;
        public bool Important;
        public Specificity Specificity;
        public int SourceOrder;
    }
}

/// <summary>Selectors Level 4 specificity triple. Compared
/// lexicographically: id count first, then class+attr+pseudo,
/// then type. <c>(0, 1, 0)</c> beats <c>(0, 0, 100)</c>.</summary>
public readonly struct Specificity : IComparable<Specificity>
{
    public int A { get; }
    public int B { get; }
    public int C { get; }

    public Specificity(int a, int b, int c) { A = a; B = b; C = c; }

    public int CompareTo(Specificity other)
    {
        int x = A.CompareTo(other.A); if (x != 0) return x;
        x = B.CompareTo(other.B); if (x != 0) return x;
        return C.CompareTo(other.C);
    }

    public override string ToString() => $"{A},{B},{C}";
}

/// <summary>Viewport dimensions used for <c>@media</c>
/// matching. Default 1280×720 mirrors the engine's
/// <c>window.innerWidth</c> shim.</summary>
public sealed class Viewport
{
    public int Width { get; init; }
    public int Height { get; init; }
    public static Viewport Default { get; } = new() { Width = 1280, Height = 720 };
}

/// <summary>Result of a <see cref="StyleResolver.Resolve"/>
/// call. Read-only flat dictionary of kebab-case property →
/// string value, plus a missing-key default of the empty
/// string per WHATWG <c>getComputedStyle</c> behavior.</summary>
public sealed class ComputedStyle
{
    public static readonly ComputedStyle Empty = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _values;

    internal ComputedStyle(Dictionary<string, string> values)
    {
        _values = values;
    }

    public int Length => _values.Count;

    /// <summary>Get the computed value for
    /// <paramref name="property"/>, or the empty string when
    /// no rule (and no inherited rule) sets it. Mirrors
    /// <c>getComputedStyle(el).getPropertyValue('foo')</c>.</summary>
    public string GetPropertyValue(string property)
    {
        if (_values.TryGetValue(property, out var v)) return v;
        return "";
    }

    public bool TryGet(string property, out string value) =>
        _values.TryGetValue(property, out value!);

    public IEnumerable<KeyValuePair<string, string>> Entries() => _values;
}
