using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsAsyncAwaitTests
{
    private static string RunAndReadConsole(string src)
    {
        var eng = new JsEngine();
        eng.RunScript(src);
        return eng.ConsoleOutput.ToString().TrimEnd('\n', '\r');
    }

    // ========================================================
    // Async function returns a promise
    // ========================================================

    [Fact]
    public void Async_function_returns_promise_object()
    {
        var out_ = RunAndReadConsole(@"
            async function f() { return 1; }
            console.log(typeof f());
        ");
        Assert.Equal("object", out_);
    }

    [Fact]
    public void Async_return_value_fulfills_promise()
    {
        var out_ = RunAndReadConsole(@"
            async function f() { return 42; }
            f().then(function (v) { console.log('got ' + v); });
        ");
        Assert.Equal("got 42", out_);
    }

    [Fact]
    public void Async_with_no_await_runs_synchronously_body()
    {
        // An async function without any awaits still returns
        // a promise, but the body runs to completion during
        // the call (it's just wrapped in a promise).
        var out_ = RunAndReadConsole(@"
            async function f() {
                console.log('inside');
                return 1;
            }
            console.log('before');
            f();
            console.log('after');
        ");
        Assert.Equal("before\ninside\nafter", out_);
    }

    [Fact]
    public void Async_throw_rejects_promise()
    {
        var out_ = RunAndReadConsole(@"
            async function f() { throw 'oops'; }
            f().catch(function (e) { console.log('caught ' + e); });
        ");
        Assert.Equal("caught oops", out_);
    }

    // ========================================================
    // await unwraps promises
    // ========================================================

    [Fact]
    public void Await_unwraps_fulfilled_promise()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                var v = await Promise.resolve(99);
                return v + 1;
            }
            f().then(function (r) { console.log(r); });
        ");
        Assert.Equal("100", out_);
    }

    [Fact]
    public void Await_plain_value_passes_through()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                var x = await 5;
                return x * 2;
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("10", out_);
    }

    [Fact]
    public void Await_after_sync_code_defers_rest()
    {
        // The code after `await` runs in a microtask, so the
        // `after` log appears before `inside`.
        var out_ = RunAndReadConsole(@"
            async function f() {
                await Promise.resolve();
                console.log('inside');
            }
            console.log('before');
            f();
            console.log('after');
        ");
        Assert.Equal("before\nafter\ninside", out_);
    }

    [Fact]
    public void Sequential_awaits_chain_values()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                var a = await Promise.resolve(1);
                var b = await Promise.resolve(a + 1);
                var c = await Promise.resolve(b + 1);
                return c;
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("3", out_);
    }

    [Fact]
    public void Deeply_sequential_awaits_accumulate()
    {
        var out_ = RunAndReadConsole(@"
            async function sum(n) {
                var total = 0;
                for (var i = 1; i <= n; i++) {
                    total = await Promise.resolve(total + i);
                }
                return total;
            }
            sum(5).then(function (v) { console.log(v); });
        ");
        Assert.Equal("15", out_);
    }

    // ========================================================
    // Error propagation
    // ========================================================

    [Fact]
    public void Await_on_rejected_promise_throws_into_body()
    {
        // Rejection at an await point propagates as a thrown
        // exception inside the async body — catchable by
        // try/catch.
        var out_ = RunAndReadConsole(@"
            async function f() {
                try {
                    await Promise.reject('nope');
                    return 'not-reached';
                } catch (e) {
                    return 'caught:' + e;
                }
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("caught:nope", out_);
    }

    [Fact]
    public void Uncaught_reject_flows_to_outer_promise()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                await Promise.reject('boom');
            }
            f().catch(function (e) { console.log('outer ' + e); });
        ");
        Assert.Equal("outer boom", out_);
    }

    [Fact]
    public void Throw_in_async_after_await_rejects()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                await Promise.resolve();
                throw 'later';
            }
            f().catch(function (e) { console.log(e); });
        ");
        Assert.Equal("later", out_);
    }

    [Fact]
    public void Try_finally_runs_on_await_error()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                try {
                    await Promise.reject('x');
                } catch (e) {
                    console.log('caught ' + e);
                }
                console.log('after');
                return 'done';
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("caught x\nafter\ndone", out_);
    }

    // ========================================================
    // Async arrow
    // ========================================================

    [Fact]
    public void Async_paren_arrow_returns_promise()
    {
        var out_ = RunAndReadConsole(@"
            var f = async (x) => x * 2;
            f(7).then(function (v) { console.log(v); });
        ");
        Assert.Equal("14", out_);
    }

    [Fact]
    public void Async_single_identifier_arrow()
    {
        var out_ = RunAndReadConsole(@"
            var f = async x => x + 1;
            f(100).then(function (v) { console.log(v); });
        ");
        Assert.Equal("101", out_);
    }

    [Fact]
    public void Async_arrow_with_await()
    {
        var out_ = RunAndReadConsole(@"
            var double = async (x) => (await Promise.resolve(x)) * 2;
            double(5).then(function (v) { console.log(v); });
        ");
        Assert.Equal("10", out_);
    }

    // ========================================================
    // Interaction with Promise.all / timing
    // ========================================================

    [Fact]
    public void Await_promise_all_resolves_to_array()
    {
        var out_ = RunAndReadConsole(@"
            async function f() {
                var results = await Promise.all([
                    Promise.resolve(1),
                    Promise.resolve(2),
                    Promise.resolve(3)
                ]);
                return results.join(',');
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("1,2,3", out_);
    }

    [Fact]
    public void Async_function_await_result_of_async_function()
    {
        var out_ = RunAndReadConsole(@"
            async function double(x) { return x * 2; }
            async function quad(x) {
                var d = await double(x);
                return await double(d);
            }
            quad(3).then(function (v) { console.log(v); });
        ");
        Assert.Equal("12", out_);
    }

    [Fact]
    public void Microtask_ordering_matches_spec()
    {
        // Classic ordering: async microtasks come before the
        // next setTimeout task.
        var out_ = RunAndReadConsole(@"
            setTimeout(function () { console.log('timer'); }, 0);
            (async function () {
                await Promise.resolve();
                console.log('after await');
            })();
            console.log('sync');
        ");
        Assert.Equal("sync\nafter await\ntimer", out_);
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Classic_async_sequence_with_error_recovery()
    {
        var out_ = RunAndReadConsole(@"
            async function fetchOrFallback(ok) {
                try {
                    var v = await (ok
                        ? Promise.resolve('data')
                        : Promise.reject('network'));
                    return 'ok:' + v;
                } catch (e) {
                    return 'fallback:' + e;
                }
            }
            fetchOrFallback(true).then(function (v) { console.log(v); });
            fetchOrFallback(false).then(function (v) { console.log(v); });
        ");
        Assert.Equal("ok:data\nfallback:network", out_);
    }

    [Fact]
    public void Async_inside_class_method()
    {
        var out_ = RunAndReadConsole(@"
            class Worker {
                async compute(x) {
                    var a = await Promise.resolve(x);
                    var b = await Promise.resolve(a * 2);
                    return b + 1;
                }
            }
            new Worker().compute(5).then(function (v) { console.log(v); });
        ");
        Assert.Equal("11", out_);
    }

    [Fact]
    public void Await_reduce_to_sum()
    {
        var out_ = RunAndReadConsole(@"
            async function asyncSum(arr) {
                var total = 0;
                for (var x of arr) {
                    total += await Promise.resolve(x);
                }
                return total;
            }
            asyncSum([10, 20, 30, 40]).then(function (v) { console.log(v); });
        ");
        Assert.Equal("100", out_);
    }

    [Fact]
    public void Async_parallel_via_promise_all()
    {
        var out_ = RunAndReadConsole(@"
            async function task(n) { return await Promise.resolve(n * n); }
            async function run() {
                var results = await Promise.all([task(2), task(3), task(4)]);
                return results.reduce(function (a, b) { return a + b; }, 0);
            }
            run().then(function (v) { console.log(v); });
        ");
        Assert.Equal("29", out_);
    }

    [Fact]
    public void Return_from_async_adopts_returned_promise()
    {
        // Returning a promise from an async function chains
        // the outer promise to it.
        var out_ = RunAndReadConsole(@"
            async function f() {
                return Promise.resolve('deep');
            }
            f().then(function (v) { console.log(v); });
        ");
        Assert.Equal("deep", out_);
    }

    // ========================================================
    // Parse-time errors
    // ========================================================

    [Fact]
    public void Await_outside_async_is_identifier_not_keyword()
    {
        // Outside an async body, `await` is a plain identifier.
        // Using it as one should work.
        Assert.Equal(
            5.0,
            new JsEngine().Evaluate("var await = 5; await;"));
    }
}
