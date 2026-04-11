using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsArrowFunctionTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- syntax forms --------

    [Fact]
    public void Single_identifier_param_concise_body()
    {
        Assert.Equal(
            25.0,
            Eval("var square = x => x * x; square(5);"));
    }

    [Fact]
    public void Parenthesized_params_concise_body()
    {
        Assert.Equal(
            7.0,
            Eval("var add = (a, b) => a + b; add(3, 4);"));
    }

    [Fact]
    public void Empty_params_concise_body()
    {
        Assert.Equal(
            42.0,
            Eval("var answer = () => 42; answer();"));
    }

    [Fact]
    public void Block_body_with_return_statement()
    {
        Assert.Equal(
            6.0,
            Eval("var f = x => { return x + x; }; f(3);"));
    }

    [Fact]
    public void Block_body_with_multiple_statements()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                var f = x => {
                    var y = x + 1;
                    var z = y + 1;
                    return x + y + z;
                };
                f(4);
            "));
    }

    [Fact]
    public void Block_body_without_return_returns_undefined()
    {
        Assert.IsType<JsUndefined>(
            Eval("var f = () => {}; f();"));
    }

    // -------- typeof --------

    [Fact]
    public void Typeof_arrow_is_function()
    {
        Assert.Equal(
            "function",
            Eval("typeof (x => x);"));
    }

    // -------- lexical this --------

    [Fact]
    public void Arrow_inherits_this_from_enclosing_method()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                var obj = {
                    value: 10,
                    get: function () {
                        var arrow = () => this.value;
                        return arrow();
                    }
                };
                obj.get();
            "));
    }

    [Fact]
    public void Arrow_this_is_not_overridden_by_call_site()
    {
        // Even if the arrow is called as a method on a different
        // object, `this` stays bound to the enclosing function's
        // `this`.
        Assert.Equal(
            10.0,
            Eval(@"
                var a = {value: 10};
                var b = {value: 99};
                var arrow;
                (function () { arrow = () => this.value; }).call(a);
                // Call via b.m: lexical this is still a.
                b.m = arrow;
                b.m();
            "));
    }

    [Fact]
    public void Arrow_in_forEach_callback_sees_outer_this()
    {
        // Classic use case: Array.prototype.forEach with an
        // arrow preserves the outer method's `this`.
        // factor=2, elements=[1,2,3] → 1*2 + 2*2 + 3*2 = 12.
        Assert.Equal(
            12.0,
            Eval(@"
                var obj = {
                    factor: 2,
                    total: 0,
                    run: function () {
                        [1, 2, 3].forEach(x => {
                            this.total = this.total + x * this.factor;
                        });
                        return this.total;
                    }
                };
                obj.run();
            "));
    }

    // -------- arguments --------

    [Fact]
    public void Arrow_arguments_is_inherited_from_outer_function()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                function outer() {
                    var arrow = () => arguments[0];
                    return arrow();
                }
                outer(42);
            "));
    }

    [Fact]
    public void Arrow_does_not_have_its_own_arguments()
    {
        // Calling an arrow with args and then reading
        // arguments from within it: arguments is the OUTER
        // function's (because arrow has no own arguments).
        Assert.Equal(
            1.0,
            Eval(@"
                function outer() {
                    var arrow = (a, b) => arguments[0];
                    // arrow is called with its own args (9, 10),
                    // but arguments refers to outer's args.
                    return arrow(9, 10);
                }
                outer(1, 2);
            "));
    }

    // -------- new --------

    [Fact]
    public void New_on_arrow_throws_TypeError()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval("var f = () => ({}); new f();"));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("TypeError", err.Get("name"));
    }

    // -------- closure over outer vars --------

    [Fact]
    public void Arrow_captures_outer_let_binding()
    {
        // Arrow functions are lexically scoped like all
        // functions — they see surrounding let/const bindings.
        Assert.Equal(
            "hello",
            Eval(@"
                let greeting = 'hello';
                var f = () => greeting;
                f();
            "));
    }

    [Fact]
    public void Arrow_in_function_expression_captures_var()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var make = function () {
                    var x = 42;
                    return () => x;
                };
                make()();
            "));
    }

    // -------- chained / higher-order --------

    [Fact]
    public void Arrow_passed_to_map()
    {
        Assert.Equal(
            "2,4,6",
            Eval("[1, 2, 3].map(x => x * 2).join(',');"));
    }

    [Fact]
    public void Arrow_with_filter_and_reduce()
    {
        Assert.Equal(
            220.0,
            Eval(@"
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
                    .filter(x => x % 2 === 0)
                    .map(x => x * x)
                    .reduce((a, b) => a + b, 0);
            "));
    }

    [Fact]
    public void Arrow_returning_arrow_currying()
    {
        Assert.Equal(
            6.0,
            Eval("var add = a => b => a + b; add(2)(4);"));
    }

    [Fact]
    public void Arrow_with_zero_params_in_setTimeout()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var msg = 'fired';
            setTimeout(() => { console.log(msg); }, 0);
        ");
        Assert.Equal("fired\n", eng.ConsoleOutput.ToString());
    }

    // -------- parenthesized concise body with object literal --------

    [Fact]
    public void Parenthesized_object_literal_concise_body()
    {
        // Without the parens, `x => {a: 1}` would parse as a
        // block. The parens force expression form.
        var eng = new JsEngine();
        eng.Evaluate("var make = x => ({val: x}); var o = make(5);");
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(5.0, o.Properties["val"]);
    }

    // -------- parenthesized expression still works --------

    [Fact]
    public void Parenthesized_expression_is_not_an_arrow()
    {
        // `(x + 1)` without a trailing `=>` must parse as a
        // normal parenthesized expression, not an arrow.
        Assert.Equal(
            6.0,
            Eval("var x = 5; (x + 1);"));
    }
}
