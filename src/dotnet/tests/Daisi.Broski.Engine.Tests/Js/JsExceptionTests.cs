using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsExceptionTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- basic throw + catch --------

    [Fact]
    public void Catch_a_primitive_thrown_value()
    {
        Assert.Equal(
            42.0,
            Eval("var r; try { throw 42; } catch (e) { r = e; } r;"));
    }

    [Fact]
    public void Catch_a_thrown_string()
    {
        Assert.Equal(
            "boom",
            Eval("var r; try { throw 'boom'; } catch (e) { r = e; } r;"));
    }

    [Fact]
    public void Catch_a_thrown_object_preserves_identity()
    {
        Assert.Equal(
            "oops",
            Eval(@"
                var r;
                try {
                    throw {name: 'CustomError', message: 'oops'};
                } catch (e) {
                    r = e.message;
                }
                r;
            "));
    }

    [Fact]
    public void Catch_parameter_is_block_scoped()
    {
        // After the catch, e should not be visible to the outer
        // scope.
        Assert.Equal(
            "undefined",
            Eval(@"
                try { throw 1; } catch (e) { }
                typeof e;
            "));
    }

    [Fact]
    public void Code_after_throw_in_try_body_is_skipped()
    {
        Assert.Equal(
            "caught",
            Eval(@"
                var r = 'initial';
                try {
                    throw 1;
                    r = 'unreachable';
                } catch (e) {
                    r = 'caught';
                }
                r;
            "));
    }

    [Fact]
    public void Catch_does_not_run_when_try_does_not_throw()
    {
        Assert.Equal(
            "ok",
            Eval(@"
                var r;
                try { r = 'ok'; } catch (e) { r = 'catch'; }
                r;
            "));
    }

    // -------- rethrow / propagation --------

    [Fact]
    public void Rethrow_from_catch_propagates_outward()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                var r;
                try {
                    try {
                        throw 1;
                    } catch (e) {
                        throw 2;
                    }
                } catch (e) {
                    r = e;
                }
                r;
            "));
    }

    [Fact]
    public void Uncaught_throw_escapes_to_the_host()
    {
        var ex = Assert.Throws<JsRuntimeException>(() => Eval("throw 'boom';"));
        Assert.Equal("boom", ex.JsValue);
    }

    [Fact]
    public void Uncaught_error_object_is_attached_to_the_host_exception()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval("throw {name: 'MyError', message: 'bad'};"));
        var obj = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("MyError", obj.Properties["name"]);
        Assert.Equal("bad", obj.Properties["message"]);
    }

    // -------- nested try / frame unwinding --------

    [Fact]
    public void Inner_try_catches_before_outer()
    {
        Assert.Equal(
            "inner",
            Eval(@"
                var r;
                try {
                    try {
                        throw 1;
                    } catch (e) {
                        r = 'inner';
                    }
                } catch (e) {
                    r = 'outer';
                }
                r;
            "));
    }

    [Fact]
    public void Throw_in_function_call_unwinds_through_the_frame()
    {
        Assert.Equal(
            "caught",
            Eval(@"
                function boom() { throw 'inside function'; }
                var r;
                try { boom(); } catch (e) { r = 'caught'; }
                r;
            "));
    }

    [Fact]
    public void Nested_function_throw_unwinds_through_multiple_frames()
    {
        Assert.Equal(
            "top",
            Eval(@"
                function inner() { throw 'x'; }
                function middle() { inner(); return 'not reached'; }
                function outer() { middle(); return 'not reached'; }
                var r;
                try { outer(); } catch (e) { r = 'top'; }
                r;
            "));
    }

    // -------- VM errors become catchable --------

    [Fact]
    public void Reference_error_from_undeclared_read_is_catchable()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate(@"
            var captured;
            try { someUndeclaredVar; } catch (e) { captured = e.name; }
            captured;
        ");
        Assert.Equal("ReferenceError", result);
    }

    [Fact]
    public void Type_error_from_non_function_call_is_catchable()
    {
        var result = Eval(@"
            var captured;
            try { var x = 1; x(); } catch (e) { captured = e.name; }
            captured;
        ");
        Assert.Equal("TypeError", result);
    }

    [Fact]
    public void Type_error_from_property_access_on_null_is_catchable()
    {
        var result = Eval(@"
            var captured;
            try { var o = null; o.foo; } catch (e) { captured = e.name; }
            captured;
        ");
        Assert.Equal("TypeError", result);
    }

    [Fact]
    public void Type_error_message_is_accessible()
    {
        var result = Eval(@"
            var msg;
            try { notDeclared; } catch (e) { msg = e.message; }
            msg;
        ");
        Assert.Equal("notDeclared is not defined", result);
    }

    // -------- try / finally --------

    [Fact]
    public void Finally_runs_after_normal_completion()
    {
        Assert.Equal(
            "body+finally",
            Eval(@"
                var r = '';
                try { r = 'body'; } finally { r = r + '+finally'; }
                r;
            "));
    }

    [Fact]
    public void Finally_runs_after_throw_and_rethrows()
    {
        // Outer catches the re-thrown exception.
        Assert.Equal(
            "caught:1|finally-ran",
            Eval(@"
                var r = '';
                try {
                    try {
                        throw 1;
                    } finally {
                        r = 'finally-ran';
                    }
                } catch (e) {
                    r = 'caught:' + e + '|' + r;
                }
                r;
            "));
    }

    // -------- try / catch / finally --------

    [Fact]
    public void Catch_and_finally_both_run_on_throw()
    {
        Assert.Equal(
            "catch+finally",
            Eval(@"
                var r = '';
                try {
                    throw 1;
                } catch (e) {
                    r = r + 'catch';
                } finally {
                    r = r + '+finally';
                }
                r;
            "));
    }

    [Fact]
    public void Finally_runs_even_when_try_completes_normally()
    {
        Assert.Equal(
            "body+finally",
            Eval(@"
                var r = '';
                try {
                    r = 'body';
                } catch (e) {
                    r = 'nope';
                } finally {
                    r = r + '+finally';
                }
                r;
            "));
    }

    [Fact]
    public void Throw_inside_catch_still_runs_finally()
    {
        Assert.Equal(
            "finally-ran",
            Eval(@"
                var r = 'initial';
                try {
                    try {
                        throw 1;
                    } catch (e) {
                        throw 2;
                    } finally {
                        r = 'finally-ran';
                    }
                } catch (e) {
                    /* swallow the re-thrown value */
                }
                r;
            "));
    }

    // -------- error name / message --------

    [Fact]
    public void Error_object_has_expected_shape()
    {
        // The phase-3a error uses a plain JsObject with name +
        // message. A real Error constructor arrives in slice 6.
        var result = Eval(@"
            var e;
            try { undeclared; } catch (err) { e = err; }
            e.name + ':' + e.message;
        ");
        Assert.Equal("ReferenceError:undeclared is not defined", result);
    }
}
