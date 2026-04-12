using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// De-stubbing slice: Object.freeze/seal enforcement, inline
/// style persistence, getComputedStyle, history+popstate,
/// matchMedia viewport evaluation.
/// </summary>
public class JsStubDeStubTests
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
    // Object.freeze / seal / preventExtensions
    // ========================================================

    [Fact]
    public void Freeze_rejects_writes()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var o = { x: 1 };
                Object.freeze(o);
                o.x = 99;
                o.x;
            "));
    }

    [Fact]
    public void Freeze_rejects_new_properties()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval(@"
                var o = { x: 1 };
                Object.freeze(o);
                o.newProp = 'nope';
                o.newProp;
            "));
    }

    [Fact]
    public void Freeze_rejects_delete()
    {
        Assert.Equal(
            true,
            Eval(@"
                var o = { x: 1 };
                Object.freeze(o);
                delete o.x;
                'x' in o;
            "));
    }

    [Fact]
    public void IsFrozen_returns_true_after_freeze()
    {
        Assert.Equal(
            true,
            Eval(@"
                var o = {};
                Object.freeze(o);
                Object.isFrozen(o);
            "));
    }

    [Fact]
    public void Seal_allows_writes_but_rejects_new_and_delete()
    {
        // Note: undefined in an array .join() renders as
        // the empty string per spec, so o.newProp (which is
        // undefined) shows as empty between the bars.
        Assert.Equal(
            "99||true",
            Eval(@"
                var o = { x: 1 };
                Object.seal(o);
                o.x = 99;         // allowed
                o.newProp = 'no'; // rejected
                delete o.x;       // rejected
                [o.x, o.newProp, 'x' in o].join('|');
            "));
    }

    [Fact]
    public void IsSealed_returns_true_after_seal()
    {
        Assert.Equal(true, Eval("var o = {}; Object.seal(o); Object.isSealed(o);"));
    }

    [Fact]
    public void PreventExtensions_rejects_new_properties()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval(@"
                var o = {};
                Object.preventExtensions(o);
                o.x = 1;
                o.x;
            "));
    }

    [Fact]
    public void IsExtensible_returns_false_after_preventExtensions()
    {
        Assert.Equal(
            false,
            Eval("var o = {}; Object.preventExtensions(o); Object.isExtensible(o);"));
    }

    // ========================================================
    // Inline style persistence
    // ========================================================

    [Fact]
    public void Style_write_and_readback()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "none",
            eng.Evaluate(@"
                var el = document.body.firstChild;
                el.style.display = 'none';
                el.style.display;
            "));
    }

    [Fact]
    public void Style_identity_is_stable()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var el = document.body.firstChild;
                el.style === el.style;
            "));
    }

    [Fact]
    public void Style_write_updates_dom_attribute()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate(@"
            document.body.firstChild.style.backgroundColor = 'red';
        ");
        var attr = div.GetAttribute("style");
        Assert.NotNull(attr);
        Assert.Contains("background-color", attr!);
        Assert.Contains("red", attr);
    }

    [Fact]
    public void Style_reads_from_existing_style_attribute()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("style", "color: blue; font-size: 14px");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "blue",
            eng.Evaluate("document.body.firstChild.style.color;"));
    }

    [Fact]
    public void Style_cssText_roundtrips()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        eng.Evaluate(@"
            document.body.firstChild.style.cssText = 'margin: 10px; padding: 5px';
        ");
        var css = eng.Evaluate("document.body.firstChild.style.cssText;") as string;
        Assert.NotNull(css);
        Assert.Contains("margin", css!);
        Assert.Contains("padding", css);
    }

    [Fact]
    public void Style_setProperty_and_getPropertyValue()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "flex",
            eng.Evaluate(@"
                var s = document.body.firstChild.style;
                s.setProperty('display', 'flex');
                s.getPropertyValue('display');
            "));
    }

    [Fact]
    public void Style_removeProperty_clears_value()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "",
            eng.Evaluate(@"
                var s = document.body.firstChild.style;
                s.display = 'block';
                s.removeProperty('display');
                s.display;
            "));
    }

    // ========================================================
    // getComputedStyle
    // ========================================================

    [Fact]
    public void GetComputedStyle_exists()
    {
        Assert.Equal("function", Eval("typeof getComputedStyle;"));
    }

    [Fact]
    public void GetComputedStyle_reads_inline_style()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var div = doc.CreateElement("div");
        div.SetAttribute("style", "display: flex");
        doc.Body!.AppendChild(div);
        Assert.Equal(
            "flex",
            eng.Evaluate("getComputedStyle(document.body.firstChild).display;"));
    }

    // ========================================================
    // history.pushState → location + popstate
    // ========================================================

    [Fact]
    public void PushState_updates_location_href()
    {
        Assert.Equal(
            "/new-path",
            Eval(@"
                history.pushState(null, '', '/new-path');
                location.href;
            "));
    }

    [Fact]
    public void ReplaceState_updates_location_href()
    {
        Assert.Equal(
            "/replaced",
            Eval(@"
                history.replaceState(null, '', '/replaced');
                location.href;
            "));
    }

    [Fact]
    public void Back_updates_location_to_previous_url()
    {
        Assert.Equal(
            "/one",
            Eval(@"
                history.pushState({ page: 1 }, '', '/one');
                history.pushState({ page: 2 }, '', '/two');
                history.back();
                location.href;
            "));
    }

    [Fact]
    public void Back_updates_state_to_previous_entry()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                history.pushState({ page: 1 }, '', '/one');
                history.pushState({ page: 2 }, '', '/two');
                history.back();
                history.state.page;
            "));
    }

    // ========================================================
    // matchMedia viewport evaluation
    // ========================================================

    [Fact]
    public void MatchMedia_min_width_matches_at_1280()
    {
        Assert.Equal(
            true,
            Eval("matchMedia('(min-width: 800px)').matches;"));
    }

    [Fact]
    public void MatchMedia_min_width_does_not_match_above_viewport()
    {
        Assert.Equal(
            false,
            Eval("matchMedia('(min-width: 1920px)').matches;"));
    }

    [Fact]
    public void MatchMedia_prefers_color_scheme_light()
    {
        Assert.Equal(
            true,
            Eval("matchMedia('(prefers-color-scheme: light)').matches;"));
    }

    [Fact]
    public void MatchMedia_prefers_color_scheme_dark()
    {
        Assert.Equal(
            false,
            Eval("matchMedia('(prefers-color-scheme: dark)').matches;"));
    }

    [Fact]
    public void MatchMedia_screen_media_type()
    {
        Assert.Equal(
            true,
            Eval("matchMedia('screen').matches;"));
    }

    [Fact]
    public void MatchMedia_print_media_type()
    {
        Assert.Equal(
            false,
            Eval("matchMedia('print').matches;"));
    }

    [Fact]
    public void MatchMedia_compound_and()
    {
        Assert.Equal(
            true,
            Eval("matchMedia('(min-width: 600px) and (max-width: 1600px)').matches;"));
    }

    [Fact]
    public void MatchMedia_not_query()
    {
        Assert.Equal(
            true,
            Eval("matchMedia('not print').matches;"));
    }

    [Fact]
    public void MatchMedia_orientation_landscape()
    {
        // 1280 > 800 → landscape
        Assert.Equal(
            true,
            Eval("matchMedia('(orientation: landscape)').matches;"));
    }
}
