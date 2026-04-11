using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-4: DOM events — <c>addEventListener</c>,
/// <c>removeEventListener</c>, <c>dispatchEvent</c>, plus
/// the <c>Event</c> and <c>CustomEvent</c> constructors.
///
/// Tests build a small tree on the C# side, attach it to a
/// fresh engine, register listeners from script, then
/// dispatch events and assert on the log they produce.
/// </summary>
public class JsDomEventTests
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
    // Event constructor
    // ========================================================

    [Fact]
    public void Event_constructor_sets_type()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal("click", eng.Evaluate("new Event('click').type;"));
    }

    [Fact]
    public void Event_defaults_bubbles_to_false()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(false, eng.Evaluate("new Event('click').bubbles;"));
    }

    [Fact]
    public void Event_init_dict_sets_bubbles()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("new Event('click', { bubbles: true }).bubbles;"));
    }

    [Fact]
    public void Event_init_dict_sets_cancelable()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate("new Event('click', { cancelable: true }).cancelable;"));
    }

    [Fact]
    public void Event_phase_constants_are_exposed()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(2.0, eng.Evaluate("Event.AT_TARGET;"));
    }

    [Fact]
    public void CustomEvent_carries_detail_payload()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            42.0,
            eng.Evaluate("new CustomEvent('ping', { detail: { count: 42 } }).detail.count;"));
    }

    // ========================================================
    // addEventListener + dispatchEvent basics
    // ========================================================

    [Fact]
    public void Listener_fires_on_dispatch()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var hits = 0;
                document.body.addEventListener('click', function () { hits++; });
                document.body.dispatchEvent(new Event('click'));
                hits;
            "));
    }

    [Fact]
    public void Listener_receives_event_as_argument()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "click",
            eng.Evaluate(@"
                var seen;
                document.body.addEventListener('click', function (e) { seen = e.type; });
                document.body.dispatchEvent(new Event('click'));
                seen;
            "));
    }

    [Fact]
    public void Listener_sees_target_as_dispatch_target()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var sameAsBody;
                document.body.addEventListener('click', function (e) {
                    sameAsBody = (e.target === document.body);
                });
                document.body.dispatchEvent(new Event('click'));
                sameAsBody;
            "));
    }

    [Fact]
    public void Listener_this_is_current_target()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var sameAsBody;
                document.body.addEventListener('click', function (e) {
                    sameAsBody = (this === document.body);
                });
                document.body.dispatchEvent(new Event('click'));
                sameAsBody;
            "));
    }

    [Fact]
    public void Multiple_listeners_fire_in_registration_order()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "abc",
            eng.Evaluate(@"
                var out = '';
                document.body.addEventListener('x', function () { out += 'a'; });
                document.body.addEventListener('x', function () { out += 'b'; });
                document.body.addEventListener('x', function () { out += 'c'; });
                document.body.dispatchEvent(new Event('x'));
                out;
            "));
    }

    [Fact]
    public void Same_listener_registered_twice_only_fires_once()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var hits = 0;
                var fn = function () { hits++; };
                document.body.addEventListener('x', fn);
                document.body.addEventListener('x', fn); // dedup
                document.body.dispatchEvent(new Event('x'));
                hits;
            "));
    }

    // ========================================================
    // removeEventListener
    // ========================================================

    [Fact]
    public void Removed_listener_does_not_fire()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            0.0,
            eng.Evaluate(@"
                var hits = 0;
                var fn = function () { hits++; };
                document.body.addEventListener('x', fn);
                document.body.removeEventListener('x', fn);
                document.body.dispatchEvent(new Event('x'));
                hits;
            "));
    }

    [Fact]
    public void Remove_does_not_affect_other_listeners()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var hits = 0;
                var keep = function () { hits++; };
                var drop = function () { hits += 10; };
                document.body.addEventListener('x', keep);
                document.body.addEventListener('x', drop);
                document.body.removeEventListener('x', drop);
                document.body.dispatchEvent(new Event('x'));
                hits;
            "));
    }

    // ========================================================
    // once option
    // ========================================================

    [Fact]
    public void Once_listener_fires_exactly_one_time()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var hits = 0;
                document.body.addEventListener('x', function () { hits++; }, { once: true });
                document.body.dispatchEvent(new Event('x'));
                document.body.dispatchEvent(new Event('x'));
                document.body.dispatchEvent(new Event('x'));
                hits;
            "));
    }

    // ========================================================
    // Bubbling + capturing
    // ========================================================

    [Fact]
    public void Bubble_reaches_parent()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            "body",
            eng.Evaluate(@"
                var seen = '';
                document.body.addEventListener('ping', function () { seen = 'body'; });
                document.getElementById('child').dispatchEvent(new Event('ping', { bubbles: true }));
                seen;
            "));
    }

    [Fact]
    public void Non_bubbling_event_does_not_reach_parent()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            "",
            eng.Evaluate(@"
                var seen = '';
                document.body.addEventListener('ping', function () { seen = 'body'; });
                document.getElementById('child').dispatchEvent(new Event('ping'));
                seen;
            "));
    }

    [Fact]
    public void Capture_phase_runs_before_target()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            "body-capture,child-target",
            eng.Evaluate(@"
                var log = [];
                document.body.addEventListener('ping', function () { log.push('body-capture'); }, { capture: true });
                document.getElementById('child').addEventListener('ping', function () { log.push('child-target'); });
                document.getElementById('child').dispatchEvent(new Event('ping', { bubbles: true }));
                log.join(',');
            "));
    }

    [Fact]
    public void Capture_target_bubble_order()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            "html-cap,body-cap,child,body-bub,html-bub",
            eng.Evaluate(@"
                var log = [];
                document.documentElement.addEventListener('ping', function () { log.push('html-cap'); }, { capture: true });
                document.body.addEventListener('ping', function () { log.push('body-cap'); }, { capture: true });
                document.getElementById('child').addEventListener('ping', function () { log.push('child'); });
                document.body.addEventListener('ping', function () { log.push('body-bub'); });
                document.documentElement.addEventListener('ping', function () { log.push('html-bub'); });
                document.getElementById('child').dispatchEvent(new Event('ping', { bubbles: true }));
                log.join(',');
            "));
    }

    [Fact]
    public void Event_phase_is_reported_correctly()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            "1,2,3",
            eng.Evaluate(@"
                var phases = [];
                document.body.addEventListener('ping', function (e) { phases.push(String(e.eventPhase)); }, { capture: true });
                document.getElementById('child').addEventListener('ping', function (e) { phases.push(String(e.eventPhase)); });
                document.body.addEventListener('ping', function (e) { phases.push(String(e.eventPhase)); });
                document.getElementById('child').dispatchEvent(new Event('ping', { bubbles: true }));
                phases.join(',');
            "));
    }

    // ========================================================
    // stopPropagation + preventDefault
    // ========================================================

    [Fact]
    public void StopPropagation_prevents_bubble()
    {
        var (eng, doc) = MakeEngineWithDocument();
        var child = doc.CreateElement("span");
        child.SetAttribute("id", "child");
        doc.Body!.AppendChild(child);
        Assert.Equal(
            false,
            eng.Evaluate(@"
                var bodyHit = false;
                document.body.addEventListener('ping', function () { bodyHit = true; });
                document.getElementById('child').addEventListener('ping', function (e) { e.stopPropagation(); });
                document.getElementById('child').dispatchEvent(new Event('ping', { bubbles: true }));
                bodyHit;
            "));
    }

    [Fact]
    public void StopImmediatePropagation_blocks_later_listeners_at_same_target()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "first",
            eng.Evaluate(@"
                var log = '';
                document.body.addEventListener('x', function (e) { log += 'first'; e.stopImmediatePropagation(); });
                document.body.addEventListener('x', function () { log += '-second'; });
                document.body.dispatchEvent(new Event('x'));
                log;
            "));
    }

    [Fact]
    public void PreventDefault_sets_defaultPrevented_when_cancelable()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var evt = new Event('x', { cancelable: true });
                document.body.addEventListener('x', function (e) { e.preventDefault(); });
                document.body.dispatchEvent(evt);
                evt.defaultPrevented;
            "));
    }

    [Fact]
    public void PreventDefault_ignored_when_not_cancelable()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            false,
            eng.Evaluate(@"
                var evt = new Event('x'); // cancelable: false
                document.body.addEventListener('x', function (e) { e.preventDefault(); });
                document.body.dispatchEvent(evt);
                evt.defaultPrevented;
            "));
    }

    [Fact]
    public void DispatchEvent_returns_false_when_preventDefault_called()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            false,
            eng.Evaluate(@"
                document.body.addEventListener('x', function (e) { e.preventDefault(); });
                document.body.dispatchEvent(new Event('x', { cancelable: true }));
            "));
    }

    [Fact]
    public void DispatchEvent_returns_true_when_no_preventDefault()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                document.body.addEventListener('x', function () {});
                document.body.dispatchEvent(new Event('x'));
            "));
    }

    // ========================================================
    // CustomEvent dispatch
    // ========================================================

    [Fact]
    public void CustomEvent_passes_detail_through_dispatch()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            "greetings",
            eng.Evaluate(@"
                var seen;
                document.body.addEventListener('say', function (e) { seen = e.detail.message; });
                document.body.dispatchEvent(new CustomEvent('say', { detail: { message: 'greetings' } }));
                seen;
            "));
    }

    // ========================================================
    // Throwing listener doesn't break subsequent listeners
    // ========================================================

    [Fact]
    public void Throwing_listener_does_not_block_others()
    {
        var (eng, _) = MakeEngineWithDocument();
        Assert.Equal(
            2.0,
            eng.Evaluate(@"
                var hits = 0;
                document.body.addEventListener('x', function () { hits++; throw new Error('bad'); });
                document.body.addEventListener('x', function () { hits++; });
                document.body.dispatchEvent(new Event('x'));
                hits;
            "));
    }
}
