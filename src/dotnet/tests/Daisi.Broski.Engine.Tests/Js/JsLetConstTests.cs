using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsLetConstTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- basic declarations --------

    [Fact]
    public void Let_declares_and_reads_like_var()
    {
        var eng = new JsEngine();
        eng.Evaluate("let x = 10;");
        Assert.Equal(10.0, eng.Globals["x"]);
    }

    [Fact]
    public void Const_declares_and_reads_like_var()
    {
        var eng = new JsEngine();
        eng.Evaluate("const y = 42;");
        Assert.Equal(42.0, eng.Globals["y"]);
    }

    [Fact]
    public void Let_without_initializer_is_undefined()
    {
        var eng = new JsEngine();
        eng.Evaluate("let x;");
        Assert.IsType<JsUndefined>(eng.Globals["x"]);
    }

    [Fact]
    public void Multiple_declarators_in_one_let_statement()
    {
        var eng = new JsEngine();
        eng.Evaluate("let a = 1, b = 2, c = a + b;");
        Assert.Equal(1.0, eng.Globals["a"]);
        Assert.Equal(2.0, eng.Globals["b"]);
        Assert.Equal(3.0, eng.Globals["c"]);
    }

    // -------- block scoping --------

    [Fact]
    public void Let_is_block_scoped_and_not_visible_outside()
    {
        // `x` exists only inside the block; outside it should
        // resolve to the outer scope (which has no `x` here,
        // so the assignment creates a global).
        Assert.Equal(
            "undefined",
            Eval(@"
                { let x = 1; }
                typeof x;
            "));
    }

    [Fact]
    public void Var_is_NOT_block_scoped()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                { var x = 1; }
                x;
            "));
    }

    [Fact]
    public void Inner_block_shadows_outer_let()
    {
        Assert.Equal(
            "outer:1 inner:2 after:1",
            Eval(@"
                let x = 1;
                let out = 'outer:' + x;
                {
                    let x = 2;
                    out = out + ' inner:' + x;
                }
                out + ' after:' + x;
            "));
    }

    [Fact]
    public void Const_is_block_scoped()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                { const PI = 3.14; }
                typeof PI;
            "));
    }

    // -------- temporal dead zone --------

    [Fact]
    public void Accessing_let_before_declaration_throws_ReferenceError()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                {
                    x;
                    let x = 5;
                }
            "));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("ReferenceError", err.Get("name"));
    }

    [Fact]
    public void Typeof_of_let_in_TDZ_throws_ReferenceError()
    {
        // Unlike undeclared identifiers (where typeof returns
        // "undefined"), typeof on a binding in its TDZ throws.
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                {
                    typeof x;
                    let x = 1;
                }
            "));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("ReferenceError", err.Get("name"));
    }

    [Fact]
    public void Outer_let_shadowed_by_inner_TDZ_still_throws()
    {
        // Inner block pre-declares x as uninitialized, so
        // accessing it before the let runs throws even though
        // the outer scope has a valid x.
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                let x = 1;
                {
                    x;
                    let x = 2;
                }
            "));
    }

    [Fact]
    public void After_declaration_let_is_readable()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                {
                    let x = 5;
                    x;
                }
            "));
    }

    // -------- for-loop let --------

    [Fact]
    public void Let_in_for_loop_header_is_scoped_to_loop()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                for (let i = 0; i < 3; i++) {}
                typeof i;
            "));
    }

    [Fact]
    public void Let_in_for_loop_counter_sums_correctly()
    {
        Assert.Equal(
            45.0,
            Eval(@"
                let sum = 0;
                for (let i = 0; i < 10; i++) { sum = sum + i; }
                sum;
            "));
    }

    [Fact]
    public void Let_in_for_loop_with_break()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                let found = -1;
                for (let i = 0; i < 10; i++) {
                    if (i === 3) { found = i; break; }
                }
                found;
            "));
    }

    // -------- nested functions --------

    [Fact]
    public void Let_inside_function_body()
    {
        Assert.Equal(
            7.0,
            Eval(@"
                function f() {
                    let x = 7;
                    return x;
                }
                f();
            "));
    }

    [Fact]
    public void Let_in_function_body_not_visible_to_caller()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function f() { let secret = 42; }
            f();
        ");
        Assert.False(eng.Globals.ContainsKey("secret"));
    }

    [Fact]
    public void Let_captured_by_inner_closure()
    {
        // Inner function reads the outer let when called later.
        Assert.Equal(
            10.0,
            Eval(@"
                function outer() {
                    let count = 10;
                    function inner() { return count; }
                    return inner();
                }
                outer();
            "));
    }

    // -------- let and var interaction --------

    [Fact]
    public void Var_outside_and_let_inside_block_dont_conflict()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var x = 1;
            {
                let x = 2;
                x;  // inside block: 2
            }
        ");
        // Outside the block, x is still the var.
        Assert.Equal(1.0, eng.Globals["x"]);
    }

    // -------- nested block scopes --------

    [Fact]
    public void Doubly_nested_blocks_each_have_their_own_scope()
    {
        Assert.Equal(
            "1 2 3",
            Eval(@"
                let out = '';
                {
                    let a = 1;
                    out = '' + a;
                    {
                        let a = 2;
                        out = out + ' ' + a;
                        {
                            let a = 3;
                            out = out + ' ' + a;
                        }
                    }
                }
                out;
            "));
    }

    // -------- catch parameter is already block-scoped --------

    [Fact]
    public void Catch_parameter_interacts_with_block_let_correctly()
    {
        // This already worked via slice 5's PushEnv; verify the
        // slice 3b-1 changes didn't regress it.
        Assert.Equal(
            "boom",
            Eval(@"
                try { throw 'boom'; } catch (e) { e; }
            "));
    }
}
