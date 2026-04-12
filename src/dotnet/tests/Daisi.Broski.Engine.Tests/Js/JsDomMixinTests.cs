using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// ChildNode / ParentNode mixin methods
/// (<c>append</c>, <c>prepend</c>, <c>remove</c>,
/// <c>before</c>, <c>after</c>, <c>replaceChildren</c>),
/// <c>document.currentScript</c> tracking, and the
/// URL base-object coercion that lets
/// <c>new URL('.', location)</c> resolve.
/// </summary>
public class JsDomMixinTests
{
    private static (JsEngine engine, Document doc) MakeEngineWithDocument(Uri? pageUrl = null)
    {
        var doc = new Document();
        var html_ = doc.CreateElement("html");
        doc.AppendChild(html_);
        var body = doc.CreateElement("body");
        html_.AppendChild(body);
        var engine = new JsEngine();
        engine.AttachDocument(doc, pageUrl);
        return (engine, doc);
    }

    // ========================================================
    // append / prepend
    // ========================================================

    [Fact]
    public void Append_node_adds_as_last_child()
    {
        var (eng, doc) = MakeEngineWithDocument();
        eng.Evaluate(@"
            var span = document.createElement('span');
            span.id = 'marker';
            document.body.append(span);
        ");
        Assert.Single(doc.Body!.ChildNodes);
        Assert.Equal("span", ((Element)doc.Body.ChildNodes[0]).TagName);
    }

    [Fact]
    public void Append_string_creates_text_node()
    {
        var (eng, doc) = MakeEngineWithDocument();
        eng.Evaluate("document.body.append('hello');");
        Assert.Equal("hello", doc.Body!.TextContent);
    }

    [Fact]
    public void Append_multiple_mixed_arguments()
    {
        var (eng, doc) = MakeEngineWithDocument();
        eng.Evaluate(@"
            document.body.append(
                'prefix ',
                document.createElement('b'),
                ' suffix'
            );
        ");
        Assert.Equal(3, doc.Body!.ChildNodes.Count);
    }

    [Fact]
    public void Prepend_inserts_at_start()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var existing = doc.CreateElement("p");
        doc.Body!.AppendChild(existing);
        eng.Evaluate("document.body.prepend(document.createElement('span'));");
        Assert.Equal(2, doc.Body.ChildNodes.Count);
        Assert.Equal("span", ((Element)doc.Body.ChildNodes[0]).TagName);
    }

    [Fact]
    public void ReplaceChildren_clears_then_adds()
    {
        var (eng, doc) = MakeEngineWithDocument();
        doc.Body!.AppendChild(doc.CreateElement("p"));
        doc.Body!.AppendChild(doc.CreateElement("span"));
        eng.Evaluate("document.body.replaceChildren('just text');");
        Assert.Single(doc.Body.ChildNodes);
        Assert.Equal("just text", doc.Body.TextContent);
    }

    // ========================================================
    // remove / before / after
    // ========================================================

    [Fact]
    public void Remove_detaches_from_parent()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "target");
        doc.Body!.AppendChild(div);
        eng.Evaluate("document.getElementById('target').remove();");
        Assert.Empty(doc.Body.ChildNodes);
    }

    [Fact]
    public void Remove_on_detached_is_noop()
    {
        // Calling remove() on a node that's already detached
        // from its parent should silently succeed, not throw.
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var el = document.createElement('div');
                el.remove();
                el.parentNode === null;
            "));
    }

    [Fact]
    public void Before_inserts_preceding_sibling()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("a");
        a.SetAttribute("id", "anchor");
        doc.Body!.AppendChild(a);
        eng.Evaluate(@"
            document.getElementById('anchor').before(
                document.createElement('span')
            );
        ");
        Assert.Equal(2, doc.Body.ChildNodes.Count);
        Assert.Equal("span", ((Element)doc.Body.ChildNodes[0]).TagName);
        Assert.Equal("a", ((Element)doc.Body.ChildNodes[1]).TagName);
    }

    [Fact]
    public void After_inserts_following_sibling()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var a = doc.CreateElement("a");
        a.SetAttribute("id", "anchor");
        doc.Body!.AppendChild(a);
        eng.Evaluate(@"
            document.getElementById('anchor').after(
                document.createElement('p')
            );
        ");
        Assert.Equal(2, doc.Body.ChildNodes.Count);
        Assert.Equal("a", ((Element)doc.Body.ChildNodes[0]).TagName);
        Assert.Equal("p", ((Element)doc.Body.ChildNodes[1]).TagName);
    }

    // ========================================================
    // document.currentScript
    // ========================================================

    [Fact]
    public void CurrentScript_returns_null_outside_execution()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(JsValue.Null, eng.Evaluate("document.currentScript;"));
    }

    [Fact]
    public void CurrentScript_returns_executing_element()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var script = doc.CreateElement("script");
        script.SetAttribute("id", "theScript");
        doc.Body!.AppendChild(script);

        // Host sets ExecutingScript before running.
        eng.ExecutingScript = script;
        var result = eng.Evaluate("document.currentScript.id;");
        eng.ExecutingScript = null;

        Assert.Equal("theScript", result);
    }

    [Fact]
    public void CurrentScript_parentElement_resolves()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var script = doc.CreateElement("script");
        doc.Body!.AppendChild(script);
        eng.ExecutingScript = script;
        var result = eng.Evaluate("document.currentScript.parentElement.tagName;");
        eng.ExecutingScript = null;
        Assert.Equal("BODY", result);
    }

    // ========================================================
    // URL base-object coercion
    // ========================================================

    [Fact]
    public void NewUrl_accepts_location_as_base()
    {
        var (eng, _) = MakeEngineWithDocument(new Uri("https://example.com/app/"));
        Assert.Equal(
            "https://example.com/app/foo",
            eng.Evaluate("new URL('foo', location).href;"));
    }

    [Fact]
    public void NewUrl_accepts_dot_relative_against_location()
    {
        var (eng, _) = MakeEngineWithDocument(new Uri("https://example.com/app/page"));
        // `new URL('.', location)` should resolve to the
        // directory of the current page.
        Assert.Equal(
            "https://example.com/app/",
            eng.Evaluate("new URL('.', location).href;"));
    }

    [Fact]
    public void NewUrl_accepts_another_url_as_base()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "https://a.com/x",
            eng.Evaluate(@"
                var base = new URL('https://a.com/');
                new URL('x', base).href;
            "));
    }

    // ========================================================
    // End-to-end — the svelte.dev bootstrap pattern
    // ========================================================

    [Fact]
    public void Svelte_style_bootstrap_runs_cleanly()
    {
        var (eng, doc) = MakeEngineWithDocument(new Uri("https://svelte.dev/"));
        // A compressed version of the svelte.dev scripts
        // that previously crashed — exercising Object.assign
        // on style, the ParentNode.append chain, offsetWidth
        // read, the ChildNode.remove, and new URL('.', location).
        eng.Evaluate(@"
            {
                const div = document.createElement('div');
                Object.assign(div.style, {
                    width: '100px',
                    overflow: 'scroll',
                    position: 'absolute'
                });
                document.body.append(div);
                const hasScrollbars = div.offsetWidth - div.clientWidth > 0;
                document.documentElement.classList.add(
                    hasScrollbars ? 'scrollbars-visible' : 'scrollbars-invisible'
                );
                div.remove();
            }

            var base_ = new URL('.', location).pathname.slice(0, -1);
            window.__base = base_;
        ");
        // The `div` should have been appended, read, and then
        // removed — net zero body elements.
        Assert.Empty(doc.Body!.ChildNodes);
        // base_ should have resolved.
        Assert.Equal("", eng.Globals["__base"]);
    }
}
