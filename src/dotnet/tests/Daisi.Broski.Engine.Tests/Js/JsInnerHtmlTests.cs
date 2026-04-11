using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-8: <c>element.innerHTML</c> read + write and
/// <c>element.outerHTML</c> read. Read path uses the new
/// <c>HtmlSerializer</c>; write path re-feeds the string
/// through the phase-1 <c>HtmlTreeBuilder</c> wrapped in
/// a minimal body envelope.
/// </summary>
public class JsInnerHtmlTests
{
    private static (JsEngine engine, Document doc) MakeEngineWithDocument()
    {
        var doc = new Document();
        var html_ = doc.CreateElement("html");
        doc.AppendChild(html_);
        var head = doc.CreateElement("head");
        html_.AppendChild(head);
        var body = doc.CreateElement("body");
        html_.AppendChild(body);
        var engine = new JsEngine();
        engine.AttachDocument(doc);
        return (engine, doc);
    }

    // ========================================================
    // innerHTML read
    // ========================================================

    [Fact]
    public void InnerHtml_reads_text_child()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateTextNode("hello"));
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "hello",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_reads_element_child_with_tag()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        var span = doc.CreateElement("span");
        span.AppendChild(doc.CreateTextNode("hi"));
        div.AppendChild(span);
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "<span>hi</span>",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_reads_attributes_on_children()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        var a = doc.CreateElement("a");
        a.SetAttribute("href", "/path");
        a.SetAttribute("class", "link");
        a.AppendChild(doc.CreateTextNode("click"));
        div.AppendChild(a);
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "<a href=\"/path\" class=\"link\">click</a>",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_emits_void_element_without_close()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var p = doc.CreateElement("p");
        p.AppendChild(doc.CreateElement("br"));
        doc.Body!.AppendChild(p);
        Assert.Equal(
            "<br>",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_emits_img_void_element_with_attrs()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var p = doc.CreateElement("p");
        var img = doc.CreateElement("img");
        img.SetAttribute("src", "/a.png");
        img.SetAttribute("alt", "a");
        p.AppendChild(img);
        doc.Body!.AppendChild(p);
        Assert.Equal(
            "<img src=\"/a.png\" alt=\"a\">",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_escapes_text_entities()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateTextNode("a < b & c > d"));
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "a &lt; b &amp; c &gt; d",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_escapes_attribute_quotes()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        var a = doc.CreateElement("a");
        a.SetAttribute("title", "he said \"hi\"");
        div.AppendChild(a);
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "<a title=\"he said &quot;hi&quot;\"></a>",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_preserves_script_content_unescaped()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var script = doc.CreateElement("script");
        // The tree builder puts script contents into a Text
        // node; the serializer must NOT escape < > inside it.
        script.AppendChild(doc.CreateTextNode("if (a < b) { c = 1; }"));
        doc.Body!.AppendChild(script);
        Assert.Equal(
            "if (a < b) { c = 1; }",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    [Fact]
    public void InnerHtml_empty_element_returns_empty_string()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "",
            eng.Evaluate("document.body.firstChild.innerHTML;"));
    }

    // ========================================================
    // outerHTML read
    // ========================================================

    [Fact]
    public void OuterHtml_includes_opening_and_closing_tag()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "main");
        div.AppendChild(doc.CreateTextNode("x"));
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "<div id=\"main\">x</div>",
            eng.Evaluate("document.body.firstChild.outerHTML;"));
    }

    [Fact]
    public void OuterHtml_for_void_element_has_no_closing_tag()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var br = doc.CreateElement("br");
        doc.Body!.AppendChild(br);
        Assert.Equal(
            "<br>",
            eng.Evaluate("document.body.firstChild.outerHTML;"));
    }

    // ========================================================
    // innerHTML write
    // ========================================================

    [Fact]
    public void InnerHtml_write_replaces_children()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateTextNode("old"));
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.innerHTML = '<span>new</span>';");
        // Backing DOM should have one element child <span>, with text "new".
        Assert.Single(div.ChildNodes);
        Assert.IsType<Element>(div.ChildNodes[0]);
        var span = (Element)div.ChildNodes[0];
        Assert.Equal("span", span.TagName);
        Assert.Equal("new", span.TextContent);
    }

    [Fact]
    public void InnerHtml_write_parses_attributes()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.innerHTML = '<a href=\"/x\" class=\"btn\">go</a>';");
        var a = (Element)div.ChildNodes[0];
        Assert.Equal("a", a.TagName);
        Assert.Equal("/x", a.GetAttribute("href"));
        Assert.Equal("btn", a.GetAttribute("class"));
    }

    [Fact]
    public void InnerHtml_write_parses_nested_elements()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.innerHTML = '<ul><li>a</li><li>b</li></ul>';");
        Assert.Single(div.ChildNodes);
        var ul = (Element)div.ChildNodes[0];
        Assert.Equal("ul", ul.TagName);
        Assert.Equal(2, ul.ChildNodes.Count);
        Assert.Equal("a", ul.ChildNodes[0].TextContent);
        Assert.Equal("b", ul.ChildNodes[1].TextContent);
    }

    [Fact]
    public void InnerHtml_empty_write_clears_children()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateElement("span"));
        div.AppendChild(doc.CreateTextNode("text"));
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.innerHTML = '';");
        Assert.Empty(div.ChildNodes);
    }

    [Fact]
    public void InnerHtml_write_preserves_subsequent_queries()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var container = doc.CreateElement("div");
        container.SetAttribute("id", "container");
        doc.Body!.AppendChild(container);
        eng.Evaluate(@"
            var c = document.getElementById('container');
            c.innerHTML = '<p class=""item"">one</p><p class=""item"">two</p>';
        ");
        Assert.Equal(
            2.0,
            eng.Evaluate("document.querySelectorAll('.item').length;"));
    }

    [Fact]
    public void InnerHtml_round_trip_through_script()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "<b>x</b>",
            eng.Evaluate(@"
                var d = document.createElement('div');
                document.body.appendChild(d);
                d.innerHTML = '<b>x</b>';
                d.innerHTML;
            "));
    }

    [Fact]
    public void InnerHtml_write_then_query_finds_new_nodes()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var container = doc.CreateElement("div");
        container.SetAttribute("id", "target");
        doc.Body!.AppendChild(container);
        eng.Evaluate(@"
            var t = document.getElementById('target');
            t.innerHTML = '<button id=""go"">Go</button>';
        ");
        Assert.Equal(
            "Go",
            eng.Evaluate("document.getElementById('go').textContent;"));
    }

    [Fact]
    public void InnerHtml_write_handles_entities()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.innerHTML = 'a &amp; b';");
        Assert.Equal("a & b", div.TextContent);
    }

    [Fact]
    public void OuterHtml_write_is_silently_ignored_in_this_slice()
    {
        // outerHTML write is deferred because it requires
        // reparenting the element within its current parent.
        // The write should be a no-op (not an error).
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateTextNode("original"));
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.outerHTML = '<span>new</span>';");
        Assert.Equal("original", div.TextContent);
    }
}
