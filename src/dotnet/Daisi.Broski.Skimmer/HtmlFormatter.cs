using System.Text;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// Render an <see cref="ArticleContent"/> as a sanitized HTML
/// fragment suitable for in-app display (e.g. the Surfer's Reader
/// view via Blazor's <c>MarkupString</c>). The output preserves
/// inline markup the Markdown / plain-text formatters drop —
/// hyperlinks, emphasis, inline code, lists, headings — while
/// stripping every script / style / event-handler attribute that
/// could execute in the host page.
///
/// <para>
/// We build the HTML ourselves rather than re-serializing the
/// extracted DOM directly so the tag set + attribute set are an
/// allow-list, not a deny-list: anything we don't explicitly emit
/// can't appear in the output.
/// </para>
/// </summary>
public static class HtmlFormatter
{
    /// <summary>Render the article body as a self-contained HTML
    /// fragment. Resolves all <c>href</c> / <c>src</c> attributes
    /// against <see cref="ArticleContent.Url"/> so the output is
    /// safe to drop into a host page at a different origin.</summary>
    public static string Format(ArticleContent article)
    {
        ArgumentNullException.ThrowIfNull(article);

        var sb = new StringBuilder();
        var ctx = new RenderContext(article.Url, sb);

        // Nav-links table at the top — survives even if the
        // article body is empty (e.g. extraction failed cleanly
        // and only metadata is available).
        AppendNavTable(sb, article);

        if (article.ContentRoot is not null)
        {
            RenderBlock(article.ContentRoot, ctx);
        }
        return sb.ToString();
    }

    /// <summary>Emit a simple two-column <c>&lt;table class="nav-links"&gt;</c>
    /// listing the page's nav links. Same allow-list escaping as
    /// the rest of the formatter; anchors get <c>target="_blank"</c>
    /// + <c>rel="noopener noreferrer"</c> for safe render inside a
    /// reader shell.</summary>
    private static void AppendNavTable(StringBuilder sb, ArticleContent article)
    {
        if (article.NavLinks.Count == 0) return;
        sb.Append("<h2>Navigation</h2>");
        sb.Append("<table class=\"nav-links\"><thead><tr><th>Link</th><th>URL</th></tr></thead><tbody>");
        foreach (var link in article.NavLinks)
        {
            var resolved = EscapeAttr(link.Href);
            sb.Append("<tr><td><a href=\"").Append(resolved)
              .Append("\" title=\"").Append(resolved)
              .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            var textSb = new StringBuilder();
            AppendEscapedText(link.Text, textSb);
            sb.Append(textSb);
            sb.Append("</a></td><td><code>").Append(resolved).Append("</code></td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private sealed class RenderContext
    {
        public StringBuilder Output { get; }
        public Uri BaseUri { get; }
        public RenderContext(Uri baseUri, StringBuilder output)
        {
            BaseUri = baseUri;
            Output = output;
        }
    }

    private static void RenderBlock(Element el, RenderContext ctx)
    {
        switch (el.TagName)
        {
            case "h1": case "h2": case "h3":
            case "h4": case "h5": case "h6":
                EmitWrappedInline(el, ctx, el.TagName);
                return;

            case "p":
                EmitWrappedInline(el, ctx, "p");
                return;

            case "ul":
            case "ol":
                EmitList(el, ctx);
                return;

            case "li":
                EmitWrappedInline(el, ctx, "li");
                return;

            case "blockquote":
                ctx.Output.Append("<blockquote>");
                RenderBlockChildren(el, ctx);
                ctx.Output.Append("</blockquote>");
                return;

            case "pre":
                EmitCodeBlock(el, ctx);
                return;

            case "hr":
                ctx.Output.Append("<hr />");
                return;

            case "img":
                EmitImage(el, ctx);
                return;

            case "figure":
            case "figcaption":
                ctx.Output.Append('<').Append(el.TagName).Append('>');
                RenderBlockChildren(el, ctx);
                ctx.Output.Append("</").Append(el.TagName).Append('>');
                return;

            case "br":
                ctx.Output.Append("<br />");
                return;

            case "table":
            case "thead":
            case "tbody":
            case "tr":
            case "td":
            case "th":
                ctx.Output.Append('<').Append(el.TagName).Append('>');
                RenderBlockChildren(el, ctx);
                ctx.Output.Append("</").Append(el.TagName).Append('>');
                return;

            case "article": case "section": case "main": case "div":
            case "header": case "footer": case "details": case "summary":
                RenderBlockChildren(el, ctx);
                return;
        }

        // Inline tags appearing at block level — wrap in a paragraph.
        if (IsInlineTag(el.TagName))
        {
            ctx.Output.Append("<p>");
            EmitInline(el, ctx);
            ctx.Output.Append("</p>");
        }
        else
        {
            // Unknown block tag — recurse into children.
            RenderBlockChildren(el, ctx);
        }
    }

    /// <summary>Walk children, coalescing inline runs into paragraphs
    /// and dispatching block elements through <see cref="RenderBlock"/>.
    /// Mirrors the Markdown formatter's strategy.</summary>
    private static void RenderBlockChildren(Element el, RenderContext ctx)
    {
        bool inlineOpen = false;

        void OpenInline()
        {
            if (!inlineOpen) { ctx.Output.Append("<p>"); inlineOpen = true; }
        }
        void CloseInline()
        {
            if (inlineOpen) { ctx.Output.Append("</p>"); inlineOpen = false; }
        }

        foreach (var child in el.ChildNodes)
        {
            if (child is Text t)
            {
                if (string.IsNullOrWhiteSpace(t.Data)) continue;
                OpenInline();
                AppendEscapedText(t.Data, ctx.Output);
                continue;
            }
            if (child is not Element c) continue;

            if (IsInlineTag(c.TagName))
            {
                OpenInline();
                EmitInline(c, ctx);
            }
            else
            {
                CloseInline();
                RenderBlock(c, ctx);
            }
        }
        CloseInline();
    }

    private static void EmitWrappedInline(Element el, RenderContext ctx, string wrapTag)
    {
        // Build inline content first so we can elide the wrapper
        // entirely if it's empty (matches the Markdown formatter's
        // "skip empty <i>" behavior).
        var saved = ctx.Output.Length;
        ctx.Output.Append('<').Append(wrapTag).Append('>');
        int contentStart = ctx.Output.Length;

        foreach (var child in el.ChildNodes)
        {
            if (child is Text t)
            {
                AppendEscapedText(t.Data, ctx.Output);
            }
            else if (child is Element c)
            {
                EmitInline(c, ctx);
            }
        }

        if (ctx.Output.Length == contentStart)
        {
            // Nothing was produced — back out the open tag.
            ctx.Output.Length = saved;
            return;
        }

        ctx.Output.Append("</").Append(wrapTag).Append('>');
    }

    private static void EmitList(Element el, RenderContext ctx)
    {
        ctx.Output.Append('<').Append(el.TagName).Append('>');
        foreach (var li in el.Children)
        {
            if (li.TagName != "li") continue;
            // List items can contain block content (nested lists,
            // paragraphs) — render through the block walker rather
            // than the inline-only emitter.
            ctx.Output.Append("<li>");
            RenderBlockChildren(li, ctx);
            ctx.Output.Append("</li>");
        }
        ctx.Output.Append("</").Append(el.TagName).Append('>');
    }

    private static void EmitCodeBlock(Element el, RenderContext ctx)
    {
        ctx.Output.Append("<pre><code>");
        AppendEscapedText(el.TextContent, ctx.Output);
        ctx.Output.Append("</code></pre>");
    }

    private static void EmitImage(Element el, RenderContext ctx)
    {
        var src = el.GetAttribute("src") ??
                  el.GetAttribute("data-src") ??
                  el.GetAttribute("data-original");
        if (string.IsNullOrWhiteSpace(src)) return;
        var resolved = ResolveAbs(src, ctx.BaseUri);
        var alt = el.GetAttribute("alt") ?? "";
        ctx.Output.Append("<img src=\"")
            .Append(EscapeAttr(resolved))
            .Append("\" alt=\"")
            .Append(EscapeAttr(alt))
            .Append("\" />");
    }

    // -------------------------------------------------------------------
    // Inline emission
    // -------------------------------------------------------------------

    private static void EmitInline(Element el, RenderContext ctx)
    {
        switch (el.TagName)
        {
            case "a":
                {
                    var href = el.GetAttribute("href") ?? "";
                    var resolved = ResolveAbs(href, ctx.BaseUri);
                    // Defense-in-depth: drop the href entirely for
                    // schemes that can execute or compromise the
                    // host (javascript:, data:, vbscript:, file:).
                    // The link text still appears, just without an
                    // href — clicking is a no-op rather than a
                    // potential XSS / file-disclosure vector.
                    if (!IsSafeHrefScheme(resolved))
                    {
                        ctx.Output.Append("<span>");
                        EmitInlineChildren(el, ctx);
                        ctx.Output.Append("</span>");
                        return;
                    }
                    // title="full url" gives the host UI a free
                    // hover-tooltip showing the resolved target —
                    // matches the address-bar preview a real
                    // browser shows on link hover.
                    var attr = EscapeAttr(resolved);
                    ctx.Output.Append("<a href=\"")
                        .Append(attr)
                        .Append("\" title=\"")
                        .Append(attr)
                        .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
                    EmitInlineChildren(el, ctx);
                    ctx.Output.Append("</a>");
                }
                return;

            case "img":
                EmitImage(el, ctx);
                return;

            case "br":
                ctx.Output.Append("<br />");
                return;

            case "strong": case "b":
            case "em":     case "i":
            case "code":
            case "small":  case "sub": case "sup":
            case "mark":   case "u":   case "kbd":
            case "abbr":   case "cite": case "q":
            case "time":   case "var":
                {
                    // Skip if empty — same icon-font defense as the
                    // Markdown formatter.
                    int saved = ctx.Output.Length;
                    string tag = el.TagName;
                    ctx.Output.Append('<').Append(tag).Append('>');
                    int contentStart = ctx.Output.Length;
                    EmitInlineChildren(el, ctx);
                    if (ctx.Output.Length == contentStart)
                    {
                        ctx.Output.Length = saved;
                        return;
                    }
                    ctx.Output.Append("</").Append(tag).Append('>');
                }
                return;

            case "span":
                EmitInlineChildren(el, ctx);
                return;
        }

        // Unknown inline tag: just walk children's text.
        EmitInlineChildren(el, ctx);
    }

    private static void EmitInlineChildren(Element el, RenderContext ctx)
    {
        foreach (var child in el.ChildNodes)
        {
            if (child is Text t)
            {
                AppendEscapedText(t.Data, ctx.Output);
            }
            else if (child is Element c)
            {
                EmitInline(c, ctx);
            }
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static bool IsInlineTag(string tag) => tag switch
    {
        "a" or "strong" or "b" or "em" or "i" or "code" or "span" or
        "small" or "sub" or "sup" or "mark" or "kbd" or "u" or
        "img" or "br" or "abbr" or "cite" or "q" or "time" or "var"
            => true,
        _ => false,
    };

    private static string ResolveAbs(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        try { return new Uri(baseUri, href).AbsoluteUri; }
        catch (UriFormatException) { return href; }
    }

    /// <summary>Allow-list of href schemes safe to render. Anything
    /// not in this set is dropped (the link text survives but the
    /// href doesn't), which kills the obvious XSS vectors at the
    /// formatter boundary regardless of whether the upstream
    /// extractor caught them.</summary>
    private static bool IsSafeHrefScheme(string href)
    {
        if (string.IsNullOrEmpty(href)) return false;
        return href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith('#') ||
               href.StartsWith('/');
    }

    /// <summary>Escape running text for HTML body context. We emit
    /// the minimal set needed to prevent tag-injection in textual
    /// position; attribute escaping uses <see cref="EscapeAttr"/>.</summary>
    private static void AppendEscapedText(string s, StringBuilder sb)
    {
        if (string.IsNullOrEmpty(s)) return;
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>Escape an attribute value. Stricter than text — we
    /// also escape the quote chars so callers can use either kind
    /// of delimiter without breaking the attribute.</summary>
    private static string EscapeAttr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
