using System.Text;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// Heuristic main-content extractor — same problem space as Mozilla's
/// Readability and arc90's original "make a reader mode" algorithm,
/// but trimmed to what's actually necessary against the kinds of pages
/// we care about (news, blog posts, docs, marketing pages with a hero
/// + description).
///
/// <para>
/// The pass is a single document walk:
/// <list type="number">
/// <item>Pull metadata off the head: title, byline, date, language,
///   description, site name, hero image.</item>
/// <item>Score every <c>div</c> / <c>article</c> / <c>main</c> /
///   <c>section</c> by text density — text length minus the text inside
///   anchors, weighted by paragraph count, with bonuses for semantic
///   landmarks (<c>article</c> / <c>main</c>) and penalties for
///   noise-class names (<c>nav</c>, <c>sidebar</c>, <c>footer</c>,
///   <c>comments</c>, <c>related</c>).</item>
/// <item>Pick the highest-scoring element. Strip <c>script</c> /
///   <c>style</c> / <c>nav</c> / <c>aside</c> / <c>iframe</c> /
///   form elements from the chosen subtree before returning.</item>
/// </list>
/// The walk is O(n) over elements; no regex or HTML re-serialization
/// is involved on the hot path.
/// </para>
///
/// <para>
/// We don't try to handle every site — the goal is "good enough on the
/// 90% case", and the extractor exposes its scoring intermediate state
/// only via the picked <see cref="ArticleContent.ContentRoot"/> so
/// callers can re-render in their own format (the markdown / json
/// formatters in this project are just two consumers).
/// </para>
/// </summary>
public static class ContentExtractor
{
    /// <summary>Extract the article body + metadata from a parsed
    /// document. <paramref name="pageUrl"/> is used to resolve relative
    /// image / link targets and to populate
    /// <see cref="ArticleContent.Url"/>.</summary>
    public static ArticleContent Extract(Document document, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageUrl);

        var head = document.Head;

        string? title = ReadMeta(head, "og:title")
                     ?? ReadMeta(head, "twitter:title")
                     ?? document.QuerySelector("title")?.TextContent.Trim();

        string? description = ReadMeta(head, "og:description")
                          ?? ReadMeta(head, "twitter:description")
                          ?? ReadMeta(head, "description");

        string? siteName = ReadMeta(head, "og:site_name") ?? pageUrl.Host;

        string? byline = ReadMeta(head, "article:author")
                     ?? ReadMeta(head, "author")
                     ?? FindBylineInBody(document);

        string? publishedAt = ReadMeta(head, "article:published_time")
                          ?? ReadMeta(head, "datePublished")
                          ?? ReadMeta(head, "date")
                          ?? FindFirstTimeAttribute(document);

        string? lang = document.DocumentElement?.GetAttribute("lang");

        string? heroImage = ReadMeta(head, "og:image")
                         ?? ReadMeta(head, "twitter:image");

        // Collect nav links BEFORE the noise-strip pass runs —
        // that pass removes <nav> elements from the content root,
        // and when the content root is the body, those nav nodes
        // are the only ones in the document. Collecting here keeps
        // them available as a separate sitemap-style list even
        // though the body text excludes them.
        var navLinks = CollectNavLinks(document, pageUrl);

        var contentRoot = PickContentRoot(document);
        if (contentRoot is not null)
        {
            // Strip noise from a clone before measuring text. We
            // mutate `contentRoot` in place — the document is owned
            // by the caller but the extractor's contract is "give me
            // the article content"; nav and script residue inside
            // the picked subtree is never useful.
            StripNoise(contentRoot);
        }

        var (plainText, wordCount) = RenderPlainText(contentRoot);

        var images = CollectImages(contentRoot, pageUrl);
        var links = CollectLinks(contentRoot, pageUrl);

        // If we still don't have a hero image but the content root
        // had one, promote the first inline image. Saves consumers
        // from re-walking just to find a card image.
        if (heroImage is null && images.Count > 0)
        {
            heroImage = images[0].Src;
        }

        // Normalize the hero image to an absolute URL — meta tags
        // sometimes emit a relative path.
        heroImage = ResolveAbsolute(heroImage, pageUrl);

        return new ArticleContent
        {
            Url = pageUrl,
            Title = Trim(title),
            Byline = Trim(byline),
            PublishedAt = Trim(publishedAt),
            Lang = Trim(lang),
            Description = Trim(description),
            SiteName = Trim(siteName),
            HeroImage = heroImage,
            ContentRoot = contentRoot,
            PlainText = plainText,
            WordCount = wordCount,
            Images = images,
            Links = links,
            NavLinks = navLinks,
        };
    }

    /// <summary>Walk every <c>&lt;nav&gt;</c> element (and
    /// <c>[role=navigation]</c> container) in the document,
    /// collecting anchor <c>href</c>s. Deduped by resolved URL,
    /// in document order, skipping in-page / <c>javascript:</c> /
    /// <c>mailto:</c> hrefs and empty-text decorative links.</summary>
    private static List<ExtractedLink> CollectNavLinks(Document document, Uri baseUri)
    {
        var result = new List<ExtractedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var navs = new List<Element>();
        foreach (var el in document.DescendantElements())
        {
            if (el.TagName == "nav") navs.Add(el);
            else if (el.GetAttribute("role") == "navigation") navs.Add(el);
        }
        if (navs.Count == 0) return result;

        foreach (var nav in navs)
        {
            foreach (var a in nav.DescendantElements())
            {
                if (a.TagName != "a") continue;
                var href = a.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (href.StartsWith('#') ||
                    href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var resolved = ResolveAbsolute(href, baseUri);
                if (resolved is null) continue;
                if (!seen.Add(resolved)) continue;

                // Prefer the text content; fall back to aria-label /
                // title / alt (for icon-only links) so we never emit
                // a row with a blank label.
                var text = CollapseInlineWhitespace(a.TextContent);
                if (text.Length == 0)
                {
                    text = a.GetAttribute("aria-label")?.Trim() ?? "";
                }
                if (text.Length == 0)
                {
                    text = a.GetAttribute("title")?.Trim() ?? "";
                }
                if (text.Length == 0)
                {
                    // Icon-only link — try the first <img alt>.
                    foreach (var child in a.DescendantElements())
                    {
                        if (child.TagName == "img")
                        {
                            text = child.GetAttribute("alt")?.Trim() ?? "";
                            if (text.Length > 0) break;
                        }
                    }
                }
                if (text.Length == 0) continue;

                result.Add(new ExtractedLink(resolved, text));
            }
        }
        return result;
    }

    /// <summary>Collapse runs of whitespace (spaces, tabs, newlines)
    /// to single spaces for inline labels. Used by nav-link
    /// extraction to keep table rows from wrapping.</summary>
    private static string CollapseInlineWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
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
        return sb.ToString().Trim();
    }

    // -------------------------------------------------------------------
    // Metadata helpers
    // -------------------------------------------------------------------

    /// <summary>Read a <c>&lt;meta&gt;</c> tag from the head whose
    /// <c>name</c>, <c>property</c>, or <c>itemprop</c> attribute equals
    /// <paramref name="key"/> (case-insensitive). Returns the trimmed
    /// <c>content</c> attribute or <c>null</c>.</summary>
    private static string? ReadMeta(Element? head, string key)
    {
        if (head is null) return null;
        foreach (var meta in head.Children)
        {
            if (meta.TagName != "meta") continue;
            string? name = meta.GetAttribute("name") ??
                           meta.GetAttribute("property") ??
                           meta.GetAttribute("itemprop");
            if (name is null) continue;
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                var content = meta.GetAttribute("content");
                if (!string.IsNullOrWhiteSpace(content))
                    return content.Trim();
            }
        }
        return null;
    }

    private static string? FindBylineInBody(Document document)
    {
        // Common byline placements: rel=author link, .byline / .author
        // class names, [itemprop=author].
        foreach (var sel in new[] {
            "a[rel=author]",
            "[itemprop=author]",
            ".byline",
            ".author",
            ".post-author",
            ".article-author",
        })
        {
            var hit = document.QuerySelector(sel);
            if (hit is not null)
            {
                var t = hit.TextContent.Trim();
                if (t.Length > 0 && t.Length < 200) return t;
            }
        }
        return null;
    }

    private static string? FindFirstTimeAttribute(Document document)
    {
        // Prefer a `<time datetime="...">` element — it's the
        // semantic answer.
        foreach (var t in document.GetElementsByTagName("time"))
        {
            var dt = t.GetAttribute("datetime");
            if (!string.IsNullOrWhiteSpace(dt)) return dt.Trim();
        }
        return null;
    }

    // -------------------------------------------------------------------
    // Content-root selection
    // -------------------------------------------------------------------

    /// <summary>Walk the body and pick the element most likely to
    /// contain the article body. See class-level docs for the scoring
    /// model.</summary>
    private static Element? PickContentRoot(Document document)
    {
        var body = document.Body ?? document.DocumentElement;
        if (body is null) return null;

        // Fast paths for sites with semantic markup. We still score
        // them because some pages have multiple <article> elements
        // (e.g. an index page) and we want the largest.
        Element? best = null;
        double bestScore = double.NegativeInfinity;

        foreach (var candidate in body.DescendantElements())
        {
            if (!IsContentCandidate(candidate)) continue;
            double score = ScoreCandidate(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        // Fall back to the body if nothing scored well — better to
        // dump everything than to drop the page on the floor.
        return best ?? body;
    }

    private static bool IsContentCandidate(Element el) => el.TagName switch
    {
        "article" => true,
        "main" => true,
        "section" => true,
        "div" => true,
        // <body> is itself a fallback; we score it implicitly via the
        // descendant walk by including it.
        "body" => true,
        _ => false,
    };

    /// <summary>Score an element's likelihood of being the article body.
    /// Higher is better. Negative scores mean "almost certainly not it".</summary>
    private static double ScoreCandidate(Element el)
    {
        int textLen = 0;
        int linkTextLen = 0;
        int paragraphCount = 0;
        int headingCount = 0;
        int cardLikeChildCount = 0;

        foreach (var node in el.DescendantsAndSelf())
        {
            if (node is Text t)
            {
                int len = t.Data.Length;
                textLen += len;
                if (HasAncestorTag(t, "a", upTo: el)) linkTextLen += len;
            }
            else if (node is Element e)
            {
                if (e.TagName == "p") paragraphCount++;
                if (e.TagName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                    headingCount++;
                // "Card-like" descendants: an <a> styled as a card,
                // or any element whose class explicitly tags it as
                // a card / tile. Detected anywhere in the subtree
                // (not just direct children) because Bootstrap-style
                // grids wrap each card in an extra col-md-* div.
                if (HasCardLikeClass(e.ClassName) ||
                    (e.TagName == "a" && HasCardLikeClass(e.ClassName)))
                {
                    cardLikeChildCount++;
                }
            }
        }

        // Trivially short = no chance.
        if (textLen < 100) return double.NegativeInfinity;

        double linkDensity = textLen == 0 ? 0 : (double)linkTextLen / textLen;
        bool isCardGrid = cardLikeChildCount >= 3 && headingCount >= 3;

        // Link-density penalty: a nav-heavy block has high text
        // length but most of it is anchor text. Real article bodies
        // are typically <30% link text. Card-grid index pages
        // (docs landings, store fronts) get a relaxed cap because
        // the cards ARE the content even though every card is an
        // anchor. Anything above 95% is still rejected as nav noise.
        double maxLinkDensity = isCardGrid ? 0.95 : 0.5;
        if (linkDensity > maxLinkDensity) return double.NegativeInfinity;

        // Score: text density × non-link weight, plus structural
        // bonuses for paragraphs, headings, and detected card grids.
        double score = textLen * (1.0 - linkDensity)
            + 30.0 * paragraphCount
            + 20.0 * headingCount;

        // Card-grid bonus: an index page where each item is a small
        // <a class="card"> shouldn't lose to a chatty footer just
        // because the footer happens to have more raw text.
        if (isCardGrid)
        {
            score += 500.0 * cardLikeChildCount;
        }

        // Semantic-tag bonus.
        if (el.TagName is "article" or "main") score *= 1.5;

        // Class / id name signals.
        string id = el.Id;
        string cls = el.ClassName;
        if (HasNoiseToken(id) || HasNoiseToken(cls)) score -= 2000;
        if (HasContentToken(id) || HasContentToken(cls)) score += 1000;
        // role=main / role=article are spec-blessed signals.
        var role = el.GetAttribute("role");
        if (role is "main" or "article") score *= 1.4;
        // Hidden / display:none content shouldn't win.
        if (el.GetAttribute("aria-hidden") == "true" ||
            el.GetAttribute("hidden") is not null)
        {
            score -= 5000;
        }

        return score;
    }

    /// <summary>True if the class name contains a token that
    /// suggests a "card" grid item — Bootstrap's <c>card</c>,
    /// frameworks' <c>tile</c>, custom <c>*-card</c> /
    /// <c>*-tile</c> patterns. Used to detect card-grid layouts so
    /// the link-density rejection doesn't drop docs index pages on
    /// the floor.</summary>
    private static bool HasCardLikeClass(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var token in s.Split(
                     [' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("card", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("tile", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("-card", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("-tile", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("card-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasNoiseToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var token in new[] {
            "nav", "navbar", "navigation", "sidebar", "side-bar",
            "footer", "header", "menu", "breadcrumb", "pagination",
            "share", "social", "comments", "comment-",
            "related", "recommended", "trending", "popular",
            "advert", "ads-", "promo", "newsletter", "subscribe",
            "cookie", "consent", "gdpr", "popup", "modal",
            "search-results", "skip-link", "hidden",
        })
        {
            if (s.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasContentToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var token in new[] {
            "article", "post", "story", "entry", "content", "main",
            "body-text", "post-body", "article-body", "story-body",
            "blog-post", "markdown",
        })
        {
            if (s.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasAncestorTag(Node node, string tagName, Element upTo)
    {
        for (Node? cur = node.ParentNode; cur is not null; cur = cur.ParentNode)
        {
            if (ReferenceEquals(cur, upTo)) return false;
            if (cur is Element e && e.TagName == tagName) return true;
        }
        return false;
    }

    // -------------------------------------------------------------------
    // Noise stripping
    // -------------------------------------------------------------------

    private static readonly HashSet<string> StripTags = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "iframe", "svg",
        "button", "form", "input", "select", "textarea",
        "nav", "aside",
        // Common but rarely useful inside an article body.
        "template",
    };

    private static void StripNoise(Element root)
    {
        // Walk depth-first and remove nodes whose tag matches the
        // strip set. We snapshot first because we're going to mutate
        // the tree we're iterating.
        var doomed = new List<Element>();
        foreach (var el in root.DescendantElements())
        {
            if (StripTags.Contains(el.TagName)) doomed.Add(el);
            else if (HasNoiseToken(el.ClassName) || HasNoiseToken(el.Id))
            {
                doomed.Add(el);
            }
        }
        foreach (var d in doomed)
        {
            // The parent may already be doomed (and detached) — guard.
            if (d.ParentNode is null) continue;
            d.ParentNode.RemoveChild(d);
        }
    }

    // -------------------------------------------------------------------
    // Plain text + image / link collection
    // -------------------------------------------------------------------

    private static (string Text, int WordCount) RenderPlainText(Element? root)
    {
        if (root is null) return ("", 0);

        var sb = new StringBuilder();
        AppendPlainText(root, sb);
        var raw = sb.ToString();

        // Collapse whitespace runs but preserve paragraph breaks
        // (double newlines).
        var collapsed = CollapseWhitespace(raw);
        int wc = CountWords(collapsed);
        return (collapsed, wc);
    }

    private static void AppendPlainText(Node node, StringBuilder sb)
    {
        if (node is Text t)
        {
            sb.Append(t.Data);
            return;
        }
        if (node is not Element e) return;

        // Block-level elements force a paragraph break around them so
        // the rendered output reads like prose, not concatenation.
        bool isBlock = IsBlockLevel(e.TagName);
        if (isBlock) sb.Append("\n\n");
        if (e.TagName == "br") sb.Append('\n');

        foreach (var child in e.ChildNodes)
        {
            AppendPlainText(child, sb);
        }

        if (isBlock) sb.Append("\n\n");
    }

    private static bool IsBlockLevel(string tag) => tag switch
    {
        "p" or "div" or "article" or "section" or "main" or "header" or
        "footer" or "aside" or "nav" or "blockquote" or "pre" or "li" or
        "ul" or "ol" or "dl" or "dt" or "dd" or "h1" or "h2" or "h3" or
        "h4" or "h5" or "h6" or "table" or "thead" or "tbody" or "tr" or
        "td" or "th" or "figure" or "figcaption" or "hr" or "details" or
        "summary" => true,
        _ => false,
    };

    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        bool inWhitespaceRun = false;
        int consecutiveNewlines = 0;
        foreach (var c in s)
        {
            if (c == '\n')
            {
                consecutiveNewlines++;
                inWhitespaceRun = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                inWhitespaceRun = true;
                continue;
            }
            // Flush pending whitespace.
            if (inWhitespaceRun && sb.Length > 0)
            {
                sb.Append(consecutiveNewlines >= 2 ? "\n\n" : " ");
            }
            sb.Append(c);
            inWhitespaceRun = false;
            consecutiveNewlines = 0;
        }
        return sb.ToString().Trim();
    }

    private static int CountWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        int count = 0;
        bool inWord = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (inWord) { count++; inWord = false; }
            }
            else inWord = true;
        }
        if (inWord) count++;
        return count;
    }

    private static List<ExtractedImage> CollectImages(Element? root, Uri baseUri)
    {
        var result = new List<ExtractedImage>();
        if (root is null) return result;
        foreach (var img in root.DescendantElements())
        {
            if (img.TagName != "img") continue;
            var src = img.GetAttribute("src") ??
                      img.GetAttribute("data-src") ??
                      img.GetAttribute("data-original");
            if (string.IsNullOrWhiteSpace(src)) continue;
            var resolved = ResolveAbsolute(src, baseUri);
            if (resolved is null) continue;
            var alt = img.GetAttribute("alt") ?? "";
            result.Add(new ExtractedImage(resolved, alt));
        }
        return result;
    }

    private static List<ExtractedLink> CollectLinks(Element? root, Uri baseUri)
    {
        var result = new List<ExtractedLink>();
        if (root is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in root.DescendantElements())
        {
            if (a.TagName != "a") continue;
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            // Skip in-page anchors and javascript: URIs — they're
            // navigation noise, not outbound references.
            if (href.StartsWith('#') ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var resolved = ResolveAbsolute(href, baseUri);
            if (resolved is null) continue;
            if (!seen.Add(resolved)) continue;
            var text = CollapseWhitespace(a.TextContent);
            if (text.Length == 0) continue;
            result.Add(new ExtractedLink(resolved, text));
        }
        return result;
    }

    private static string? ResolveAbsolute(string? href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            return new Uri(baseUri, href).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim();
    }
}
