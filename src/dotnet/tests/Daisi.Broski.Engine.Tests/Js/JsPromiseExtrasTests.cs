using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsPromiseExtrasTests
{
    private static string RunAndReadConsole(string src)
    {
        var eng = new JsEngine();
        eng.RunScript(src);
        return eng.ConsoleOutput.ToString().TrimEnd('\n', '\r');
    }

    // ========================================================
    // Promise.allSettled
    // ========================================================

    [Fact]
    public void AllSettled_returns_status_descriptors()
    {
        var out_ = RunAndReadConsole(@"
            Promise.allSettled([
                Promise.resolve(1),
                Promise.reject('oops'),
                Promise.resolve(3)
            ]).then(function (results) {
                var parts = results.map(function (r) {
                    if (r.status === 'fulfilled') return 'f:' + r.value;
                    return 'r:' + r.reason;
                });
                console.log(parts.join(','));
            });
        ");
        Assert.Equal("f:1,r:oops,f:3", out_);
    }

    [Fact]
    public void AllSettled_never_rejects()
    {
        var out_ = RunAndReadConsole(@"
            var logged = false;
            Promise.allSettled([Promise.reject('a'), Promise.reject('b')])
                .then(function () { logged = true; console.log('ok'); })
                .catch(function () { console.log('unexpected-reject'); });
        ");
        Assert.Equal("ok", out_);
    }

    [Fact]
    public void AllSettled_empty_resolves_with_empty_array()
    {
        var out_ = RunAndReadConsole(@"
            Promise.allSettled([]).then(function (r) { console.log('len=' + r.length); });
        ");
        Assert.Equal("len=0", out_);
    }

    // ========================================================
    // Promise.any
    // ========================================================

    [Fact]
    public void Any_resolves_with_first_fulfilled()
    {
        var out_ = RunAndReadConsole(@"
            Promise.any([Promise.reject('a'), Promise.resolve('ok'), Promise.reject('b')])
                .then(function (v) { console.log(v); });
        ");
        Assert.Equal("ok", out_);
    }

    [Fact]
    public void Any_rejects_aggregate_when_all_reject()
    {
        var out_ = RunAndReadConsole(@"
            Promise.any([Promise.reject('a'), Promise.reject('b')])
                .catch(function (e) {
                    console.log(e.name + ':' + e.errors.join(','));
                });
        ");
        Assert.Equal("AggregateError:a,b", out_);
    }

    [Fact]
    public void Any_empty_rejects_immediately()
    {
        var out_ = RunAndReadConsole(@"
            Promise.any([]).catch(function (e) { console.log(e.name); });
        ");
        Assert.Equal("AggregateError", out_);
    }

    // ========================================================
    // Promise.prototype.finally
    // ========================================================

    [Fact]
    public void Finally_runs_on_fulfill()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(42)
                .finally(function () { console.log('final'); })
                .then(function (v) { console.log('got ' + v); });
        ");
        Assert.Equal("final\ngot 42", out_);
    }

    [Fact]
    public void Finally_runs_on_reject_and_propagates_reason()
    {
        var out_ = RunAndReadConsole(@"
            Promise.reject('bad')
                .finally(function () { console.log('final'); })
                .catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("final\ncaught bad", out_);
    }

    [Fact]
    public void Finally_passes_through_fulfillment_value()
    {
        // finally's callback return value is ignored; the
        // original settlement flows through.
        var out_ = RunAndReadConsole(@"
            Promise.resolve('original')
                .finally(function () { return 'replaced'; })
                .then(function (v) { console.log(v); });
        ");
        Assert.Equal("original", out_);
    }
}
