using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Js.Dom;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-2: the JS → C# DOM bridge. Each test constructs
/// a small DOM on the C# side, attaches it to a fresh
/// <see cref="JsEngine"/>, runs a script that touches the
/// <c>document</c> global, and asserts on either the script's
/// return value or the resulting DOM state.
///
/// The tests deliberately split read-path (`script asks the
/// DOM what's there`) from write-path (`script mutates the
/// DOM, C# observes the change`) so regressions in either
/// direction surface independently.
/// </summary>
public class JsDomBridgeTests
{
    private static (JsEngine engine, Document doc) MakeEngineWithDocument(string html = "")
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
    // document global + basic accessors
    // ========================================================

    [Fact]
    public void Document_global_is_wired()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("object", eng.Evaluate("typeof document;"));
    }

    [Fact]
    public void Document_nodeType_is_document()
    {
        var (eng, _) = MakeEngineWithDocument();
        // Node.DOCUMENT_NODE = 9
        Assert.Equal(9.0, eng.Evaluate("document.nodeType;"));
    }

    [Fact]
    public void Document_documentElement_is_html()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("HTML", eng.Evaluate("document.documentElement.tagName;"));
    }

    [Fact]
    public void Document_body_returns_body_element()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("BODY", eng.Evaluate("document.body.tagName;"));
    }

    [Fact]
    public void Document_head_returns_head_element()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("HEAD", eng.Evaluate("document.head.tagName;"));
    }

    // ========================================================
    // Element property reads
    // ========================================================

    [Fact]
    public void Element_tagName_is_uppercase()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal("DIV", eng.Evaluate("document.body.firstChild.tagName;"));
    }

    [Fact]
    public void Element_id_reads_attribute()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "main");
        doc.Body!.AppendChild(div);
        Assert.Equal("main", eng.Evaluate("document.body.firstChild.id;"));
    }

    [Fact]
    public void Element_className_reads_attribute()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "foo bar");
        doc.Body!.AppendChild(div);
        Assert.Equal("foo bar", eng.Evaluate("document.body.firstChild.className;"));
    }

    [Fact]
    public void Element_classList_supports_contains()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "a b c");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate("document.body.firstChild.classList.contains('b');"));
        Assert.Equal(
            false,
            eng.Evaluate("document.body.firstChild.classList.contains('x');"));
    }

    [Fact]
    public void Element_textContent_reads_descendants()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateTextNode("hello "));
        var span = doc.CreateElement("span");
        span.AppendChild(doc.CreateTextNode("world"));
        div.AppendChild(span);
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "hello world",
            eng.Evaluate("document.body.firstChild.textContent;"));
    }

    [Fact]
    public void Element_getAttribute_returns_value()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("a");
        a.SetAttribute("href", "https://example.com/");
        doc.Body!.AppendChild(a);
        Assert.Equal(
            "https://example.com/",
            eng.Evaluate("document.body.firstChild.getAttribute('href');"));
    }

    [Fact]
    public void Element_hasAttribute_is_true_when_present()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("a");
        a.SetAttribute("target", "_blank");
        doc.Body!.AppendChild(a);
        Assert.Equal(
            true,
            eng.Evaluate("document.body.firstChild.hasAttribute('target');"));
    }

    [Fact]
    public void Element_hasAttribute_is_false_when_missing()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("a");
        doc.Body!.AppendChild(a);
        Assert.Equal(
            false,
            eng.Evaluate("document.body.firstChild.hasAttribute('href');"));
    }

    // ========================================================
    // Query methods (document + element)
    // ========================================================

    [Fact]
    public void Document_getElementById_returns_match()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "target");
        div.AppendChild(doc.CreateTextNode("pick me"));
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "pick me",
            eng.Evaluate("document.getElementById('target').textContent;"));
    }

    [Fact]
    public void Document_getElementById_returns_null_when_missing()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            JsValue.Null,
            eng.Evaluate("document.getElementById('nope');"));
    }

    [Fact]
    public void Document_querySelector_finds_first_match()
    {
        var (eng, doc) = MakeEngineWithDocument();
        for (int i = 0; i < 3; i++)
        {
            var p = doc.CreateElement("p");
            p.AppendChild(doc.CreateTextNode($"para {i}"));
            doc.Body!.AppendChild(p);
        }
        Assert.Equal(
            "para 0",
            eng.Evaluate("document.querySelector('p').textContent;"));
    }

    [Fact]
    public void Document_querySelectorAll_returns_array()
    {
        var (eng, doc) = MakeEngineWithDocument();
        for (int i = 0; i < 3; i++)
        {
            var p = doc.CreateElement("p");
            p.AppendChild(doc.CreateTextNode($"p{i}"));
            doc.Body!.AppendChild(p);
        }
        Assert.Equal(
            3.0,
            eng.Evaluate("document.querySelectorAll('p').length;"));
    }

    [Fact]
    public void Document_querySelectorAll_is_iterable()
    {
        var (eng, doc) = MakeEngineWithDocument();
        for (int i = 0; i < 3; i++)
        {
            var p = doc.CreateElement("p");
            p.AppendChild(doc.CreateTextNode($"p{i}"));
            doc.Body!.AppendChild(p);
        }
        Assert.Equal(
            "p0p1p2",
            eng.Evaluate("var out=''; for (var p of document.querySelectorAll('p')) out += p.textContent; out;"));
    }

    [Fact]
    public void Element_querySelector_scoped_to_subtree()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var outer = doc.CreateElement("div");
        outer.SetAttribute("id", "outer");
        doc.Body!.AppendChild(outer);
        var inner = doc.CreateElement("span");
        inner.AppendChild(doc.CreateTextNode("scoped"));
        outer.AppendChild(inner);
        Assert.Equal(
            "scoped",
            eng.Evaluate("document.getElementById('outer').querySelector('span').textContent;"));
    }

    [Fact]
    public void Document_getElementsByTagName_returns_all_matches()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var ul = doc.CreateElement("ul");
        doc.Body!.AppendChild(ul);
        for (int i = 0; i < 4; i++)
        {
            ul.AppendChild(doc.CreateElement("li"));
        }
        Assert.Equal(
            4.0,
            eng.Evaluate("document.getElementsByTagName('li').length;"));
    }

    [Fact]
    public void Document_getElementsByClassName_returns_matches()
    {
        var (eng, doc) = MakeEngineWithDocument();
        for (int i = 0; i < 3; i++)
        {
            var d = doc.CreateElement("div");
            d.SetAttribute("class", "row");
            doc.Body!.AppendChild(d);
        }
        var other = doc.CreateElement("div");
        other.SetAttribute("class", "header");
        doc.Body!.AppendChild(other);
        Assert.Equal(
            3.0,
            eng.Evaluate("document.getElementsByClassName('row').length;"));
    }

    // ========================================================
    // Mutation from script
    // ========================================================

    [Fact]
    public void Script_createElement_and_appendChild_mutates_dom()
    {
        var (eng, doc) = MakeEngineWithDocument();
        eng.Evaluate(@"
            var d = document.createElement('div');
            d.id = 'added';
            document.body.appendChild(d);
        ");
        var added = doc.GetElementById("added");
        Assert.NotNull(added);
        Assert.Equal("div", added!.TagName);
    }

    [Fact]
    public void Script_setAttribute_mutates_dom()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.setAttribute('data-key', 'v');");
        Assert.Equal("v", div.GetAttribute("data-key"));
    }

    [Fact]
    public void Script_removeAttribute_mutates_dom()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("data-x", "1");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.removeAttribute('data-x');");
        Assert.False(div.HasAttribute("data-x"));
    }

    [Fact]
    public void Script_setting_id_mutates_dom()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.id = 'wired';");
        Assert.Equal("wired", div.Id);
    }

    [Fact]
    public void Script_setting_className_mutates_dom()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.className = 'alpha beta';");
        Assert.Equal("alpha beta", div.ClassName);
    }

    [Fact]
    public void Script_textContent_replacement_rewrites_subtree()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.AppendChild(doc.CreateElement("span"));
        div.AppendChild(doc.CreateTextNode("old"));
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.textContent = 'new content';");
        Assert.Equal("new content", div.TextContent);
        // Old children should be gone, replaced by a single text node.
        Assert.Single(div.ChildNodes);
        Assert.IsType<Text>(div.ChildNodes[0]);
    }

    [Fact]
    public void Script_removeChild_detaches_node()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.removeChild(document.body.firstChild);");
        Assert.Null(div.ParentNode);
        Assert.Empty(doc.Body!.ChildNodes);
    }

    [Fact]
    public void Script_classList_add_mutates_className()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.classList.add('first', 'second');");
        Assert.Contains("first", div.ClassList);
        Assert.Contains("second", div.ClassList);
    }

    [Fact]
    public void Script_classList_remove_mutates_className()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "a b c");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.body.firstChild.classList.remove('b');");
        Assert.Equal(new[] { "a", "c" }, div.ClassList);
    }

    [Fact]
    public void Script_classList_toggle_adds_when_missing()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "a");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate("document.body.firstChild.classList.toggle('b');"));
        Assert.Contains("b", div.ClassList);
    }

    [Fact]
    public void Script_classList_toggle_removes_when_present()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "a b");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            false,
            eng.Evaluate("document.body.firstChild.classList.toggle('b');"));
        Assert.DoesNotContain("b", div.ClassList);
    }

    // ========================================================
    // Identity semantics
    // ========================================================

    [Fact]
    public void Wrapper_identity_is_stable_across_reads()
    {
        var (eng, doc) = MakeEngineWithDocument();
        // el.parentNode === el.parentNode — requires cache hit
        // on the wrapper, otherwise each read builds a fresh
        // wrapper and === fails.
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var el = document.body.firstChild;
                el.parentNode === el.parentNode;
            "));
    }

    [Fact]
    public void Document_global_is_same_instance_per_call()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("document === document;"));
    }

    [Fact]
    public void Element_reference_survives_round_trip()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "same");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var a = document.getElementById('same');
                var b = document.querySelector('#same');
                a === b;
            "));
    }

    // ========================================================
    // Tree traversal
    // ========================================================

    [Fact]
    public void Element_childNodes_reflects_backing_children()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var ul = doc.CreateElement("ul");
        doc.Body!.AppendChild(ul);
        for (int i = 0; i < 3; i++)
        {
            ul.AppendChild(doc.CreateElement("li"));
        }
        Assert.Equal(
            3.0,
            eng.Evaluate("document.querySelector('ul').childNodes.length;"));
    }

    [Fact]
    public void Element_firstChild_returns_wrapped_node()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var ul = doc.CreateElement("ul");
        doc.Body!.AppendChild(ul);
        ul.AppendChild(doc.CreateElement("li"));
        Assert.Equal(
            "LI",
            eng.Evaluate("document.querySelector('ul').firstChild.tagName;"));
    }

    [Fact]
    public void Element_parentNode_walks_up()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var outer = doc.CreateElement("div");
        outer.SetAttribute("id", "outer");
        doc.Body!.AppendChild(outer);
        var inner = doc.CreateElement("span");
        inner.SetAttribute("id", "inner");
        outer.AppendChild(inner);
        Assert.Equal(
            "outer",
            eng.Evaluate("document.getElementById('inner').parentNode.id;"));
    }

    [Fact]
    public void Element_contains_recognizes_descendants()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("div");
        doc.Body!.AppendChild(a);
        var b = doc.CreateElement("span");
        a.AppendChild(b);
        Assert.Equal(
            true,
            eng.Evaluate("document.body.firstChild.contains(document.querySelector('span'));"));
    }

    [Fact]
    public void Element_matches_selector()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "box");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate("document.body.firstChild.matches('.box');"));
    }

    [Fact]
    public void Element_closest_finds_ancestor()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var section = doc.CreateElement("section");
        section.SetAttribute("class", "outer");
        doc.Body!.AppendChild(section);
        var span = doc.CreateElement("span");
        section.AppendChild(span);
        Assert.Equal(
            "SECTION",
            eng.Evaluate("document.querySelector('span').closest('.outer').tagName;"));
    }

    // ========================================================
    // window shim
    // ========================================================

    [Fact]
    public void Window_exists_after_attach()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("object", eng.Evaluate("typeof window;"));
    }

    [Fact]
    public void Window_document_equals_document_global()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("window.document === document;"));
    }

    [Fact]
    public void Window_self_reference_is_stable()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("window.window === window;"));
    }

    // ========================================================
    // End-to-end scripted pipeline
    // ========================================================

    [Fact]
    public void Script_can_build_a_list_programmatically()
    {
        var (eng, doc) = MakeEngineWithDocument();
        eng.Evaluate(@"
            var ul = document.createElement('ul');
            ul.id = 'list';
            var items = ['a', 'b', 'c'];
            for (var i = 0; i < items.length; i++) {
                var li = document.createElement('li');
                li.textContent = items[i];
                ul.appendChild(li);
            }
            document.body.appendChild(ul);
        ");
        var ul = doc.GetElementById("list");
        Assert.NotNull(ul);
        Assert.Equal(3, ul!.ChildNodes.Count);
        Assert.Equal("a", ul.ChildNodes[0].TextContent);
        Assert.Equal("c", ul.ChildNodes[2].TextContent);
    }

    [Fact]
    public void Script_can_query_then_mutate_in_one_pipeline()
    {
        var (eng, doc) = MakeEngineWithDocument();
        for (int i = 0; i < 3; i++)
        {
            var li = doc.CreateElement("li");
            li.SetAttribute("class", "item");
            li.AppendChild(doc.CreateTextNode($"item {i}"));
            doc.Body!.AppendChild(li);
        }
        eng.Evaluate(@"
            var items = document.querySelectorAll('.item');
            for (var i = 0; i < items.length; i++) {
                items[i].setAttribute('data-index', String(i));
            }
        ");
        Assert.Equal("0", doc.Body!.ChildNodes[0] is Element e0 ? e0.GetAttribute("data-index") : null);
        Assert.Equal("1", doc.Body!.ChildNodes[1] is Element e1 ? e1.GetAttribute("data-index") : null);
        Assert.Equal("2", doc.Body!.ChildNodes[2] is Element e2 ? e2.GetAttribute("data-index") : null);
    }
}
