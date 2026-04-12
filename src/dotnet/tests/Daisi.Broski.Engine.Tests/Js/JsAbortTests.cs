using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-9: <c>AbortController</c> + <c>AbortSignal</c>.
/// Signals aren't DOM nodes so they use a flat event
/// dispatch (no capture / bubble walk) — the tests here
/// focus on the aborted / reason state machine, listener
/// fire ordering, and the <c>AbortSignal.abort(reason)</c>
/// static factory.
/// </summary>
public class JsAbortTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Controller + signal basics
    // ========================================================

    [Fact]
    public void Controller_exposes_signal()
    {
        Assert.Equal(
            "object",
            Eval("typeof new AbortController().signal;"));
    }

    [Fact]
    public void Signal_aborted_starts_false()
    {
        Assert.Equal(
            false,
            Eval("new AbortController().signal.aborted;"));
    }

    [Fact]
    public void Signal_reason_starts_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval("typeof new AbortController().signal.reason;"));
    }

    // ========================================================
    // Abort() flips state
    // ========================================================

    [Fact]
    public void Abort_sets_aborted_true()
    {
        Assert.Equal(
            true,
            Eval(@"
                var c = new AbortController();
                c.abort();
                c.signal.aborted;
            "));
    }

    [Fact]
    public void Abort_with_reason_stores_reason()
    {
        Assert.Equal(
            "user cancel",
            Eval(@"
                var c = new AbortController();
                c.abort('user cancel');
                c.signal.reason;
            "));
    }

    [Fact]
    public void Abort_without_reason_defaults_to_AbortError()
    {
        Assert.Equal(
            "AbortError",
            Eval(@"
                var c = new AbortController();
                c.abort();
                c.signal.reason.name;
            "));
    }

    [Fact]
    public void Second_abort_is_noop()
    {
        Assert.Equal(
            "first",
            Eval(@"
                var c = new AbortController();
                c.abort('first');
                c.abort('second'); // ignored
                c.signal.reason;
            "));
    }

    // ========================================================
    // addEventListener('abort', ...)
    // ========================================================

    [Fact]
    public void Abort_listener_fires_on_abort()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var hits = 0;
                var c = new AbortController();
                c.signal.addEventListener('abort', function () { hits++; });
                c.abort();
                hits;
            "));
    }

    [Fact]
    public void Abort_listener_receives_event_with_target()
    {
        Assert.Equal(
            true,
            Eval(@"
                var sameTarget;
                var c = new AbortController();
                c.signal.addEventListener('abort', function (e) {
                    sameTarget = (e.target === c.signal);
                });
                c.abort();
                sameTarget;
            "));
    }

    [Fact]
    public void Abort_listener_sees_aborted_true_during_fire()
    {
        Assert.Equal(
            true,
            Eval(@"
                var seenAborted;
                var c = new AbortController();
                c.signal.addEventListener('abort', function () {
                    seenAborted = c.signal.aborted;
                });
                c.abort();
                seenAborted;
            "));
    }

    [Fact]
    public void Multiple_listeners_fire_in_registration_order()
    {
        Assert.Equal(
            "abc",
            Eval(@"
                var log = '';
                var c = new AbortController();
                c.signal.addEventListener('abort', function () { log += 'a'; });
                c.signal.addEventListener('abort', function () { log += 'b'; });
                c.signal.addEventListener('abort', function () { log += 'c'; });
                c.abort();
                log;
            "));
    }

    [Fact]
    public void Remove_listener_prevents_fire()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var hits = 0;
                var c = new AbortController();
                var fn = function () { hits++; };
                c.signal.addEventListener('abort', fn);
                c.signal.removeEventListener('abort', fn);
                c.abort();
                hits;
            "));
    }

    [Fact]
    public void Same_listener_twice_dedups()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var hits = 0;
                var c = new AbortController();
                var fn = function () { hits++; };
                c.signal.addEventListener('abort', fn);
                c.signal.addEventListener('abort', fn);
                c.abort();
                hits;
            "));
    }

    [Fact]
    public void Once_listener_auto_removes()
    {
        // Trigger once then fire a manual dispatchEvent —
        // should still only hit once because once: true
        // already removed it after the first fire.
        Assert.Equal(
            1.0,
            Eval(@"
                var hits = 0;
                var c = new AbortController();
                c.signal.addEventListener('abort', function () { hits++; }, { once: true });
                c.abort();
                c.signal.dispatchEvent(new Event('abort'));
                hits;
            "));
    }

    // ========================================================
    // throwIfAborted
    // ========================================================

    [Fact]
    public void ThrowIfAborted_is_noop_when_not_aborted()
    {
        Assert.Equal(
            "ok",
            Eval(@"
                var c = new AbortController();
                try { c.signal.throwIfAborted(); 'ok'; }
                catch (e) { 'threw'; }
            "));
    }

    [Fact]
    public void ThrowIfAborted_throws_reason_when_aborted()
    {
        Assert.Equal(
            "user stop",
            Eval(@"
                var c = new AbortController();
                c.abort('user stop');
                try { c.signal.throwIfAborted(); 'ok'; }
                catch (e) { e; }
            "));
    }

    // ========================================================
    // Static factory
    // ========================================================

    [Fact]
    public void AbortSignal_abort_static_returns_preAborted_signal()
    {
        Assert.Equal(
            true,
            Eval(@"
                var sig = AbortSignal.abort();
                sig.aborted;
            "));
    }

    [Fact]
    public void AbortSignal_abort_static_stores_reason()
    {
        Assert.Equal(
            "immediate",
            Eval(@"
                var sig = AbortSignal.abort('immediate');
                sig.reason;
            "));
    }

    [Fact]
    public void AbortSignal_direct_construction_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new AbortSignal(); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    // ========================================================
    // End-to-end coordination pattern
    // ========================================================

    [Fact]
    public void Signal_coordinates_multiple_subsystems()
    {
        // Canonical pattern: one controller propagates
        // cancellation to several independent listeners.
        Assert.Equal(
            "net:stopped,db:stopped",
            Eval(@"
                var log = [];
                var c = new AbortController();
                c.signal.addEventListener('abort', function () { log.push('net:stopped'); });
                c.signal.addEventListener('abort', function () { log.push('db:stopped'); });
                c.abort();
                log.join(',');
            "));
    }
}
