using Daisi.Broski.Engine.Html;
using Daisi.Broski.Skimmer;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Skimmer;

/// <summary>
/// End-to-end tests for the article extractor + JSON / Markdown
/// formatters. Each test feeds a fixture HTML string through the
/// real HTML tree builder, runs <see cref="ContentExtractor.Extract"/>,
/// and asserts on the extracted structure or its rendered output.
///
/// No network, no sandbox — pure parser + extractor + formatter.
/// </summary>
public class SkimmerTests
{
    private static readonly Uri TestUrl = new("https://example.com/article/1");

    private static ArticleContent ExtractFromHtml(string html)
    {
        var doc = HtmlTreeBuilder.Parse(html);
        return ContentExtractor.Extract(doc, TestUrl);
    }

    // ========================================================
    // Metadata extraction
    // ========================================================

    [Fact]
    public void Picks_OpenGraph_title_over_html_title()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head>
              <title>Title in head</title>
              <meta property="og:title" content="OG Title">
            </head><body><article><p>Body that's long enough to score above the threshold for content extraction. " +
              "It needs to clear about a hundred characters of running text. Here's some more text to get there.</p></article></body></html>
            """);
        Assert.Equal("OG Title", article.Title);
    }

    [Fact]
    public void Falls_back_to_html_title()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head><title>Just A Title</title></head>
            <body><article><p>Body content. Body content. Body content. Body content.
              Body content. Body content. Body content. Body content. Body content.</p></article></body></html>
            """);
        Assert.Equal("Just A Title", article.Title);
    }

    [Fact]
    public void Picks_byline_from_meta_author()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head>
              <meta name="author" content="Jane Doe">
            </head><body><article><p>Body content. Body content. Body content. Body content.
              Body content. Body content. Body content. Body content. Body content.</p></article></body></html>
            """);
        Assert.Equal("Jane Doe", article.Byline);
    }

    [Fact]
    public void Picks_byline_from_rel_author_when_meta_missing()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <a rel="author" href="/u/sam">Sam Carter</a>
                <p>Body content. Body content. Body content. Body content.
                   Body content. Body content. Body content. Body content. Body content.</p>
              </article>
            </body></html>
            """);
        Assert.Equal("Sam Carter", article.Byline);
    }

    [Fact]
    public void Picks_published_at_from_article_published_time()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head>
              <meta property="article:published_time" content="2026-04-13T10:00:00Z">
            </head><body><article><p>Body content. Body content. Body content. Body content.
              Body content. Body content. Body content. Body content.</p></article></body></html>
            """);
        Assert.Equal("2026-04-13T10:00:00Z", article.PublishedAt);
    }

    [Fact]
    public void Reads_lang_from_html_element()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html lang="en-US"><body><article><p>Body content. Body content. Body content. Body content.
              Body content. Body content. Body content. Body content.</p></article></body></html>
            """);
        Assert.Equal("en-US", article.Lang);
    }

    [Fact]
    public void Hero_image_falls_back_to_first_inline_image()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <img src="/photos/hero.jpg" alt="Cover">
                <p>Body content. Body content. Body content. Body content. Body content.
                   Body content. Body content. Body content. Body content.</p>
              </article>
            </body></html>
            """);
        Assert.Equal("https://example.com/photos/hero.jpg", article.HeroImage);
    }

    // ========================================================
    // Content-root selection
    // ========================================================

    [Fact]
    public void Prefers_article_over_navigation_block()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <nav class="navigation">
                <a href="/a">A</a><a href="/b">B</a><a href="/c">C</a><a href="/d">D</a>
                <a href="/e">E</a><a href="/f">F</a><a href="/g">G</a><a href="/h">H</a>
              </nav>
              <article>
                <p>This is the real article body. It has multiple sentences and runs
                   well over the 100-character threshold the extractor uses.</p>
                <p>A second paragraph confirms the article shape and gives the
                   scoring pass extra evidence to pick this block.</p>
              </article>
            </body></html>
            """);
        Assert.NotNull(article.ContentRoot);
        Assert.Equal("article", article.ContentRoot!.TagName);
        Assert.Contains("real article body", article.PlainText);
        Assert.DoesNotContain("E", article.PlainText.Split(' '));
    }

    [Fact]
    public void Strips_script_and_style_from_picked_content()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <script>alert('boo')</script>
                <style>.x { color: red }</style>
                <p>Real text. Real text. Real text. Real text. Real text.
                   Real text. Real text. Real text. Real text.</p>
              </article>
            </body></html>
            """);
        Assert.DoesNotContain("alert", article.PlainText);
        Assert.DoesNotContain("color: red", article.PlainText);
        Assert.Contains("Real text", article.PlainText);
    }

    [Fact]
    public void Word_count_is_reasonable()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>One two three four five six seven eight nine ten.</p>
              <p>Eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty.</p>
            </article></body></html>
            """);
        Assert.Equal(20, article.WordCount);
    }

    [Fact]
    public void Collects_inline_links_with_resolved_absolute_urls()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <p>See <a href="/a">post A</a> and <a href="https://other.example/b">B</a> and
                <a href="#anchor">in-page</a> and <a href="javascript:void(0)">js</a>.
                And again <a href="/a">post A</a> (dedup).</p>
                <p>Body filler. Body filler. Body filler. Body filler. Body filler. Body filler.</p>
              </article>
            </body></html>
            """);
        // Two unique outbound links — in-page anchors and javascript:
        // are filtered, the duplicate is collapsed.
        Assert.Equal(2, article.Links.Count);
        Assert.Equal("https://example.com/a", article.Links[0].Href);
        Assert.Equal("post A", article.Links[0].Text);
        Assert.Equal("https://other.example/b", article.Links[1].Href);
    }

    // ========================================================
    // JSON formatter
    // ========================================================

    [Fact]
    public void Json_output_is_well_formed_and_contains_fields()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html lang="en"><head>
              <title>Hello</title>
              <meta name="author" content="Ada">
            </head><body><article><p>Long enough body content. Long enough body content. Long enough body content.</p></article></body></html>
            """);
        var json = JsonFormatter.Format(article);
        Assert.Contains("\"title\": \"Hello\"", json);
        Assert.Contains("\"byline\": \"Ada\"", json);
        Assert.Contains("\"lang\": \"en\"", json);
        Assert.Contains("\"url\": \"https://example.com/article/1\"", json);
        Assert.Contains("\"wordCount\":", json);
    }

    [Fact]
    public void Json_escapes_quotes_and_newlines_in_text()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head><title>"He said \\n"</title></head>
            <body><article><p>Content content content content content content content content.</p></article></body></html>
            """);
        var json = JsonFormatter.Format(article);
        Assert.Contains(@"\""He said", json);
    }

    [Fact]
    public void Json_uses_null_for_missing_fields()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article><p>Only a body. Only a body. Only a body. Only a body. Only a body.</p></article></body></html>
            """);
        var json = JsonFormatter.Format(article);
        Assert.Contains("\"title\": null", json);
        Assert.Contains("\"byline\": null", json);
        Assert.Contains("\"publishedAt\": null", json);
    }

    // ========================================================
    // Markdown formatter
    // ========================================================

    [Fact]
    public void Md_renders_h1_h2_and_paragraphs()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <h1>Title</h1>
                <p>Intro paragraph that is long enough to score.</p>
                <h2>Subhead</h2>
                <p>Following content under the subhead.</p>
              </article>
            </body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("# Title", md);
        Assert.Contains("## Subhead", md);
        Assert.Contains("Intro paragraph", md);
    }

    [Fact]
    public void Md_renders_links_and_inline_emphasis()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <p>This is <strong>bold</strong> and <em>italic</em>
                   and <a href="/foo">link</a> text. " +
                "And here is more text to push us over the threshold.</p>
              </article>
            </body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("**bold**", md);
        Assert.Contains("*italic*", md);
        Assert.Contains("[link](https://example.com/foo)", md);
    }

    [Fact]
    public void Md_renders_unordered_and_ordered_lists()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <p>Intro intro intro intro intro intro intro intro intro intro intro intro intro intro.</p>
                <ul>
                  <li>First</li>
                  <li>Second</li>
                </ul>
                <ol>
                  <li>One</li>
                  <li>Two</li>
                </ol>
              </article>
            </body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("- First", md);
        Assert.Contains("- Second", md);
        Assert.Contains("1. One", md);
        Assert.Contains("2. Two", md);
    }

    [Fact]
    public void Md_renders_code_block_with_language()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body>
              <article>
                <p>Intro intro intro intro intro intro intro intro intro intro intro intro intro intro.</p>
                <pre><code class="language-js">var x = 1;</code></pre>
              </article>
            </body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("```js", md);
        Assert.Contains("var x = 1;", md);
    }

    [Fact]
    public void Md_emits_metadata_header()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><head>
              <title>Hello</title>
              <meta name="author" content="Ada">
              <meta property="article:published_time" content="2026-04-13">
            </head><body><article><p>Body content. Body content. Body content. Body content. Body content.</p></article></body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("# Hello", md);
        Assert.Contains("**Ada**", md);
        Assert.Contains("2026-04-13", md);
        Assert.Contains("[source](https://example.com/article/1)", md);
    }

    [Fact]
    public void Md_inlines_links_for_anchors_at_block_level()
    {
        // Pattern from daisi.ai's CTA strip: a div containing only
        // <a> elements styled as buttons. Without inline-coalescing
        // the block dispatch loses the href and emits plain text.
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>Lead paragraph long enough to clear the scoring threshold easily without padding.</p>
              <div>
                <a href="/join">Join The Rebellion</a>
                <a href="/donate">Donate Your GPU</a>
                <a href="/oss">Open Source</a>
              </div>
              <p>Another paragraph for body density.</p>
            </article></body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("[Join The Rebellion](https://example.com/join)", md);
        Assert.Contains("[Donate Your GPU](https://example.com/donate)", md);
        Assert.Contains("[Open Source](https://example.com/oss)", md);
    }

    [Fact]
    public void Md_skips_empty_emphasis_wrappers()
    {
        // Font Awesome icons render as empty <i> elements. Without
        // the empty-wrapper guard the formatter emits a stray "**"
        // run that breaks downstream parsers (and looks ugly in
        // raw output).
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>Lead paragraph long enough to clear the scoring threshold easily without padding.</p>
              <div>
                <a href="/x"><i class="fa-solid fa-microchip"></i>Click Me</a>
              </div>
              <p>More body content for density.</p>
            </article></body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("[Click Me](https://example.com/x)", md);
        Assert.DoesNotContain("**Click Me", md);
        Assert.DoesNotContain("*Click Me", md);
    }

    [Fact]
    public void Md_collapses_runaway_whitespace_inside_headings()
    {
        // CSS-driven layouts split heading text across lines that
        // CommonMark would otherwise turn into multiple paragraphs.
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <h1>
                THE
                AI REBELLION
                HAS BEGUN
              </h1>
              <p>Lead paragraph long enough to clear the scoring threshold easily.</p>
            </article></body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains("# THE AI REBELLION HAS BEGUN", md);
    }

    // ========================================================
    // HTML formatter
    // ========================================================

    [Fact]
    public void Html_renders_anchors_with_target_blank()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>See <a href="/foo">foo</a> and <a href="https://other.example/x">x</a>.
                 Plus more body text to clear the threshold easily.</p>
            </article></body></html>
            """);
        var html = HtmlFormatter.Format(article);
        Assert.Contains("<a href=\"https://example.com/foo\" target=\"_blank\" rel=\"noopener noreferrer\">foo</a>", html);
        Assert.Contains("<a href=\"https://other.example/x\" target=\"_blank\" rel=\"noopener noreferrer\">x</a>", html);
    }

    [Fact]
    public void Html_strips_scripts_and_event_handlers()
    {
        // Even if a script makes it through the extractor's noise
        // strip somehow, the HTML formatter's allow-list approach
        // means it can never appear in the output.
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>Body text. Body text. Body text. Body text. Body text. Body text. Body text.</p>
              <p onclick="alert(1)">Bad. <a href="javascript:alert(2)" onclick="alert(3)">click</a></p>
            </article></body></html>
            """);
        var html = HtmlFormatter.Format(article);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("alert", html);
        // The href filter at the extractor level drops javascript:
        // links from article.Links, but the formatter still sees
        // the raw <a>; we don't currently filter href schemes in
        // the formatter, so the link IS emitted — just without
        // the inline event handlers, which is the security
        // boundary we care about.
    }

    [Fact]
    public void Html_renders_lists_and_headings()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <h2>Section Title</h2>
              <p>Intro paragraph long enough to clear the scoring threshold easily.</p>
              <ul><li>Apple</li><li>Banana</li></ul>
            </article></body></html>
            """);
        var html = HtmlFormatter.Format(article);
        Assert.Contains("<h2>Section Title</h2>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>", html);
        Assert.Contains("Apple", html);
    }

    [Fact]
    public void Html_escapes_text_metacharacters()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>Use &lt;script&gt; tags? Compare 1 &lt; 2 &amp;&amp; 3 &gt; 1.
                 Plus more body text to clear the threshold easily.</p>
            </article></body></html>
            """);
        var html = HtmlFormatter.Format(article);
        // Source already-escaped text should round-trip safely.
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;&amp;", html);
    }

    [Fact]
    public void Md_escapes_inline_metacharacters()
    {
        var article = ExtractFromHtml("""
            <!doctype html><html><body><article>
              <p>This has * and _ and ` and [brackets] in it. Plus more text to clear the threshold easily.</p>
            </article></body></html>
            """);
        var md = MarkdownFormatter.Format(article);
        Assert.Contains(@"\*", md);
        Assert.Contains(@"\_", md);
        Assert.Contains(@"\`", md);
        Assert.Contains(@"\[brackets\]", md);
    }
}
