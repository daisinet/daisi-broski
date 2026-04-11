using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsPromiseTests
{
    /// <summary>
    /// Helper that runs a script against a fresh engine and
    /// drains the microtask queue. Most promise tests need
    /// the drain step because `.then` callbacks run as
    /// microtasks — the bare <c>Evaluate</c> call doesn't
    /// wait for them.
    /// </summary>
    private static string RunAndReadConsole(string src)
    {
        var eng = new JsEngine();
        eng.RunScript(src);
        return eng.ConsoleOutput.ToString().TrimEnd('\n', '\r');
    }

    // ========================================================
    // Basic construction + synchronous settlement
    // ========================================================

    [Fact]
    public void New_promise_is_an_object()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function (resolve, reject) {});
            console.log(typeof p);
        ");
        Assert.Equal("object", out_);
    }

    [Fact]
    public void Then_runs_after_resolve()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function (resolve) { resolve(42); });
            p.then(function (v) { console.log('got ' + v); });
        ");
        Assert.Equal("got 42", out_);
    }

    [Fact]
    public void Then_is_asynchronous_not_sync()
    {
        // Classic ordering: the synchronous `console.log('after')`
        // must run before the `.then` callback.
        var out_ = RunAndReadConsole(@"
            console.log('before');
            var p = new Promise(function (resolve) { resolve(1); });
            p.then(function (v) { console.log('inside then ' + v); });
            console.log('after');
        ");
        Assert.Equal("before\nafter\ninside then 1", out_);
    }

    [Fact]
    public void Catch_runs_after_reject()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function (resolve, reject) { reject('oops'); });
            p.catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught oops", out_);
    }

    [Fact]
    public void Then_with_two_args_routes_to_correct_handler()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function (resolve, reject) { reject('bad'); });
            p.then(
                function (v) { console.log('ok ' + v); },
                function (e) { console.log('err ' + e); }
            );
        ");
        Assert.Equal("err bad", out_);
    }

    [Fact]
    public void Executor_throw_rejects_promise()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function () { throw 'boom'; });
            p.catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught boom", out_);
    }

    // ========================================================
    // Chaining
    // ========================================================

    [Fact]
    public void Then_chain_forwards_values()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(1)
                .then(function (v) { return v + 1; })
                .then(function (v) { return v * 10; })
                .then(function (v) { console.log('end=' + v); });
        ");
        Assert.Equal("end=20", out_);
    }

    [Fact]
    public void Then_handler_returning_promise_waits_for_it()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(1)
                .then(function (v) {
                    return new Promise(function (resolve) { resolve(v + 100); });
                })
                .then(function (v) { console.log('final=' + v); });
        ");
        Assert.Equal("final=101", out_);
    }

    [Fact]
    public void Then_handler_that_throws_rejects_chained_promise()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(1)
                .then(function () { throw 'bang'; })
                .catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught bang", out_);
    }

    [Fact]
    public void Catch_recovers_and_continues_chain()
    {
        var out_ = RunAndReadConsole(@"
            Promise.reject('err')
                .catch(function (e) { return 'recovered:' + e; })
                .then(function (v) { console.log(v); });
        ");
        Assert.Equal("recovered:err", out_);
    }

    [Fact]
    public void Rejection_propagates_through_then_without_handler()
    {
        var out_ = RunAndReadConsole(@"
            Promise.reject('root')
                .then(function (v) { return v + '!'; })
                .then(function (v) { return v + '?'; })
                .catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught root", out_);
    }

    // ========================================================
    // Statics
    // ========================================================

    [Fact]
    public void Promise_resolve_with_plain_value()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(7).then(function (v) { console.log(v); });
        ");
        Assert.Equal("7", out_);
    }

    [Fact]
    public void Promise_resolve_with_promise_returns_same()
    {
        var out_ = RunAndReadConsole(@"
            var p = new Promise(function (r) { r('x'); });
            var q = Promise.resolve(p);
            console.log(p === q);
        ");
        Assert.Equal("true", out_);
    }

    [Fact]
    public void Promise_reject_produces_rejected()
    {
        var out_ = RunAndReadConsole(@"
            Promise.reject('nope').catch(function (e) { console.log(e); });
        ");
        Assert.Equal("nope", out_);
    }

    [Fact]
    public void Promise_all_fulfills_with_all_results()
    {
        var out_ = RunAndReadConsole(@"
            Promise.all([Promise.resolve(1), Promise.resolve(2), Promise.resolve(3)])
                .then(function (results) { console.log(results.join(',')); });
        ");
        Assert.Equal("1,2,3", out_);
    }

    [Fact]
    public void Promise_all_mixes_values_and_promises()
    {
        var out_ = RunAndReadConsole(@"
            Promise.all([1, Promise.resolve(2), 'three'])
                .then(function (r) { console.log(r.join('|')); });
        ");
        Assert.Equal("1|2|three", out_);
    }

    [Fact]
    public void Promise_all_rejects_on_first_rejection()
    {
        var out_ = RunAndReadConsole(@"
            Promise.all([
                Promise.resolve(1),
                Promise.reject('nope'),
                Promise.resolve(3)
            ]).then(
                function () { console.log('should not run'); },
                function (e) { console.log('err=' + e); }
            );
        ");
        Assert.Equal("err=nope", out_);
    }

    [Fact]
    public void Promise_all_empty_fulfills_immediately()
    {
        var out_ = RunAndReadConsole(@"
            Promise.all([]).then(function (r) { console.log('len=' + r.length); });
        ");
        Assert.Equal("len=0", out_);
    }

    [Fact]
    public void Promise_race_fulfills_with_first()
    {
        // Both promises resolve synchronously; the race
        // settles with whichever microtask drains first,
        // which is the first in enqueue order: the earlier
        // element.
        var out_ = RunAndReadConsole(@"
            Promise.race([
                Promise.resolve('first'),
                Promise.resolve('second')
            ]).then(function (v) { console.log(v); });
        ");
        Assert.Equal("first", out_);
    }

    [Fact]
    public void Promise_race_rejects_with_first_rejection()
    {
        var out_ = RunAndReadConsole(@"
            Promise.race([
                Promise.reject('err1'),
                Promise.resolve('ok')
            ]).catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught err1", out_);
    }

    // ========================================================
    // Interaction with event loop
    // ========================================================

    [Fact]
    public void Promise_then_callback_sees_persistent_globals()
    {
        var out_ = RunAndReadConsole(@"
            var greeting = 'hello';
            Promise.resolve().then(function () {
                console.log(greeting + ' world');
            });
        ");
        Assert.Equal("hello world", out_);
    }

    [Fact]
    public void Microtask_runs_before_timer_task()
    {
        // Spec: microtasks always drain before the next
        // macrotask (setTimeout callback). So the .then
        // should log before the timer fires.
        var out_ = RunAndReadConsole(@"
            setTimeout(function () { console.log('timer'); }, 0);
            Promise.resolve().then(function () { console.log('micro'); });
        ");
        Assert.Equal("micro\ntimer", out_);
    }

    [Fact]
    public void Deep_chain_runs_in_order()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(0)
                .then(function (v) { return v + 1; })
                .then(function (v) { return v + 2; })
                .then(function (v) { return v + 3; })
                .then(function (v) { return v + 4; })
                .then(function (v) { console.log('sum=' + v); });
        ");
        Assert.Equal("sum=10", out_);
    }

    // ========================================================
    // Thenable adoption
    // ========================================================

    [Fact]
    public void Resolving_with_thenable_adopts_it()
    {
        var out_ = RunAndReadConsole(@"
            var thenable = {
                then: function (resolve, reject) { resolve('from-thenable'); }
            };
            Promise.resolve(thenable).then(function (v) { console.log(v); });
        ");
        // Note: Promise.resolve on a non-Promise object with a
        // `.then` method should follow the thenable per spec.
        Assert.Equal("from-thenable", out_);
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Promise_chain_inside_class_method()
    {
        var out_ = RunAndReadConsole(@"
            class Api {
                fetch(x) {
                    return Promise.resolve(x * 2);
                }
            }
            var api = new Api();
            api.fetch(5).then(function (r) { console.log('result=' + r); });
        ");
        Assert.Equal("result=10", out_);
    }

    [Fact]
    public void Promise_all_chained_to_sum()
    {
        var out_ = RunAndReadConsole(@"
            Promise.all([Promise.resolve(1), Promise.resolve(2), Promise.resolve(3), Promise.resolve(4)])
                .then(function (nums) {
                    return nums.reduce(function (a, b) { return a + b; }, 0);
                })
                .then(function (total) { console.log('total=' + total); });
        ");
        Assert.Equal("total=10", out_);
    }

    [Fact]
    public void Promise_chain_with_error_recovery_and_continue()
    {
        var out_ = RunAndReadConsole(@"
            Promise.resolve(1)
                .then(function (v) { throw 'bad:' + v; })
                .catch(function (e) { return 'handled:' + e; })
                .then(function (v) { console.log('finally ' + v); });
        ");
        Assert.Equal("finally handled:bad:1", out_);
    }
}
