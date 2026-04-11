using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsGeneratorTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Basic function* and yield
    // ========================================================

    [Fact]
    public void Generator_returns_iterator_object()
    {
        // Calling a generator function doesn't execute the body —
        // it returns a generator object that acts as an iterator.
        Assert.Equal(
            "object",
            Eval(@"
                function* gen() { yield 1; }
                typeof gen();
            "));
    }

    [Fact]
    public void First_next_yields_first_value_not_done()
    {
        Assert.Equal(
            "1:false",
            Eval(@"
                function* gen() { yield 1; }
                var g = gen();
                var r = g.next();
                r.value + ':' + r.done;
            "));
    }

    [Fact]
    public void Two_successive_next_calls()
    {
        Assert.Equal(
            "1,2",
            Eval(@"
                function* gen() {
                    yield 1;
                    yield 2;
                }
                var g = gen();
                g.next().value + ',' + g.next().value;
            "));
    }

    [Fact]
    public void Next_after_return_reports_done_true()
    {
        Assert.Equal(
            "true",
            Eval(@"
                function* gen() {
                    yield 1;
                }
                var g = gen();
                g.next();           // {value: 1, done: false}
                var r = g.next();   // done
                r.done + '';
            "));
    }

    [Fact]
    public void Generator_return_statement_populates_final_value()
    {
        Assert.Equal(
            "final:true",
            Eval(@"
                function* gen() {
                    yield 1;
                    return 'final';
                }
                var g = gen();
                g.next();
                var r = g.next();
                r.value + ':' + r.done;
            "));
    }

    [Fact]
    public void Yield_with_no_argument_yields_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                function* gen() { yield; }
                typeof gen().next().value;
            "));
    }

    // ========================================================
    // Sent values (gen.next(arg))
    // ========================================================

    [Fact]
    public void Sent_value_becomes_yield_expression_result()
    {
        Assert.Equal(
            "hello",
            Eval(@"
                function* gen() {
                    var received = yield 1;
                    yield received;
                }
                var g = gen();
                g.next();              // {value: 1, done: false}
                g.next('hello').value; // the sent value comes back
            "));
    }

    [Fact]
    public void Sent_value_on_first_next_is_ignored()
    {
        // Spec: the very first call to next() discards its
        // argument because there's no suspended yield waiting
        // for it.
        Assert.Equal(
            1.0,
            Eval(@"
                function* gen() { yield 1; }
                var g = gen();
                g.next('ignored').value;
            "));
    }

    // ========================================================
    // Generator body uses local state / closures
    // ========================================================

    [Fact]
    public void Generator_runs_body_with_full_control_flow()
    {
        Assert.Equal(
            "0,1,2,3",
            Eval(@"
                function* count(n) {
                    for (var i = 0; i < n; i++) yield i;
                }
                var out = [];
                var g = count(4);
                var step = g.next();
                while (!step.done) {
                    out.push(step.value);
                    step = g.next();
                }
                out.join(',');
            "));
    }

    [Fact]
    public void Generator_closure_captures_outer_variable()
    {
        Assert.Equal(
            "10,11,12",
            Eval(@"
                var base = 10;
                function* gen() {
                    for (var i = 0; i < 3; i++) yield base + i;
                }
                var out = [];
                var g = gen();
                var s = g.next();
                while (!s.done) { out.push(s.value); s = g.next(); }
                out.join(',');
            "));
    }

    [Fact]
    public void Generator_yields_computed_expression()
    {
        Assert.Equal(
            "4,9",
            Eval(@"
                function* squares() {
                    yield 2 * 2;
                    yield 3 * 3;
                }
                var g = squares();
                g.next().value + ',' + g.next().value;
            "));
    }

    // ========================================================
    // Iteration (generator as iterable)
    // ========================================================

    [Fact]
    public void Generator_is_iterable_via_symbol_iterator()
    {
        // gen[Symbol.iterator]() returns gen itself.
        Assert.True((bool)Eval(@"
            function* g() { yield 1; }
            var it = g();
            it[Symbol.iterator]() === it;
        ")!);
    }

    [Fact]
    public void For_of_over_generator()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function* g() {
                    yield 1; yield 2; yield 3;
                }
                var total = 0;
                for (var n of g()) total += n;
                total;
            "));
    }

    [Fact]
    public void Spread_generator_into_array()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                function* g() { yield 1; yield 2; yield 3; }
                [...g()].join(',');
            "));
    }

    [Fact]
    public void Spread_generator_into_function_call()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function* g() { yield 1; yield 2; yield 3; }
                function sum3(a, b, c) { return a + b + c; }
                sum3(...g());
            "));
    }

    // ========================================================
    // Error paths
    // ========================================================

    [Fact]
    public void Yield_outside_generator_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval(@"
                function plain() { yield 1; }
            "));
    }

    [Fact]
    public void Yield_inside_nested_plain_function_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval(@"
                function* outer() {
                    function inner() { yield 1; }
                    yield inner;
                }
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Infinite_generator_taken_lazily()
    {
        Assert.Equal(
            "0,1,2,3,4",
            Eval(@"
                function* nats() {
                    var i = 0;
                    while (true) yield i++;
                }
                var g = nats();
                var out = [];
                for (var i = 0; i < 5; i++) out.push(g.next().value);
                out.join(',');
            "));
    }

    [Fact]
    public void Generator_fibonacci_with_destructuring_swap()
    {
        Assert.Equal(
            "0,1,1,2,3,5,8",
            Eval(@"
                function* fib() {
                    var a = 0, b = 1;
                    while (true) {
                        yield a;
                        var t = a + b;
                        a = b;
                        b = t;
                    }
                }
                var g = fib();
                var out = [];
                for (var i = 0; i < 7; i++) out.push(g.next().value);
                out.join(',');
            "));
    }

    [Fact]
    public void Generator_composed_with_map_via_spread()
    {
        Assert.Equal(
            "1,4,9,16",
            Eval(@"
                function* nums() { yield 1; yield 2; yield 3; yield 4; }
                [...nums()].map(function (n) { return n * n; }).join(',');
            "));
    }

    [Fact]
    public void Classic_send_pattern()
    {
        // A ping-pong pattern where next() sends a value
        // and gets a transformed response.
        Assert.Equal(
            "got:hello",
            Eval(@"
                function* echo() {
                    while (true) {
                        var msg = yield;
                        yield 'got:' + msg;
                    }
                }
                var g = echo();
                g.next();                  // prime
                g.next('hello').value;     // send, receive ack
            "));
    }
}
