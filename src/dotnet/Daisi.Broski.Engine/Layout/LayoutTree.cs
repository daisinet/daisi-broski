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

        var style = resolver.Resolve(element);
        var position = style.GetPropertyValue("position");
        bool outOfFlow = position is "absolute" or "fixed";

        if (outOfFlow)
        {
            // Out-of-flow positioning. The element is taken
            // out of normal flow — its position comes from
            // top / right / bottom / left against its nearest
            // positioned ancestor (or the viewport for fixed).
            // Minimum-viable: anchor to (0, 0) of the containing
            // block, resolve top/left as px offsets. Doesn't
            // chase the "nearest positioned ancestor" chain
            // yet; uses the nearest flex/grid parent or the
            // block parent we were handed, which is right for
            // hero overlays and decoration sphere patterns
            // that anchor to a `position: relative` wrapper.
            ResolveAbsolutePosition(box, parent, style, fontSize, rootFontSize,
                viewport, position == "fixed");
            LayChildrenAndResolveHeight(box, element, resolver, viewport,
                fontSize, rootFontSize, declaredHeight, parent.Width);
            // Don't advance parent.Height — the out-of-flow
            // box doesn't push siblings down.
            return;
        }

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

    /// <summary>Position an out-of-flow box using its
    /// <c>top</c>/<c>left</c>/<c>right</c>/<c>bottom</c>
    /// offsets. Minimum-viable: treats <paramref name="parent"/>
    /// as the containing block (ignores the "nearest positioned
    /// ancestor" chain; adequate for hero overlays /
    /// decorations wrapped in a <c>position: relative</c>
    /// parent, which is the common pattern). <c>fixed</c>
    /// anchors to the viewport instead of the parent.</summary>
    /// <summary>Internal passthrough so FlexLayout /
    /// GridLayout can reuse the absolute-positioning math
    /// for out-of-flow flex / grid children.</summary>
    internal static void ResolveAbsolutePositionInternal(
        LayoutBox box, LayoutBox parent, ComputedStyle style,
        double fontSize, double rootFontSize, Viewport viewport, bool isFixed) =>
        ResolveAbsolutePosition(box, parent, style, fontSize, rootFontSize, viewport, isFixed);

    private static void ResolveAbsolutePosition(
        LayoutBox box, LayoutBox parent, ComputedStyle style,
        double fontSize, double rootFontSize, Viewport viewport, bool isFixed)
    {
        double cbX = isFixed ? 0 : parent.X;
        double cbY = isFixed ? 0 : parent.Y;
        double cbW = isFixed ? viewport.Width : parent.Width;
        double cbH = isFixed ? viewport.Height : parent.Height;

        var topL = Length.Parse(style.GetPropertyValue("top"));
        var leftL = Length.Parse(style.GetPropertyValue("left"));
        var rightL = Length.Parse(style.GetPropertyValue("right"));
        var bottomL = Length.Parse(style.GetPropertyValue("bottom"));

        double x = cbX;
        if (!leftL.IsNone && !leftL.IsAuto)
        {
            x = cbX + leftL.Resolve(cbW, fontSize, rootFontSize);
        }
        else if (!rightL.IsNone && !rightL.IsAuto && box.Width > 0)
        {
            x = cbX + cbW - rightL.Resolve(cbW, fontSize, rootFontSize) - box.Width;
        }
        double y = cbY;
        if (!topL.IsNone && !topL.IsAuto)
        {
            y = cbY + topL.Resolve(cbH, fontSize, rootFontSize);
        }
        else if (!bottomL.IsNone && !bottomL.IsAuto && box.Height > 0)
        {
            y = cbY + cbH - bottomL.Resolve(cbH, fontSize, rootFontSize) - box.Height;
        }
        box.X = x + box.Margin.Left + box.Border.Left + box.Padding.Left;
        box.Y = y + box.Margin.Top + box.Border.Top + box.Padding.Top;
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
        // <img> with no CSS width: prefer the width="" HTML
        // attribute, then the decoded image's natural width.
        // Falls through to the normal auto-fill path when
        // none of those apply.
        if (declaredWidth.IsNone && element.TagName == "img")
        {
            declaredWidth = ResolveImageDimension(element, "width", isHeight: false);
        }
        // <svg> sizes off its width="" attribute, and absent
        // that, off the viewBox width. Without a dimension the
        // inline-block would auto-fill the parent — way too
        // big for icons, which is the common case.
        if (declaredWidth.IsNone && element.TagName == "svg")
        {
            declaredWidth = ResolveSvgDimension(element, isHeight: false);
        }
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
        if (declaredHeight.IsNone && element.TagName == "img")
        {
            declaredHeight = ResolveImageDimension(element, "height", isHeight: true);
        }
        if (declaredHeight.IsNone && element.TagName == "svg")
        {
            declaredHeight = ResolveSvgDimension(element, isHeight: true);
        }

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

        // Inline-flow dispatch: when every element child is
        // inline-display, lay them out side-by-side with
        // line wrapping. Otherwise the default block-flow
        // loop stacks each child as a full-width block.
        if (InlineLayout.ShouldUseInlineFlow(element, resolver))
        {
            InlineLayout.LayoutChildren(box, element, resolver, viewport,
                fontSize, rootFontSize);
        }
        else
        {
            foreach (var child in element.ChildNodes)
            {
                if (child is Element childEl)
                {
                    BuildAndLay(box, childEl, resolver, viewport);
                }
            }
        }

        if (declaredHeight.IsNone || declaredHeight.IsAuto)
        {
            // box.Height was being mutated by child layouts
            // as we accumulated them — that's our content
            // height. If no children at all, keep zero
            // unless the element has direct text children.
            // Without this, a <p>Hello</p> with no element
            // children gets box.Height=0 and the next sibling
            // stacks on top, clipping the rendered letters.
            if (box.Height < 0.5)
            {
                double textH = MeasureDirectTextHeight(element, box.Width, fontSize);
                if (textH > 0) box.Height = textH;
            }
        }
        else
        {
            box.Height = declaredHeight.Resolve(containingHeight, fontSize, rootFontSize);
        }
    }

    /// <summary>Measure the vertical space needed to paint
    /// the element's direct <see cref="Text"/> children at
    /// the given <paramref name="fontSize"/>, word-wrapped to
    /// <paramref name="contentWidth"/>. Mirrors the painter's
    /// wrap + line-height math so block elements that only
    /// contain text (a <c>&lt;p&gt;Hello&lt;/p&gt;</c>, a
    /// <c>&lt;h1&gt;TITLE&lt;/h1&gt;</c>) reserve the right
    /// amount of height for their content.</summary>
    private static double MeasureDirectTextHeight(
        Element element, double contentWidth, double fontSize)
    {
        int charCount = 0;
        foreach (var c in element.ChildNodes)
        {
            if (c is Daisi.Broski.Engine.Dom.Text t) charCount += t.Data.Trim().Length;
        }
        if (charCount == 0) return 0;
        int scale = Daisi.Broski.Engine.Paint.BitmapFont.ScaleFor(fontSize);
        int cellW = Daisi.Broski.Engine.Paint.BitmapFont.CellWidth * scale;
        int maxChars = Math.Max(1, (int)(contentWidth / cellW));
        int lines = Math.Max(1, (int)Math.Ceiling(charCount / (double)maxChars));
        double lineAdvance = fontSize * 1.2;
        return lines * lineAdvance;
    }

    /// <summary>For an inline <c>&lt;svg&gt;</c> with no CSS
    /// sizing, derive width / height from the element's
    /// <c>width</c>/<c>height</c> attributes, then from the
    /// viewBox dimensions. Icons almost always specify one or
    /// the other; without a fallback an SVG would auto-fill
    /// the containing block (looks enormous in context).</summary>
    private static Length ResolveSvgDimension(Element element, bool isHeight)
    {
        var attrName = isHeight ? "height" : "width";
        var attr = element.GetAttribute(attrName);
        if (!string.IsNullOrEmpty(attr))
        {
            var len = Length.Parse(attr);
            if (!len.IsNone) return len;
        }
        var viewBox = element.GetAttribute("viewBox") ?? element.GetAttribute("viewbox");
        if (!string.IsNullOrEmpty(viewBox))
        {
            var parts = viewBox.Split(new[] { ' ', ',' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && double.TryParse(
                parts[isHeight ? 3 : 2],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) && v > 0)
            {
                return Length.Px(v);
            }
        }
        return Length.None;
    }

    /// <summary>For an <c>&lt;img&gt;</c> element with no CSS
    /// width/height, derive the dimension from the HTML
    /// <c>width</c>/<c>height</c> attribute, then from the
    /// decoded image's natural size. Returns
    /// <see cref="Length.None"/> when neither is available
    /// (the layout falls back to auto-fill in that case).</summary>
    private static Length ResolveImageDimension(Element element, string attrName, bool isHeight)
    {
        var attr = element.GetAttribute(attrName);
        if (!string.IsNullOrEmpty(attr))
        {
            // Plain integers (the legacy HTML attribute form
            // doesn't carry units) and pixel-suffixed lengths
            // both round-trip through Length.Parse.
            var len = Length.Parse(attr);
            if (!len.IsNone) return len;
        }
        var doc = element.OwnerDocument;
        if (doc?.Images is { } imgMap && imgMap.TryGetValue(element, out var raw))
        {
            if (raw is Daisi.Broski.Engine.Paint.RasterBuffer img)
            {
                return Length.Px(isHeight ? img.Height : img.Width);
            }
            // Fetched SVGs are stored as their root <svg>
            // element. Derive the intrinsic size from the
            // same attrs/viewBox chain inline <svg> uses.
            if (raw is Element svgRoot && svgRoot.TagName == "svg")
            {
                var svgLen = ResolveSvgDimension(svgRoot, isHeight);
                if (!svgLen.IsNone) return svgLen;
            }
        }
        return Length.None;
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
            Length? shortW;
            if (isBorder)
            {
                // `border: 1px solid red` has width + style +
                // color tokens, only one of which is a Length.
                // Extract the first length-parseable token and
                // apply it uniformly — border-top/right/bottom/
                // left-width all share the same width in the
                // shorthand.
                shortW = ExtractBorderShorthandWidth(shorthand);
                if (shortW is Length w)
                {
                    if (top.IsNone) top = w;
                    if (right.IsNone) right = w;
                    if (bottom.IsNone) bottom = w;
                    if (left.IsNone) left = w;
                }
            }
            else
            {
                var parts = ShorthandSides.Parse(shorthand);
                if (top.IsNone) top = parts.Top;
                if (right.IsNone) right = parts.Right;
                if (bottom.IsNone) bottom = parts.Bottom;
                if (left.IsNone) left = parts.Left;
            }
        }

        return new BoxEdges(
            ResolveEdge(top, containingWidth, fontSize, rootFontSize),
            ResolveEdge(right, containingWidth, fontSize, rootFontSize),
            ResolveEdge(bottom, containingWidth, fontSize, rootFontSize),
            ResolveEdge(left, containingWidth, fontSize, rootFontSize));
    }

    /// <summary>Pull the first length-parseable token out of
    /// a <c>border</c> shorthand. <c>border: 1px solid red</c>
    /// has three tokens of mixed types (width / style /
    /// color); we skip the style + color and return the
    /// width. Returns null when no length is found (so
    /// <c>border: solid red</c> without a width keeps the
    /// cascade-default zero and doesn't paint a border).</summary>
    private static Length? ExtractBorderShorthandWidth(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var token in SplitOnTopLevelSpaces(value))
        {
            // Keyword widths: thin / medium / thick per spec.
            switch (token.Trim().ToLowerInvariant())
            {
                case "thin": return Length.Px(1);
                case "medium": return Length.Px(3);
                case "thick": return Length.Px(5);
                case "none" or "hidden": return Length.Px(0);
            }
            var l = Length.Parse(token);
            if (!l.IsNone && !l.IsAuto) return l;
        }
        return null;
    }

    private static List<string> SplitOnTopLevelSpaces(string value)
    {
        var parts = new List<string>();
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
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
    private readonly IReadOnlyList<Stylesheet> _sheets;
    /// <summary>Per-pass memoization of resolved styles.
    /// One layout build calls Resolve(element) thousands of
    /// times — once per element, plus recursively for
    /// inheritance — and each call would otherwise walk
    /// every rule in every stylesheet. Caching makes the
    /// total work O(elements × rules) instead of
    /// O(elements × depth × rules).</summary>
    private readonly Dictionary<Element, ComputedStyle> _cache = new();

    public double RootFontSize { get; }

    public LayoutStyleResolver(Document document, Viewport viewport)
    {
        _document = document;
        _viewport = viewport;
        _userAgent = UserAgentStyles.Stylesheet;
        // Pre-compose the sheet list once. The UA stylesheet
        // sits first so author rules cascade on top per
        // origin precedence.
        var sheets = new List<Stylesheet>(_document.StyleSheets.Count + 1)
        {
            _userAgent,
        };
        sheets.AddRange(_document.StyleSheets);
        _sheets = sheets;
        RootFontSize = 16;
    }

    public ComputedStyle Resolve(Element element)
    {
        if (_cache.TryGetValue(element, out var hit)) return hit;
        var result = CompositeResolver.Resolve(element, _sheets, _viewport, this);
        _cache[element] = result;
        return result;
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
        Element element, IReadOnlyList<Stylesheet> sheets, Viewport viewport,
        LayoutStyleResolver? resolver = null)
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
                if (rule is StyleRule sr)
                {
                    AddIfMatches(element, sr, matches, ref sourceOrder);
                }
                else if (rule is AtRule ar
                    && ar.Name is "media" or "supports"
                    && StyleResolver.MediaQueryMatchesPublic(ar, viewport))
                {
                    // @media / @supports blocks — walk nested
                    // StyleRules when the query matches against
                    // the layout viewport. Without this, Bootstrap
                    // / Tailwind responsive breakpoints are
                    // silently dropped and `.col-lg-8 { width:
                    // 66.67% }` never applies.
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
        ApplyInheritance(element, sheets, viewport, values, resolver);

        // Inherit custom properties + substitute var()
        // references so layout-time consumers (display,
        // width, etc.) see resolved values.
        InheritCustomProperties(element, sheets, viewport, values, resolver);
        VarResolver.SubstituteAll(values);

        return new ComputedStyle(values);
    }

    private static void InheritCustomProperties(
        Element element, IReadOnlyList<Stylesheet> sheets, Viewport viewport,
        Dictionary<string, string> into, LayoutStyleResolver? resolver)
    {
        for (var anc = element.ParentNode as Element; anc is not null; anc = anc.ParentNode as Element)
        {
            ComputedStyle ancStyle = resolver is not null
                ? resolver.Resolve(anc)
                : Resolve(anc, sheets, viewport);
            foreach (var kv in ancStyle.Entries())
            {
                if (!kv.Key.StartsWith("--", StringComparison.Ordinal)) continue;
                if (into.ContainsKey(kv.Key)) continue;
                into[kv.Key] = kv.Value;
            }
        }
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
        Dictionary<string, string> into, LayoutStyleResolver? resolver)
    {
        // Hit the layout-resolver's per-pass cache when one
        // was supplied so each ancestor resolves at most
        // once per layout build, not per-call.
        var ancestorCache = resolver is null
            ? new Dictionary<Element, ComputedStyle>() : null;
        foreach (var prop in InheritableProps)
        {
            if (into.ContainsKey(prop)) continue;
            for (var anc = element.ParentNode as Element; anc is not null; anc = anc.ParentNode as Element)
            {
                ComputedStyle ancStyle;
                if (resolver is not null)
                {
                    ancStyle = resolver.Resolve(anc);
                }
                else if (!ancestorCache!.TryGetValue(anc, out var cached))
                {
                    ancStyle = Resolve(anc, sheets, viewport);
                    ancestorCache[anc] = ancStyle;
                }
                else
                {
                    ancStyle = cached;
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
