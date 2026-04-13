using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Round-2 host API shims: <c>history</c>,
/// <c>document.cookie</c> / <c>readyState</c>, element
/// <c>dataset</c> / <c>getBoundingClientRect</c> /
/// layout-dependent stubs, <c>requestAnimationFrame</c>,
/// and the observer stubs (<c>IntersectionObserver</c>,
/// <c>ResizeObserver</c>, <c>MutationObserver</c>).
/// </summary>
public class JsBrowserHost2Tests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    private static (JsEngine engine, Document doc) MakeEngineWithDocument()
    {
        var doc = new Document();
        var html_ = doc.CreateElement("html");
        doc.AppendChild(html_);
        var body = doc.CreateElement("body");
        html_.AppendChild(body);
        var engine = new JsEngine();
        engine.AttachDocument(doc);
        return (engine, doc);
    }

    // ========================================================
    // history
    // ========================================================

    [Fact]
    public void History_exists()
    {
        Assert.Equal("object", Eval("typeof history;"));
    }

    [Fact]
    public void History_initial_length_is_one()
    {
        Assert.Equal(1.0, Eval("history.length;"));
    }

    [Fact]
    public void History_initial_state_is_null()
    {
        Assert.Equal(JsValue.Null, Eval("history.state;"));
    }

    [Fact]
    public void History_pushState_increments_length()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                history.pushState({ page: 1 }, '', '/one');
                history.pushState({ page: 2 }, '', '/two');
                history.length;
            "));
    }

    [Fact]
    public void History_pushState_stores_state()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                history.pushState({ count: 42 }, '', '/x');
                history.state.count;
            "));
    }

    [Fact]
    public void History_replaceState_does_not_grow_length()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                history.pushState({ a: 1 }, '', '/a');
                history.replaceState({ b: 2 }, '', '/b');
                history.length;
            "));
    }

    [Fact]
    public void History_back_then_state_returns_previous()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                history.pushState({ n: 1 }, '', '/a');
                history.pushState({ n: 2 }, '', '/b');
                history.back();
                history.state.n;
            "));
    }

    [Fact]
    public void History_forward_advances_after_back()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                history.pushState({ n: 1 }, '', '/a');
                history.pushState({ n: 2 }, '', '/b');
                history.back();
                history.forward();
                history.state.n;
            "));
    }

    [Fact]
    public void History_scrollRestoration_reads_auto()
    {
        Assert.Equal("auto", Eval("history.scrollRestoration;"));
    }

    // ========================================================
    // document.cookie + readyState + misc
    // ========================================================

    [Fact]
    public void Document_readyState_is_complete()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("complete", eng.Evaluate("document.readyState;"));
    }

    [Fact]
    public void Document_cookie_starts_empty()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("", eng.Evaluate("document.cookie;"));
    }

    [Fact]
    public void Document_cookie_set_and_read()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "name=alice",
            eng.Evaluate(@"
                document.cookie = 'name=alice; path=/';
                document.cookie;
            "));
    }

    [Fact]
    public void Document_cookie_appends_not_replaces()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "a=1; b=2",
            eng.Evaluate(@"
                document.cookie = 'a=1';
                document.cookie = 'b=2';
                document.cookie;
            "));
    }

    [Fact]
    public void Document_cookie_replace_same_name()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "name=bob",
            eng.Evaluate(@"
                document.cookie = 'name=alice';
                document.cookie = 'name=bob';
                document.cookie;
            "));
    }

    [Fact]
    public void Document_visibility_state_is_visible()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("visible", eng.Evaluate("document.visibilityState;"));
    }

    [Fact]
    public void Document_defaultView_is_window()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("document.defaultView === window;"));
    }

    [Fact]
    public void Document_characterSet_is_utf8()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("UTF-8", eng.Evaluate("document.characterSet;"));
    }

    // ========================================================
    // Element.dataset + layout stubs
    // ========================================================

    [Fact]
    public void Element_dataset_reads_data_attributes()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("data-user-id", "42");
        div.SetAttribute("data-role", "admin");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "42",
            eng.Evaluate("document.body.firstChild.dataset.userId;"));
    }

    [Fact]
    public void Element_dataset_empty_when_no_data_attrs()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("class", "btn");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "undefined",
            eng.Evaluate("typeof document.body.firstChild.dataset.userId;"));
    }

    [Fact]
    public void Element_getBoundingClientRect_returns_a_DOMRect()
    {
        // Phase 6c shipped real layout — an empty div now has
        // a non-zero width because block boxes default to
        // filling their containing block. The pre-layout
        // expectation here was "zero rect" and it was relaxed
        // to "non-zero width" once block layout landed.
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        var width = (double)eng.Evaluate(
            "document.body.firstChild.getBoundingClientRect().width;")!;
        Assert.True(width > 0,
            "block layout should give the div a non-zero width");
    }

    [Fact]
    public void Element_offsetWidth_is_zero_without_layout()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            0.0,
            eng.Evaluate("document.body.firstChild.offsetWidth;"));
    }

    [Fact]
    public void Element_style_setProperty_is_no_op()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "undefined",
            eng.Evaluate("typeof document.body.firstChild.style.setProperty('display', 'none');"));
    }

    [Fact]
    public void Element_click_dispatches_click_event()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var btn = doc.CreateElement("button");
        btn.SetAttribute("id", "go");
        doc.Body!.AppendChild(btn);
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var hits = 0;
                var btn = document.getElementById('go');
                btn.addEventListener('click', function () { hits++; });
                btn.click();
                hits;
            "));
    }

    // ========================================================
    // requestAnimationFrame
    // ========================================================

    [Fact]
    public void RequestAnimationFrame_fires_callback_after_drain()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var fired = false;
            requestAnimationFrame(function (ts) { fired = true; });
        ");
        Assert.Equal(true, engine.Globals["fired"]);
    }

    [Fact]
    public void RequestAnimationFrame_callback_receives_timestamp()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var ts = null;
            requestAnimationFrame(function (t) { ts = typeof t; });
        ");
        Assert.Equal("number", engine.Globals["ts"]);
    }

    [Fact]
    public void CancelAnimationFrame_cancels_pending()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var fired = false;
            var id = requestAnimationFrame(function () { fired = true; });
            cancelAnimationFrame(id);
        ");
        Assert.Equal(false, engine.Globals["fired"]);
    }

    // ========================================================
    // Observers
    // ========================================================

    [Fact]
    public void IntersectionObserver_constructs_without_error()
    {
        Assert.Equal(
            "object",
            Eval("typeof new IntersectionObserver(function () {});"));
    }

    [Fact]
    public void IntersectionObserver_observe_is_no_op()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "undefined",
            eng.Evaluate(@"
                var obs = new IntersectionObserver(function () {});
                typeof obs.observe(document.body.firstChild);
            "));
    }

    [Fact]
    public void ResizeObserver_exists()
    {
        Assert.Equal(
            "function",
            Eval("typeof ResizeObserver;"));
    }

    [Fact]
    public void MutationObserver_exists()
    {
        Assert.Equal(
            "function",
            Eval("typeof MutationObserver;"));
    }

    [Fact]
    public void Observer_disconnect_and_takeRecords_return_correct_shapes()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var obs = new MutationObserver(function () {});
                obs.disconnect();
                obs.takeRecords().length;
            "));
    }
}
