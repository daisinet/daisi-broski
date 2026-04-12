using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-10: polish bundle. Three small additions that
/// each wouldn't warrant their own slice: <c>Reflect.construct</c>
/// (now unblocked by a public VM constructor helper),
/// <c>crypto.subtle.digest</c> for SHA-1/256/384/512, and
/// <c>AbortSignal.timeout</c> / <c>AbortSignal.any</c>.
/// </summary>
public class JsPolishTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Reflect.construct
    // ========================================================

    [Fact]
    public void Reflect_construct_creates_instance_of_class()
    {
        Assert.Equal(
            true,
            Eval(@"
                class Foo { constructor() { this.ok = true; } }
                var f = Reflect.construct(Foo, []);
                f instanceof Foo && f.ok;
            "));
    }

    [Fact]
    public void Reflect_construct_forwards_arguments()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                function Point(x, y) { this.x = x; this.y = y; }
                var p = Reflect.construct(Point, [2, 3]);
                p.x + p.y;
            "));
    }

    [Fact]
    public void Reflect_construct_rejects_non_function()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { Reflect.construct({}, []); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    // ========================================================
    // crypto.subtle.digest
    // ========================================================

    [Fact]
    public void SubtleCrypto_digest_sha256_returns_32_bytes()
    {
        Eval(@"
            var len = 0;
            var bytes = new TextEncoder().encode('hello');
            crypto.subtle.digest('SHA-256', bytes).then(function (buf) { len = buf.byteLength; });
        ");
        // The first statement is the init; the second is the
        // fetch. Grab via a second Eval on the same engine.
        var engine = new JsEngine();
        engine.RunScript(@"
            var len = 0;
            var bytes = new TextEncoder().encode('hello');
            crypto.subtle.digest('SHA-256', bytes).then(function (buf) { len = buf.byteLength; });
        ");
        Assert.Equal(32.0, engine.Globals["len"]);
    }

    [Fact]
    public void SubtleCrypto_digest_sha1_returns_20_bytes()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var len = 0;
            crypto.subtle.digest('SHA-1', new TextEncoder().encode('abc'))
                .then(function (b) { len = b.byteLength; });
        ");
        Assert.Equal(20.0, engine.Globals["len"]);
    }

    [Fact]
    public void SubtleCrypto_digest_sha512_returns_64_bytes()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var len = 0;
            crypto.subtle.digest('SHA-512', new TextEncoder().encode('abc'))
                .then(function (b) { len = b.byteLength; });
        ");
        Assert.Equal(64.0, engine.Globals["len"]);
    }

    [Fact]
    public void SubtleCrypto_digest_matches_known_sha256_of_abc()
    {
        // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        var engine = new JsEngine();
        engine.RunScript(@"
            var hex = '';
            function bytesToHex(buf) {
                var view = new Uint8Array(buf);
                var out = '';
                for (var i = 0; i < view.length; i++) {
                    var h = view[i].toString(16);
                    if (h.length < 2) h = '0' + h;
                    out += h;
                }
                return out;
            }
            crypto.subtle.digest('SHA-256', new TextEncoder().encode('abc'))
                .then(function (buf) { hex = bytesToHex(buf); });
        ");
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            engine.Globals["hex"]);
    }

    [Fact]
    public void SubtleCrypto_digest_accepts_algorithm_object()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var len = 0;
            crypto.subtle.digest({ name: 'SHA-256' }, new TextEncoder().encode('x'))
                .then(function (b) { len = b.byteLength; });
        ");
        Assert.Equal(32.0, engine.Globals["len"]);
    }

    [Fact]
    public void SubtleCrypto_digest_rejects_unknown_algorithm()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var errName = '';
            crypto.subtle.digest('MD5', new TextEncoder().encode('x'))
                .then(function () {}, function (e) { errName = e.name; });
        ");
        Assert.Equal("NotSupportedError", engine.Globals["errName"]);
    }

    // ========================================================
    // AbortSignal.timeout
    // ========================================================

    [Fact]
    public void AbortSignal_timeout_eventually_aborts()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var aborted = false;
            var sig = AbortSignal.timeout(0);
            sig.addEventListener('abort', function () { aborted = true; });
        ");
        Assert.Equal(true, engine.Globals["aborted"]);
    }

    [Fact]
    public void AbortSignal_timeout_reason_is_TimeoutError()
    {
        var engine = new JsEngine();
        engine.RunScript(@"
            var name = '';
            var sig = AbortSignal.timeout(0);
            sig.addEventListener('abort', function () { name = sig.reason.name; });
        ");
        Assert.Equal("TimeoutError", engine.Globals["name"]);
    }

    // ========================================================
    // AbortSignal.any
    // ========================================================

    [Fact]
    public void AbortSignal_any_aborts_when_first_input_aborts()
    {
        Assert.Equal(
            "input-1",
            Eval(@"
                var c1 = new AbortController();
                var c2 = new AbortController();
                var any = AbortSignal.any([c1.signal, c2.signal]);
                c1.abort('input-1');
                any.reason;
            "));
    }

    [Fact]
    public void AbortSignal_any_inherits_second_abort_reason()
    {
        Assert.Equal(
            "input-2",
            Eval(@"
                var c1 = new AbortController();
                var c2 = new AbortController();
                var any = AbortSignal.any([c1.signal, c2.signal]);
                c2.abort('input-2');
                any.reason;
            "));
    }

    [Fact]
    public void AbortSignal_any_already_aborted_input_propagates_sync()
    {
        Assert.Equal(
            true,
            Eval(@"
                var s = AbortSignal.abort('gone');
                var any = AbortSignal.any([s]);
                any.aborted && any.reason === 'gone';
            "));
    }

    [Fact]
    public void AbortSignal_any_only_fires_once()
    {
        // After one input aborts, the combined signal
        // should ignore subsequent aborts from other inputs
        // (once aborted, always aborted).
        Assert.Equal(
            "first",
            Eval(@"
                var c1 = new AbortController();
                var c2 = new AbortController();
                var any = AbortSignal.any([c1.signal, c2.signal]);
                c1.abort('first');
                c2.abort('second');
                any.reason;
            "));
    }
}
