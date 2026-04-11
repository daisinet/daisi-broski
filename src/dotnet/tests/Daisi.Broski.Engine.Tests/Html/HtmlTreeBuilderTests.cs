using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

public class HtmlTreeBuilderTests
{
    // -------- document structure --------

    [Fact]
    public void Doctype_and_html_head_body_structure_is_built()
    {
        var doc = HtmlTreeBuilder.Parse("<!DOCTYPE html><html><head><title>hi</title></head><body>x</body></html>");

        Assert.NotNull(doc.Doctype);
        Assert.Equal("html", doc.Doctype!.Name);

        Assert.NotNull(doc.DocumentElement);
        Assert.Equal("html", doc.DocumentElement!.TagName);

        Assert.NotNull(doc.Head);
        Assert.NotNull(doc.Body);

        var title = doc.Head!.FirstElementChild;
        Assert.NotNull(title);
        Assert.Equal("title", title!.TagName);
        Assert.Equal("hi", title.TextContent);

        Assert.Equal("x", doc.Body!.TextContent);
    }

    [Fact]
    public void Implicit_html_head_body_synthesized_when_missing()
    {
        var doc = HtmlTreeBuilder.Parse("<p>just a paragraph</p>");

        Assert.NotNull(doc.DocumentElement);
        Assert.Equal("html", doc.DocumentElement!.TagName);
        Assert.NotNull(doc.Head);
        Assert.NotNull(doc.Body);

        var p = doc.Body!.FirstElementChild;
        Assert.NotNull(p);
        Assert.Equal("p", p!.TagName);
        Assert.Equal("just a paragraph", p.TextContent);
    }

    [Fact]
    public void Doctype_defaults_when_name_is_null()
    {
        var doc = HtmlTreeBuilder.Parse("<!DOCTYPE><p>hi</p>");
        Assert.NotNull(doc.Doctype);
        Assert.Equal("html", doc.Doctype!.Name);
    }

    // -------- attributes --------

    [Fact]
    public void Attributes_flow_from_token_to_element()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"/x\" class=\"primary\">link</a>");

        var a = doc.GetElementsByTagName("a").First();
        Assert.Equal("/x", a.GetAttribute("href"));
        Assert.Equal("primary", a.GetAttribute("class"));
        Assert.Equal("link", a.TextContent);
    }

    // -------- nesting --------

    [Fact]
    public void Nested_elements_build_a_tree()
    {
        var doc = HtmlTreeBuilder.Parse("<div><span>inner</span> outer</div>");

        var div = doc.GetElementsByTagName("div").First();
        Assert.Equal(2, div.ChildNodes.Count);
        var span = Assert.IsType<Element>(div.ChildNodes[0]);
        Assert.Equal("span", span.TagName);
        Assert.Equal("inner", span.TextContent);
        var text = Assert.IsType<Text>(div.ChildNodes[1]);
        Assert.Equal(" outer", text.Data);
    }

    [Fact]
    public void Consecutive_character_runs_merge_into_one_text_node()
    {
        var doc = HtmlTreeBuilder.Parse("<p>hello world</p>");

        var p = doc.GetElementsByTagName("p").First();
        Assert.Single(p.ChildNodes);
        var t = Assert.IsType<Text>(p.FirstChild);
        Assert.Equal("hello world", t.Data);
    }

    // -------- void elements --------

    [Fact]
    public void Void_elements_do_not_push_onto_stack()
    {
        var doc = HtmlTreeBuilder.Parse("<p>a<br>b<img src=\"x.png\">c</p>");

        var p = doc.GetElementsByTagName("p").First();
        // Expected children: "a", <br>, "b", <img>, "c"
        Assert.Equal(5, p.ChildNodes.Count);
        Assert.Equal("a", ((Text)p.ChildNodes[0]).Data);
        Assert.Equal("br", ((Element)p.ChildNodes[1]).TagName);
        Assert.Equal("b", ((Text)p.ChildNodes[2]).Data);
        Assert.Equal("img", ((Element)p.ChildNodes[3]).TagName);
        Assert.Equal("c", ((Text)p.ChildNodes[4]).Data);
    }

    [Fact]
    public void Img_attributes_round_trip()
    {
        var doc = HtmlTreeBuilder.Parse("<img src=\"/a.png\" alt=\"hi\">");

        var img = doc.GetElementsByTagName("img").First();
        Assert.Equal("/a.png", img.GetAttribute("src"));
        Assert.Equal("hi", img.GetAttribute("alt"));
    }

    // -------- implicit <p> closing --------

    [Fact]
    public void Block_level_start_tag_closes_open_paragraph()
    {
        var doc = HtmlTreeBuilder.Parse("<p>first<div>second</div>");

        var body = doc.Body!;
        var p = Assert.IsType<Element>(body.ChildNodes[0]);
        var div = Assert.IsType<Element>(body.ChildNodes[1]);
        Assert.Equal("p", p.TagName);
        Assert.Equal("div", div.TagName);
        Assert.Equal("first", p.TextContent);
        Assert.Equal("second", div.TextContent);
    }

    [Fact]
    public void Paragraph_implicitly_closes_previous_paragraph()
    {
        var doc = HtmlTreeBuilder.Parse("<p>one<p>two");

        var body = doc.Body!;
        Assert.Equal(2, body.Children.Count());
        var ps = body.Children.ToList();
        Assert.All(ps, el => Assert.Equal("p", el.TagName));
        Assert.Equal("one", ps[0].TextContent);
        Assert.Equal("two", ps[1].TextContent);
    }

    // -------- list and row implicit closes --------

    [Fact]
    public void Li_implicitly_closes_previous_li()
    {
        var doc = HtmlTreeBuilder.Parse("<ul><li>one<li>two<li>three</ul>");

        var ul = doc.GetElementsByTagName("ul").First();
        var lis = ul.Children.ToList();
        Assert.Equal(3, lis.Count);
        Assert.Equal("one", lis[0].TextContent);
        Assert.Equal("two", lis[1].TextContent);
        Assert.Equal("three", lis[2].TextContent);
    }

    // -------- misnested end tags --------

    [Fact]
    public void Unknown_end_tag_is_ignored()
    {
        var doc = HtmlTreeBuilder.Parse("<p>hello</q></p>");

        var p = doc.GetElementsByTagName("p").First();
        Assert.Equal("hello", p.TextContent);
    }

    [Fact]
    public void Misnested_end_tag_pops_open_ancestors_up_to_match()
    {
        // Phase-1 simplified adoption agency: </b> pops up to <b> and
        // everything above it on the stack. The resulting tree has
        // the <i> closed even though its own </i> wasn't seen.
        var doc = HtmlTreeBuilder.Parse("<p><b><i>text</b>after</p>");

        // At minimum: "text" is inside <b>/<i>, "after" is not.
        var p = doc.GetElementsByTagName("p").First();
        var text = p.TextContent;
        Assert.Contains("text", text);
        Assert.Contains("after", text);
    }

    // -------- script / style body through the tree builder --------

    [Fact]
    public void Script_body_is_preserved_as_text_node()
    {
        var doc = HtmlTreeBuilder.Parse("<html><body><script>if (a < b) {}</script></body></html>");

        var script = doc.GetElementsByTagName("script").First();
        Assert.Equal("if (a < b) {}", script.TextContent);
    }

    [Fact]
    public void Style_body_is_preserved_as_text_node()
    {
        var doc = HtmlTreeBuilder.Parse("<style>a > b { color: red; }</style><p>x</p>");

        var style = doc.GetElementsByTagName("style").First();
        Assert.Equal("a > b { color: red; }", style.TextContent);
    }

    [Fact]
    public void Title_body_decodes_entities_through_tree_builder()
    {
        var doc = HtmlTreeBuilder.Parse("<title>a &amp; b</title>");

        Assert.Equal("a & b", doc.Head!.FirstElementChild!.TextContent);
    }

    // -------- comments --------

    [Fact]
    public void Comments_are_attached_as_comment_nodes()
    {
        var doc = HtmlTreeBuilder.Parse("<div><!-- hi --><p>x</p></div>");

        var div = doc.GetElementsByTagName("div").First();
        Assert.Equal(2, div.ChildNodes.Count);
        var c = Assert.IsType<Comment>(div.ChildNodes[0]);
        Assert.Equal(" hi ", c.Data);
    }

    // -------- end-to-end realistic small document --------

    [Fact]
    public void Realistic_small_document_parses_correctly()
    {
        const string src = """
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8">
              <title>Daisi Broski Test</title>
              <link rel="stylesheet" href="/style.css">
            </head>
            <body>
              <h1 class="headline">Hello &amp; welcome</h1>
              <p id="intro">This is a <em>test</em> page.</p>
              <ul>
                <li>one
                <li>two
                <li>three
              </ul>
              <a href="/next">Next</a>
              <script>var x = 1;</script>
            </body>
            </html>
            """;

        var doc = HtmlTreeBuilder.Parse(src);

        Assert.NotNull(doc.Doctype);
        Assert.NotNull(doc.Head);
        Assert.NotNull(doc.Body);

        // Head has meta, title, link.
        Assert.NotNull(doc.Head!.Children.FirstOrDefault(e => e.TagName == "meta"));
        Assert.NotNull(doc.Head.Children.FirstOrDefault(e => e.TagName == "title"));
        Assert.NotNull(doc.Head.Children.FirstOrDefault(e => e.TagName == "link"));

        // Title text.
        var title = doc.Head.Children.First(e => e.TagName == "title");
        Assert.Equal("Daisi Broski Test", title.TextContent);

        // Body has h1, p, ul, a, script.
        Assert.NotNull(doc.Body!.Children.FirstOrDefault(e => e.TagName == "h1"));

        var h1 = doc.Body.Children.First(e => e.TagName == "h1");
        Assert.Equal("headline", h1.ClassName);
        Assert.Equal("Hello & welcome", h1.TextContent);

        var intro = doc.GetElementById("intro");
        Assert.NotNull(intro);
        Assert.Equal("p", intro!.TagName);
        Assert.Contains("test", intro.TextContent);

        var ul = doc.Body.Children.First(e => e.TagName == "ul");
        var lis = ul.Children.Where(e => e.TagName == "li").ToList();
        Assert.Equal(3, lis.Count);

        var link = doc.Body.Children.First(e => e.TagName == "a");
        Assert.Equal("/next", link.GetAttribute("href"));

        var script = doc.Body.Children.First(e => e.TagName == "script");
        Assert.Equal("var x = 1;", script.TextContent);
    }
}
