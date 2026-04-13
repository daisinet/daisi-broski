using System.Text;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// Convert an <see cref="ArticleContent"/> into CommonMark Markdown.
/// The header carries the title, byline, and date as a small YAML-ish
/// preamble (so downstream tools can parse them without re-extracting),
/// followed by the body translated tag-by-tag from
/// <see cref="ArticleContent.ContentRoot"/>.
///
/// <para>
/// Supported source tags: <c>h1</c>–<c>h6</c>, <c>p</c>, <c>br</c>,
/// <c>strong</c>/<c>b</c>, <c>em</c>/<c>i</c>, <c>code</c>,
/// <c>pre</c>, <c>blockquote</c>, <c>ul</c>/<c>ol</c>/<c>li</c>,
/// <c>a</c>, <c>img</c>, <c>hr</c>, <c>figure</c>/<c>figcaption</c>.
/// Unknown tags are walked through transparently (their text content is
/// emitted but the tag itself is dropped).
/// </para>
///
/// <para>
/// Anchor and image targets are resolved against the article URL by
/// the extractor before they hit this formatter, so the markdown links
/// are always absolute.
/// </para>
/// </summary>
public static class MarkdownFormatter
{
    public static string Format(ArticleContent article)
    {
        ArgumentNullException.ThrowIfNull(article);

        var sb = new StringBuilder();

        // Title as the H1 — readers expect this even if the original
        // page used the title only for the browser tab.
        if (!string.IsNullOrWhiteSpace(article.Title))
        {
            sb.Append("# ").Append(article.Title!.Trim()).Append("\n\n");
        }

        // Compact metadata block. Italicized so it visually separates
        // from the body; markdown renderers display it as a one-liner
        // with bullet separators.
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(article.Byline))
            meta.Add($"By **{article.Byline}**");
        if (!string.IsNullOrWhiteSpace(article.PublishedAt))
            meta.Add(article.PublishedAt!);
        if (!string.IsNullOrWhiteSpace(article.SiteName))
            meta.Add($"on _{article.SiteName}_");
        meta.Add($"[source]({article.Url.AbsoluteUri})");
        sb.Append(string.Join(" • ", meta)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(article.HeroImage))
        {
            sb.Append($"![]({article.HeroImage})\n\n");
        }

        if (!string.IsNullOrWhiteSpace(article.Description))
        {
            sb.Append("> ").Append(article.Description).Append("\n\n");
        }

        if (article.ContentRoot is not null)
        {
            var ctx = new RenderContext(article.Url);
            RenderBlock(article.ContentRoot, ctx);
            sb.Append(ctx.Output.ToString().TrimEnd()).Append('\n');
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------
    // DOM walker
    // -------------------------------------------------------------------

    private sealed class RenderContext
    {
        public StringBuilder Output { get; } = new();
        public Uri BaseUri { get; }
        public int ListDepth { get; set; }
        public bool InsideOrderedList { get; set; }
        public int OrderedItemCounter { get; set; }
        public int BlockQuoteDepth { get; set; }
        public RenderContext(Uri baseUri) { BaseUri = baseUri; }
    }

    private static void RenderBlock(Element el, RenderContext ctx)
    {
        switch (el.TagName)
        {
            case "h1": EmitHeading(el, ctx, 1); return;
            case "h2": EmitHeading(el, ctx, 2); return;
            case "h3": EmitHeading(el, ctx, 3); return;
            case "h4": EmitHeading(el, ctx, 4); return;
            case "h5": EmitHeading(el, ctx, 5); return;
            case "h6": EmitHeading(el, ctx, 6); return;

            case "p":
                EmitParagraph(el, ctx);
                return;

            case "ul":
            case "ol":
                EmitList(el, ctx, ordered: el.TagName == "ol");
                return;

            case "blockquote":
                EmitBlockQuote(el, ctx);
                return;

            case "pre":
                EmitCodeBlock(el, ctx);
                return;

            case "hr":
                EmitLinePrefix(ctx);
                ctx.Output.Append("---\n\n");
                return;

            case "figure":
                RenderBlockChildren(el, ctx);
                return;

            case "figcaption":
                EmitLinePrefix(ctx);
                ctx.Output.Append('*');
                EmitInlineChildren(el, ctx);
                ctx.Output.Append("*\n\n");
                return;

            case "br":
                ctx.Output.Append("  \n");
                return;

            case "img":
                EmitImage(el, ctx);
                ctx.Output.Append("\n\n");
                return;

            // Wrapper blocks: walk children. Article / section /
            // div etc. — we don't need to mark them up but their
            // children carry the real content. We delegate to
            // RenderBlockChildren so runs of inline children
            // (text + <a> + <strong> + ...) get coalesced into a
            // single paragraph instead of each being emitted in
            // isolation (which loses link wrapping for an <a>
            // appearing as a direct child of a wrapper div).
            case "article":
            case "section":
            case "main":
            case "div":
            case "header":
            case "footer":
            case "details":
            case "summary":
                RenderBlockChildren(el, ctx);
                return;

            // Tables: rough conversion. Real CommonMark needs a
            // pipe-table extension, which most renderers handle.
            case "table":
                EmitTable(el, ctx);
                return;
        }

        // Unknown tag at the block level: if it's an inline element
        // (a / strong / em / span / ...), wrap it in its own
        // paragraph through the inline renderer so any href / emphasis
        // markup survives. Otherwise just walk its children as blocks
        // to recover whatever readable content is in there.
        if (IsInlineTag(el.TagName))
        {
            EmitInlineParagraph(el, ctx);
        }
        else
        {
            RenderBlockChildren(el, ctx);
        }
    }

    /// <summary>
    /// Walk an element's children, grouping consecutive inline
    /// content (Text nodes + inline elements) into single paragraphs
    /// so links / emphasis / images survive the block dispatch loop.
    /// Block-level children (<c>p</c>, <c>h1-h6</c>, <c>ul</c>, ...)
    /// flush the in-flight paragraph and dispatch to RenderBlock.
    /// </summary>
    private static void RenderBlockChildren(Element el, RenderContext ctx)
    {
        StringBuilder? inlineBuf = null;

        void Flush()
        {
            if (inlineBuf is null) return;
            var text = inlineBuf.ToString().Trim();
            inlineBuf = null;
            if (text.Length == 0) return;
            EmitLinePrefix(ctx);
            ctx.Output.Append(text).Append("\n\n");
        }

        foreach (var child in el.ChildNodes)
        {
            if (child is Text t)
            {
                if (string.IsNullOrWhiteSpace(t.Data)) continue;
                inlineBuf ??= new StringBuilder();
                inlineBuf.Append(EscapeInline(t.Data));
                continue;
            }
            if (child is not Element c) continue;

            if (IsInlineTag(c.TagName))
            {
                // Append a single space between adjacent inline runs
                // so consecutive sibling links don't collide
                // ("[a](u1)[b](u2)" instead of "[a](u1) [b](u2)").
                if (inlineBuf is not null && inlineBuf.Length > 0 &&
                    !char.IsWhiteSpace(inlineBuf[^1]))
                {
                    inlineBuf.Append(' ');
                }
                inlineBuf ??= new StringBuilder();
                EmitInlineNode(c, ctx, inlineBuf);
            }
            else
            {
                Flush();
                RenderBlock(c, ctx);
            }
        }
        Flush();
    }

    /// <summary>True for tags that produce inline (run-of-text) markup
    /// in CommonMark. Anything block-level dispatches through
    /// <see cref="RenderBlock"/>.</summary>
    private static bool IsInlineTag(string tag) => tag switch
    {
        "a" or "strong" or "b" or "em" or "i" or "code" or "span" or
        "small" or "sub" or "sup" or "mark" or "kbd" or "u" or
        "img" or "br" or "abbr" or "cite" or "q" or "time" or "var"
            => true,
        _ => false,
    };

    /// <summary>Wrap a single inline element (e.g. an <c>&lt;a&gt;</c>
    /// appearing at block-level position) in its own paragraph so its
    /// inline-only markup survives the block dispatch.</summary>
    private static void EmitInlineParagraph(Element el, RenderContext ctx)
    {
        var sb = new StringBuilder();
        EmitInlineNode(el, ctx, sb);
        var text = sb.ToString().Trim();
        if (text.Length == 0) return;
        EmitLinePrefix(ctx);
        ctx.Output.Append(text).Append("\n\n");
    }

    private static void EmitHeading(Element el, RenderContext ctx, int level)
    {
        // Build the heading inline first so we can collapse runaway
        // whitespace from CSS-driven layouts (sites that do
        // `<h1>FOO\n  BAR\n  BAZ</h1>` and rely on display:flex to
        // make it look like a single line). Without this, the
        // markdown heading rendered with raw newlines, which
        // CommonMark splits into multiple paragraphs.
        var inline = new StringBuilder();
        EmitInlineChildren(el, ctx, inline);
        var text = CollapseRunWhitespace(inline.ToString()).Trim();
        if (text.Length == 0) return;

        EmitLinePrefix(ctx);
        ctx.Output.Append(new string('#', level)).Append(' ').Append(text);
        ctx.Output.Append("\n\n");
    }

    /// <summary>Collapse runs of whitespace (spaces, tabs, newlines)
    /// to single spaces. Used inside headings + other contexts where
    /// spec-strict CommonMark wants a single line.</summary>
    private static string CollapseRunWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool inWs = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWs && sb.Length > 0) sb.Append(' ');
                inWs = true;
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        return sb.ToString();
    }

    private static void EmitParagraph(Element el, RenderContext ctx)
    {
        var inlineSb = new StringBuilder();
        EmitInlineChildren(el, ctx, inlineSb);
        var text = inlineSb.ToString().Trim();
        if (text.Length == 0) return;

        EmitLinePrefix(ctx);
        ctx.Output.Append(text).Append("\n\n");
    }

    private static void EmitList(Element el, RenderContext ctx, bool ordered)
    {
        bool savedOrdered = ctx.InsideOrderedList;
        int savedCounter = ctx.OrderedItemCounter;
        ctx.InsideOrderedList = ordered;
        ctx.OrderedItemCounter = 0;
        ctx.ListDepth++;

        foreach (var child in el.Children)
        {
            if (child.TagName != "li") continue;
            EmitListItem(child, ctx);
        }

        ctx.ListDepth--;
        ctx.InsideOrderedList = savedOrdered;
        ctx.OrderedItemCounter = savedCounter;
        ctx.Output.Append('\n');
    }

    private static void EmitListItem(Element li, RenderContext ctx)
    {
        EmitLinePrefix(ctx);
        // List indentation: 2 spaces per nesting level beyond the
        // first.
        int indent = (ctx.ListDepth - 1) * 2;
        if (indent > 0) ctx.Output.Append(new string(' ', indent));

        if (ctx.InsideOrderedList)
        {
            ctx.OrderedItemCounter++;
            ctx.Output.Append(ctx.OrderedItemCounter).Append(". ");
        }
        else
        {
            ctx.Output.Append("- ");
        }

        // Inline content first — anything that isn't a nested list.
        var nestedLists = new List<Element>();
        var inlineSb = new StringBuilder();
        foreach (var child in li.ChildNodes)
        {
            if (child is Element c && (c.TagName == "ul" || c.TagName == "ol"))
            {
                nestedLists.Add(c);
            }
            else
            {
                EmitInlineNode(child, ctx, inlineSb);
            }
        }
        ctx.Output.Append(inlineSb.ToString().Trim()).Append('\n');

        // Now nested lists, after the item's own line.
        foreach (var nested in nestedLists) EmitList(nested, ctx, ordered: nested.TagName == "ol");
    }

    private static void EmitBlockQuote(Element el, RenderContext ctx)
    {
        ctx.BlockQuoteDepth++;
        foreach (var child in el.Children) RenderBlock(child, ctx);
        ctx.BlockQuoteDepth--;
    }

    private static void EmitCodeBlock(Element el, RenderContext ctx)
    {
        // Look for a language hint on a child <code class="language-*">
        // — that's how every common syntax-highlighting renderer
        // (highlight.js, prism, GitHub) tags pre+code blocks.
        string? lang = null;
        var codeChild = el.QuerySelector("code");
        if (codeChild is not null)
        {
            foreach (var token in codeChild.ClassList)
            {
                if (token.StartsWith("language-", StringComparison.Ordinal))
                {
                    lang = token["language-".Length..];
                    break;
                }
                if (token.StartsWith("lang-", StringComparison.Ordinal))
                {
                    lang = token["lang-".Length..];
                    break;
                }
            }
        }

        EmitLinePrefix(ctx);
        ctx.Output.Append("```").Append(lang ?? "").Append('\n');
        // Use the raw text — code blocks shouldn't have inline
        // markdown processing applied.
        ctx.Output.Append(el.TextContent.TrimEnd());
        ctx.Output.Append("\n```\n\n");
    }

    private static void EmitImage(Element el, RenderContext ctx)
    {
        var src = el.GetAttribute("src") ??
                  el.GetAttribute("data-src") ??
                  el.GetAttribute("data-original");
        if (string.IsNullOrWhiteSpace(src)) return;
        var alt = (el.GetAttribute("alt") ?? "").Trim();
        var resolved = ResolveAbs(src, ctx.BaseUri);
        EmitLinePrefix(ctx);
        ctx.Output.Append('!').Append('[').Append(EscapeAlt(alt))
            .Append("](").Append(resolved).Append(')');
    }

    private static void EmitTable(Element el, RenderContext ctx)
    {
        var rows = new List<List<string>>();
        bool sawHead = false;
        foreach (var section in el.Children)
        {
            if (section.TagName is not ("thead" or "tbody" or "tr")) continue;
            if (section.TagName == "tr")
            {
                rows.Add(ExtractRow(section, ctx));
            }
            else
            {
                if (section.TagName == "thead") sawHead = true;
                foreach (var tr in section.Children)
                {
                    if (tr.TagName == "tr") rows.Add(ExtractRow(tr, ctx));
                }
            }
        }
        if (rows.Count == 0) return;
        int columnCount = 0;
        foreach (var r in rows) if (r.Count > columnCount) columnCount = r.Count;

        EmitLinePrefix(ctx);
        // Header row + separator: if no thead was seen, treat row 0
        // as the header (most CommonMark-compatible renderers
        // require a header row).
        for (int i = 0; i < rows.Count; i++)
        {
            ctx.Output.Append("| ");
            for (int c = 0; c < columnCount; c++)
            {
                var cell = c < rows[i].Count ? rows[i][c] : "";
                ctx.Output.Append(cell.Replace('|', '\\').Replace('\n', ' '));
                ctx.Output.Append(" |");
                if (c < columnCount - 1) ctx.Output.Append(' ');
            }
            ctx.Output.Append('\n');

            if (i == 0)
            {
                ctx.Output.Append('|');
                for (int c = 0; c < columnCount; c++) ctx.Output.Append(" --- |");
                ctx.Output.Append('\n');
            }
            _ = sawHead; // consumed only as a hint above — we always emit a separator after row 0
        }
        ctx.Output.Append('\n');
    }

    private static List<string> ExtractRow(Element tr, RenderContext ctx)
    {
        var cells = new List<string>();
        foreach (var c in tr.Children)
        {
            if (c.TagName is not ("td" or "th")) continue;
            var sb = new StringBuilder();
            EmitInlineChildren(c, ctx, sb);
            cells.Add(sb.ToString().Trim());
        }
        return cells;
    }

    // -------------------------------------------------------------------
    // Inline rendering
    // -------------------------------------------------------------------

    private static void EmitInlineChildren(Element parent, RenderContext ctx)
        => EmitInlineChildren(parent, ctx, ctx.Output);

    private static void EmitInlineChildren(Element parent, RenderContext ctx, StringBuilder sb)
    {
        foreach (var child in parent.ChildNodes)
        {
            EmitInlineNode(child, ctx, sb);
        }
    }

    private static void EmitInlineNode(Node node, RenderContext ctx, StringBuilder sb)
    {
        if (node is Text t)
        {
            sb.Append(EscapeInline(t.Data));
            return;
        }
        if (node is not Element e) return;

        switch (e.TagName)
        {
            case "strong":
            case "b":
                {
                    // Collect first; an empty wrapper would emit a
                    // stray "**" run that breaks downstream parsers.
                    var inner = new StringBuilder();
                    EmitInlineChildren(e, ctx, inner);
                    if (inner.Length == 0) return;
                    sb.Append("**").Append(inner).Append("**");
                }
                return;

            case "em":
            case "i":
                {
                    // Empty <i> elements are typically icon-font
                    // glyphs (Font Awesome <i class="fa-...">) — skip
                    // entirely rather than emit stray "*" markers.
                    var inner = new StringBuilder();
                    EmitInlineChildren(e, ctx, inner);
                    if (inner.Length == 0) return;
                    sb.Append('*').Append(inner).Append('*');
                }
                return;

            case "code":
                {
                    var content = e.TextContent;
                    if (content.Length == 0) return;
                    sb.Append('`').Append(content).Append('`');
                }
                return;

            case "a":
                {
                    var href = e.GetAttribute("href") ?? "";
                    var resolved = ResolveAbs(href, ctx.BaseUri);
                    sb.Append('[');
                    EmitInlineChildren(e, ctx, sb);
                    sb.Append("](").Append(resolved).Append(')');
                }
                return;

            case "img":
                {
                    var src = e.GetAttribute("src") ?? "";
                    var alt = (e.GetAttribute("alt") ?? "").Trim();
                    var resolved = ResolveAbs(src, ctx.BaseUri);
                    sb.Append('!').Append('[').Append(EscapeAlt(alt))
                        .Append("](").Append(resolved).Append(')');
                }
                return;

            case "br":
                sb.Append("  \n");
                return;

            case "span":
            case "u":
            case "small":
            case "sub":
            case "sup":
            case "mark":
            case "kbd":
                EmitInlineChildren(e, ctx, sb);
                return;
        }

        // Unknown inline tag: just walk its children's text.
        EmitInlineChildren(e, ctx, sb);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static void EmitLinePrefix(RenderContext ctx)
    {
        for (int i = 0; i < ctx.BlockQuoteDepth; i++) ctx.Output.Append("> ");
    }

    private static string ResolveAbs(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        try { return new Uri(baseUri, href).AbsoluteUri; }
        catch (UriFormatException) { return href; }
    }

    /// <summary>Escape characters that have a markdown meaning when
    /// inside running prose: <c>*</c>, <c>_</c>, <c>`</c>, <c>[</c>,
    /// <c>]</c>, <c>\</c>. We do not escape <c>#</c> mid-line because
    /// CommonMark only treats it as a heading at the start of a line.</summary>
    private static string EscapeInline(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '*' or '_' or '`' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Image alt text uses a stricter escape set — closing
    /// brackets and backslashes break the <c>![alt](src)</c> form.</summary>
    private static string EscapeAlt(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("]", "\\]").Replace("[", "\\[");
    }
}
