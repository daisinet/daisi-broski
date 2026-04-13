using Daisi.Broski.Engine.Css;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Phase-6c layout entry point: build a layout tree for a
/// <see cref="Document"/> against a viewport, run block flow
/// to converge on pixel positions, return the root box.
/// Lazy by design — call <see cref="Build"/> once per layout
/// query; the tree is regenerated on each call so the result
/// always reflects the current DOM + cascade state.
///
/// <para>
/// The block-flow algorithm is the simplest version that
/// agrees with real browsers on pages without inline /
/// float / flex / grid:
/// <list type="number">
/// <item>For each child of a block box, compute its
///   resolved width (auto → fill parent, percentage →
///   relative to parent's content width, fixed → as
///   declared).</item>
/// <item>Lay out the child's own children first (recursive)
///   so its content height is known.</item>
/// <item>Compute the child's height (auto → sum of child
///   heights, percentage → relative, fixed → as
///   declared).</item>
/// <item>Position the child at the parent's current cursor
///   (top edge), respecting margins. Advance the cursor by
///   the child's outer height.</item>
/// </list>
/// Margin collapsing isn't modeled — adjacent vertical
/// margins simply sum. That's wrong per spec but converges
/// to "close enough" for real pages and keeps the algorithm
/// linear.
/// </para>
/// </summary>
public static class LayoutTree
{
    /// <summary>Build and lay out a tree rooted at the
    /// document's root element. Returns the root layout box
    /// (the synthetic viewport box wrapping the DOM root).
    /// Pass a custom <paramref name="viewport"/> to layout
    /// against a non-default canvas.</summary>
    public static LayoutBox Build(Document document, Viewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        viewport ??= Viewport.Default;
        var resolver = new LayoutStyleResolver(document, viewport);

        var root = new LayoutBox
        {
            Display = BoxDisplay.Block,
            Width = viewport.Width,
            Height = 0,
            X = 0,
            Y = 0,
        };

        if (document.DocumentElement is not null)
        {
            BuildAndLay(root, document.DocumentElement, resolver, viewport);
        }
        return root;
    }

    /// <summary>Find the layout box for a given element by
    /// walking the tree. Returns null when the element isn't
    /// present (e.g. <c>display: none</c> excluded it).</summary>
    public static LayoutBox? Find(LayoutBox root, Element element)
    {
        foreach (var box in root.Descendants())
        {
            if (box.Element == element) return box;
        }
        return null;
    }

    private static void BuildAndLay(
        LayoutBox parent, Element element,
        LayoutStyleResolver resolver, Viewport viewport)
    {
        var prepared = PrepareBox(parent, element, resolver, viewport);
        if (prepared is null) return;
        var (box, fontSize, rootFontSize, declaredHeight) = prepared.Value;
        parent.Children.Add(box);

        // Position the box at the parent's current cursor —
        // the parent's height (so far) plus this box's top
        // outer edge. The X is parent.X + outer-left edges
        // so the content area sits inside padding/border.
        box.X = parent.X + box.Margin.Left + box.Border.Left + box.Padding.Left;
        box.Y = parent.Y + parent.Height
            + box.Margin.Top + box.Border.Top + box.Padding.Top;

        LayChildrenAndResolveHeight(box, element, resolver, viewport,
            fontSize, rootFontSize, declaredHeight, parent.Width);

        // Advance the parent's content height by this box's
        // outer height so the next sibling sits below.
        parent.Height += OuterHeight(box);
    }

    /// <summary>Build a layout box for <paramref name="element"/>
    /// without positioning it. Used by both the normal block-flow
    /// path (<see cref="BuildAndLay"/>) and by
    /// <see cref="FlexLayout"/>, which needs to construct items
    /// before computing their main-axis positions. Returns
    /// <c>null</c> when the element has <c>display: none</c>.</summary>
    internal static (LayoutBox Box, double FontSize, double RootFontSize, Length DeclaredHeight)?
        PrepareBox(
            LayoutBox parent, Element element,
            LayoutStyleResolver resolver, Viewport viewport)
    {
        var style = resolver.Resolve(element);
        var display = ParseDisplay(style.GetPropertyValue("display"), element);
        if (display == BoxDisplay.None) return null;

        var fontSize = ResolveFontSize(style, parent);
        var rootFontSize = resolver.RootFontSize;

        var margin = ResolveEdges(style, "margin", parent.Width, fontSize, rootFontSize);
        var padding = ResolveEdges(style, "padding", parent.Width, fontSize, rootFontSize);
        var border = ResolveEdges(style, "border", parent.Width, fontSize, rootFontSize, isBorder: true);

        var declaredWidth = Length.Parse(style.GetPropertyValue("width"));
        double width;
        if (declaredWidth.IsNone || declaredWidth.IsAuto)
        {
            width = parent.Width
                - margin.Left - margin.Right
                - padding.Left - padding.Right
                - border.Left - border.Right;
            if (width < 0) width = 0;
        }
        else
        {
            width = declaredWidth.Resolve(parent.Width, fontSize, rootFontSize);
        }

        var declaredHeight = Length.Parse(style.GetPropertyValue("height"));

        var box = new LayoutBox
        {
            Element = element,
            Display = display,
            Width = width,
            Margin = margin,
            Padding = padding,
            Border = border,
        };
        return (box, fontSize, rootFontSize, declaredHeight);
    }

    /// <summary>Lay the box's children (block-flow or flex) and
    /// resolve the box's height from the declared value or from
    /// the accumulated content height.</summary>
    internal static void LayChildrenAndResolveHeight(
        LayoutBox box, Element element,
        LayoutStyleResolver resolver, Viewport viewport,
        double fontSize, double rootFontSize,
        Length declaredHeight, double containingHeight)
    {
        // For flex containers, resolve the declared height
        // BEFORE laying out children — the flex algorithm
        // needs the container's cross size to drive
        // align-items / stretch / cross-axis positioning.
        // Block flow doesn't care about the container's
        // height during child layout (it's purely
        // accumulated from below), so we keep the post-
        // children resolution there.
        if (box.Display == BoxDisplay.Flex)
        {
            if (!declaredHeight.IsNone && !declaredHeight.IsAuto)
            {
                box.Height = declaredHeight.Resolve(containingHeight, fontSize, rootFontSize);
            }
            var style = resolver.Resolve(element);
            FlexLayout.LayoutChildren(box, element, style, resolver, viewport,
                fontSize, rootFontSize);
            return;
        }
        if (box.Display == BoxDisplay.Grid)
        {
            if (!declaredHeight.IsNone && !declaredHeight.IsAuto)
            {
                box.Height = declaredHeight.Resolve(containingHeight, fontSize, rootFontSize);
            }
            var style = resolver.Resolve(element);
            GridLayout.LayoutChildren(box, element, style, resolver, viewport,
                fontSize, rootFontSize);
            return;
        }

        foreach (var child in element.ChildNodes)
        {
            if (child is Element childEl)
            {
                BuildAndLay(box, childEl, resolver, viewport);
            }
        }

        if (declaredHeight.IsNone || declaredHeight.IsAuto)
        {
            // box.Height was being mutated by child layouts
            // as we accumulated them — that's our content
            // height. If no children at all, keep zero.
        }
        else
        {
            box.Height = declaredHeight.Resolve(containingHeight, fontSize, rootFontSize);
        }
    }

    /// <summary>Compute a box's outer height — content height
    /// plus the four edge sides. Shared by both the block-flow
    /// path and FlexLayout (column direction).</summary>
    internal static double OuterHeight(LayoutBox box) =>
        box.Height
        + box.Margin.Top + box.Margin.Bottom
        + box.Padding.Top + box.Padding.Bottom
        + box.Border.Top + box.Border.Bottom;

    /// <summary>Compute a box's outer width — content width
    /// plus the four edge sides on the horizontal axis.
    /// Shared by FlexLayout (row direction).</summary>
    internal static double OuterWidth(LayoutBox box) =>
        box.Width
        + box.Margin.Left + box.Margin.Right
        + box.Padding.Left + box.Padding.Right
        + box.Border.Left + box.Border.Right;

    private static BoxDisplay ParseDisplay(string value, Element element)
    {
        // Empty value falls back to the spec default for the
        // element's natural role. We could special-case more
        // tags here; the user-agent stylesheet covers the
        // common ones already.
        if (string.IsNullOrEmpty(value))
        {
            return element.TagName switch
            {
                "html" or "body" or "div" or "section" or "article"
                    or "header" or "footer" or "nav" or "main"
                    or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                    or "ul" or "ol" or "li" or "blockquote" or "pre"
                    => BoxDisplay.Block,
                "head" or "script" or "style" or "link" or "meta" or "title"
                    => BoxDisplay.None,
                _ => BoxDisplay.Inline,
            };
        }
        return value switch
        {
            "none" => BoxDisplay.None,
            "flex" => BoxDisplay.Flex,
            "grid" => BoxDisplay.Grid,
            "block" or "list-item"
                or "table" or "table-row" or "table-cell"
                => BoxDisplay.Block,
            "inline" => BoxDisplay.Inline,
            "inline-block" => BoxDisplay.InlineBlock,
            _ => BoxDisplay.Block,
        };
    }

    private static double ResolveFontSize(ComputedStyle style, LayoutBox parent)
    {
        // Inheritance has already been applied by the cascade
        // resolver, so a missing font-size means there's no
        // declared value at any ancestor — fall back to the
        // root default of 16px.
        var raw = Length.Parse(style.GetPropertyValue("font-size"));
        return raw.Resolve(parent.Width, parent.Width > 0 ? 16 : 16, 16, fallback: 16);
    }

    /// <summary>Compute the four edge offsets for a CSS box-
    /// model property family (<c>margin</c>, <c>padding</c>,
    /// or <c>border</c>). Per-side longhands win over the
    /// shorthand; the shorthand is parsed as a 1–4 token
    /// space-separated list per CSS 2.1 §8.5. The
    /// <c>border</c> family also recognizes the
    /// <c>border-{side}-width</c> longhands and treats the
    /// shorthand <c>border</c> declaration as supplying the
    /// width when only one numeric token is present.</summary>
    private static BoxEdges ResolveEdges(
        ComputedStyle style, string family,
        double containingWidth, double fontSize, double rootFontSize,
        bool isBorder = false)
    {
        // 1) Longhand per-side wins. For border the longhand
        // suffix is `-width` (`border-top-width`); for the
        // others it's just the side.
        var top = Length.Parse(style.GetPropertyValue(
            isBorder ? $"{family}-top-width" : $"{family}-top"));
        var right = Length.Parse(style.GetPropertyValue(
            isBorder ? $"{family}-right-width" : $"{family}-right"));
        var bottom = Length.Parse(style.GetPropertyValue(
            isBorder ? $"{family}-bottom-width" : $"{family}-bottom"));
        var left = Length.Parse(style.GetPropertyValue(
            isBorder ? $"{family}-left-width" : $"{family}-left"));

        // 2) Fall back to shorthand for any unset side.
        if (top.IsNone || right.IsNone || bottom.IsNone || left.IsNone)
        {
            var shorthand = style.GetPropertyValue(family);
            var parts = ShorthandSides.Parse(shorthand);
            if (top.IsNone) top = parts.Top;
            if (right.IsNone) right = parts.Right;
            if (bottom.IsNone) bottom = parts.Bottom;
            if (left.IsNone) left = parts.Left;
        }

        return new BoxEdges(
            ResolveEdge(top, containingWidth, fontSize, rootFontSize),
            ResolveEdge(right, containingWidth, fontSize, rootFontSize),
            ResolveEdge(bottom, containingWidth, fontSize, rootFontSize),
            ResolveEdge(left, containingWidth, fontSize, rootFontSize));
    }

    private static double ResolveEdge(Length len, double containingWidth, double fontSize, double rootFontSize)
    {
        // Auto margins are spec'd to behave specially (they
        // center the box horizontally for block layout).
        // Phase 6c treats them as zero for now — a more
        // faithful resolver lands in 6d when text alignment
        // exposes the same need.
        if (len.IsAuto || len.IsNone) return 0;
        return len.Resolve(containingWidth, fontSize, rootFontSize);
    }
}

/// <summary>Wraps the cascade resolver with the user-agent
/// stylesheet baseline applied first so layout always sees
/// the standard display/font/margin defaults the spec
/// requires. The author stylesheet is layered on top per
/// origin precedence.</summary>
internal sealed class LayoutStyleResolver
{
    private readonly Document _document;
    private readonly Viewport _viewport;
    private readonly Stylesheet _userAgent;

    public double RootFontSize { get; }

    public LayoutStyleResolver(Document document, Viewport viewport)
    {
        _document = document;
        _viewport = viewport;
        _userAgent = UserAgentStyles.Stylesheet;
        // Compute root font-size once per layout pass —
        // every em / rem resolution refers back to it.
        RootFontSize = 16;
    }

    public ComputedStyle Resolve(Element element)
    {
        // For now, use the public StyleResolver and rely on
        // the user-agent stylesheet being injected as the
        // first source. We achieve that by pre-pending it
        // via a per-call composite list — cheap because the
        // UA sheet is parsed once and cached.
        return ResolveWithUaStylesheet(element);
    }

    private ComputedStyle ResolveWithUaStylesheet(Element element)
    {
        // Compose: UA sheet + document.styleSheets in order.
        // The cleanest way without changing public API is to
        // call into a private helper that takes an explicit
        // sheet list.
        var sheets = new List<Stylesheet>(_document.StyleSheets.Count + 1)
        {
            _userAgent,
        };
        sheets.AddRange(_document.StyleSheets);
        return CompositeResolver.Resolve(element, sheets, _viewport);
    }
}

/// <summary>Variant of <see cref="StyleResolver"/> that
/// accepts an explicit stylesheet list — used by layout to
/// inject the user-agent baseline ahead of the document's
/// author rules. Internal because the public single-document
/// API is what almost every caller wants.</summary>
internal static class CompositeResolver
{
    public static ComputedStyle Resolve(
        Element element, IReadOnlyList<Stylesheet> sheets, Viewport viewport)
    {
        // Implementation duplicates StyleResolver.Resolve
        // but reads sheets from the supplied list rather than
        // from element.OwnerDocument.StyleSheets. The
        // duplication is small enough not to be worth a
        // shared internal method right now.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<(string Property, string Value, bool Important, Specificity Spec, int Order)>();
        int sourceOrder = 0;
        foreach (var sheet in sheets)
        {
            foreach (var rule in sheet.Rules)
            {
                if (rule is StyleRule sr) AddIfMatches(element, sr, matches, ref sourceOrder);
            }
            // @media support omitted from the layout-side resolver;
            // the public StyleResolver covers it. Layout typically
            // wants the largest/representative viewport anyway.
        }
        matches.Sort((a, b) =>
        {
            int impCmp = a.Important.CompareTo(b.Important);
            if (impCmp != 0) return impCmp;
            int spec = a.Spec.CompareTo(b.Spec);
            if (spec != 0) return spec;
            return a.Order.CompareTo(b.Order);
        });
        foreach (var m in matches) values[m.Property] = m.Value;

        // Inline style on top.
        var styleAttr = element.GetAttribute("style");
        if (!string.IsNullOrEmpty(styleAttr))
        {
            foreach (var d in CssParser.ParseDeclarationList(styleAttr))
            {
                values[d.Property] = d.Value;
            }
        }

        // Inheritance — same set as StyleResolver.
        ApplyInheritance(element, sheets, viewport, values);

        return new ComputedStyle(values);
    }

    private static void AddIfMatches(
        Element element, StyleRule rule,
        List<(string, string, bool, Specificity, int)> matches, ref int order)
    {
        if (rule.Selectors is null) return;
        Specificity? best = null;
        foreach (var complex in rule.Selectors.Selectors)
        {
            if (Daisi.Broski.Engine.Dom.Selectors.SelectorMatcher.MatchesComplex(element, complex))
            {
                var s = StyleResolver.ComputeSpecificity(complex);
                if (best is null || s.CompareTo(best.Value) > 0) best = s;
            }
        }
        if (best is null) return;
        int o = order++;
        foreach (var d in rule.Declarations)
        {
            matches.Add((d.Property, d.Value, d.Important, best.Value, o));
        }
    }

    private static void ApplyInheritance(
        Element element, IReadOnlyList<Stylesheet> sheets, Viewport viewport,
        Dictionary<string, string> into)
    {
        var ancestorCache = new Dictionary<Element, ComputedStyle>();
        foreach (var prop in InheritableProps)
        {
            if (into.ContainsKey(prop)) continue;
            for (var anc = element.ParentNode as Element; anc is not null; anc = anc.ParentNode as Element)
            {
                if (!ancestorCache.TryGetValue(anc, out var ancStyle))
                {
                    ancStyle = Resolve(anc, sheets, viewport);
                    ancestorCache[anc] = ancStyle;
                }
                if (ancStyle.TryGet(prop, out var v))
                {
                    into[prop] = v;
                    break;
                }
            }
        }
    }

    // Mirrors the inheritable list in StyleResolver. Kept as
    // a private duplicate rather than promoted to a shared
    // helper because both callers benefit from the array
    // being a static-readonly direct-access field.
    private static readonly string[] InheritableProps = new[]
    {
        "color", "font", "font-family", "font-size", "font-weight",
        "font-style", "font-variant", "line-height", "text-align",
        "text-indent", "text-transform", "white-space",
        "letter-spacing", "word-spacing", "direction", "visibility",
        "cursor", "list-style", "list-style-type",
    };
}

/// <summary>Parse the 1–4 token shorthand value for box-edge
/// properties (<c>margin: 1px 2px 3px 4px</c>) into the four
/// resolved edges per CSS 2.1 §8.5: 1 token = all sides,
/// 2 tokens = vertical / horizontal, 3 = top / horizontal /
/// bottom, 4 = top / right / bottom / left.</summary>
internal static class ShorthandSides
{
    public static (Length Top, Length Right, Length Bottom, Length Left) Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (Length.None, Length.None, Length.None, Length.None);
        var tokens = SplitTopLevel(value);
        return tokens.Count switch
        {
            1 => (Length.Parse(tokens[0]), Length.Parse(tokens[0]), Length.Parse(tokens[0]), Length.Parse(tokens[0])),
            2 => (Length.Parse(tokens[0]), Length.Parse(tokens[1]), Length.Parse(tokens[0]), Length.Parse(tokens[1])),
            3 => (Length.Parse(tokens[0]), Length.Parse(tokens[1]), Length.Parse(tokens[2]), Length.Parse(tokens[1])),
            >= 4 => (Length.Parse(tokens[0]), Length.Parse(tokens[1]), Length.Parse(tokens[2]), Length.Parse(tokens[3])),
            _ => (Length.None, Length.None, Length.None, Length.None),
        };
    }

    private static List<string> SplitTopLevel(string value)
    {
        var parts = new List<string>();
        int depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
