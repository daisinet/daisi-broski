using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsGeneratorPolishTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // gen.return()
    // ========================================================

    [Fact]
    public void Return_completes_generator()
    {
        Assert.Equal(
            "done/42",
            Eval(@"
                function* gen() { yield 1; yield 2; yield 3; }
                var g = gen();
                g.next();  // first yield
                var r = g.return(42);
                (r.done ? 'done' : 'not') + '/' + r.value;
            "));
    }

    [Fact]
    public void Return_on_completed_is_noop()
    {
        Assert.Equal(
            "true",
            Eval(@"
                function* g() {}
                var it = g();
                it.next();
                var r = it.return(5);
                r.done + '';
            "));
    }

    [Fact]
    public void Return_makes_subsequent_next_done()
    {
        Assert.Equal(
            "true",
            Eval(@"
                function* g() { yield 1; yield 2; }
                var it = g();
                it.next();
                it.return();
                var r = it.next();
                r.done + '';
            "));
    }

    // ========================================================
    // gen.throw()
    // ========================================================

    [Fact]
    public void Throw_caught_by_try_catch_in_generator()
    {
        Assert.Equal(
            "caught:oops",
            Eval(@"
                function* g() {
                    try {
                        yield 1;
                    } catch (e) {
                        yield 'caught:' + e;
                    }
                }
                var it = g();
                it.next();
                it.throw('oops').value;
            "));
    }

    [Fact]
    public void Throw_uncaught_propagates_to_caller()
    {
        // Rethrown from gen.throw — the test engine should
        // surface it as a JsRuntimeException from Evaluate.
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                function* g() { yield 1; }
                var it = g();
                it.next();
                it.throw('boom');
            "));
    }

    // ========================================================
    // yield* delegation
    // ========================================================

    [Fact]
    public void YieldStar_over_array()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                function* g() { yield* [1, 2, 3]; }
                [...g()].join(',');
            "));
    }

    [Fact]
    public void YieldStar_over_string()
    {
        Assert.Equal(
            "h,i",
            Eval(@"
                function* g() { yield* 'hi'; }
                [...g()].join(',');
            "));
    }

    [Fact]
    public void YieldStar_composed_with_own_yields()
    {
        Assert.Equal(
            "a,b,c,d",
            Eval(@"
                function* g() {
                    yield 'a';
                    yield* ['b', 'c'];
                    yield 'd';
                }
                [...g()].join(',');
            "));
    }

    [Fact]
    public void YieldStar_over_another_generator()
    {
        Assert.Equal(
            "1,2,3,4",
            Eval(@"
                function* inner() { yield 1; yield 2; }
                function* outer() {
                    yield* inner();
                    yield 3;
                    yield 4;
                }
                [...outer()].join(',');
            "));
    }

    [Fact]
    public void YieldStar_with_for_of()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                function* g() { yield* [1, 2, 3, 4, 5]; }
                var total = 0;
                for (var n of g()) total += n;
                total;
            "));
    }
}
