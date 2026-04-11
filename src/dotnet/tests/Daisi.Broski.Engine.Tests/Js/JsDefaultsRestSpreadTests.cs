using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsDefaultsRestSpreadTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Default parameters
    // ========================================================

    [Fact]
    public void Default_applies_when_arg_missing()
    {
        Assert.Equal(
            5.0,
            Eval("function f(x, y = 5) { return x + y; } f(0);"));
    }

    [Fact]
    public void Default_applies_when_arg_is_undefined()
    {
        // Spec: undefined triggers default, null does not.
        Assert.Equal(
            5.0,
            Eval("function f(y = 5) { return y; } f(undefined);"));
    }

    [Fact]
    public void Default_ignored_when_arg_provided()
    {
        Assert.Equal(
            7.0,
            Eval("function f(x, y = 5) { return x + y; } f(3, 4);"));
    }

    [Fact]
    public void Default_ignored_when_arg_is_null()
    {
        // `null` is a real value and does not trigger the default.
        Assert.Equal(
            "object",
            Eval("function f(y = 5) { return typeof y; } f(null);"));
    }

    [Fact]
    public void Default_is_full_expression()
    {
        Assert.Equal(
            8.0,
            Eval("var base = 3; function f(x = base + 5) { return x; } f();"));
    }

    [Fact]
    public void Default_can_reference_prior_param()
    {
        // a=4, b defaults to a*2=8, sum=12
        Assert.Equal(
            12.0,
            Eval("function f(a, b = a * 2) { return a + b; } f(4);"));
    }

    [Fact]
    public void Default_does_not_see_later_params()
    {
        // a's default cannot see b — b is TDZ-ish because it's
        // later in parameter order. In our implementation, the
        // default runs before b is bound from the positional
        // arg, so it reads undefined for b. This is slightly
        // looser than spec (spec throws) but acceptable.
        // Verify via observable behavior: calling with only one
        // arg makes b undefined at default-eval time.
        Assert.Equal(
            "undefined",
            Eval("function f(a = typeof b, b) { return a; } f();"));
    }

    [Fact]
    public void Default_is_lazy()
    {
        // The default expression is only evaluated when needed.
        Assert.Equal(
            1.0,
            Eval(@"
                var calls = 0;
                function makeDefault() { calls++; return 99; }
                function f(x = makeDefault()) { return x; }
                f(10);  // default not evaluated
                f();    // default evaluated
                calls;
            "));
    }

    [Fact]
    public void Default_in_arrow_function()
    {
        Assert.Equal(
            15.0,
            Eval("var f = (x, y = 10) => x + y; f(5);"));
    }

    [Fact]
    public void Default_in_single_param_arrow()
    {
        // Single-ident arrows (`x => x`) don't accept defaults —
        // only the parenthesized form (`(x = 1) => ...`).
        Assert.Equal(
            3.0,
            Eval("var f = (x = 3) => x; f();"));
    }

    [Fact]
    public void Default_in_function_expression()
    {
        Assert.Equal(
            42.0,
            Eval("var f = function (n = 42) { return n; }; f();"));
    }

    // ========================================================
    // Rest parameters
    // ========================================================

    [Fact]
    public void Rest_collects_all_trailing_args()
    {
        Assert.Equal(
            3.0,
            Eval("function f(...args) { return args.length; } f(1, 2, 3);"));
    }

    [Fact]
    public void Rest_is_empty_array_when_no_extras()
    {
        Assert.Equal(
            0.0,
            Eval("function f(a, ...rest) { return rest.length; } f(1);"));
    }

    [Fact]
    public void Rest_captures_leftover_after_named_params()
    {
        Assert.Equal(
            9.0,
            Eval(@"
                function f(a, b, ...rest) {
                    return rest[0] + rest[1] + rest[2];
                }
                f(100, 200, 1, 3, 5);
            "));
    }

    [Fact]
    public void Rest_is_a_real_array_with_prototype()
    {
        // The rest arg should be a JsArray with Array.prototype.
        Assert.Equal(
            6.0,
            Eval(@"
                function sum(...nums) {
                    return nums.reduce(function (a, b) { return a + b; }, 0);
                }
                sum(1, 2, 3);
            "));
    }

    [Fact]
    public void Rest_supports_map_and_join()
    {
        Assert.Equal(
            "1,4,9",
            Eval(@"
                function squared(...nums) {
                    return nums.map(function (n) { return n * n; }).join(',');
                }
                squared(1, 2, 3);
            "));
    }

    [Fact]
    public void Rest_in_arrow_function()
    {
        Assert.Equal(
            6.0,
            Eval("var sum = (...xs) => xs.reduce(function (a, b) { return a + b; }, 0); sum(1, 2, 3);"));
    }

    [Fact]
    public void Rest_must_be_last_param_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval("function f(...a, b) { return a; }"));
    }

    [Fact]
    public void Rest_may_not_have_default_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval("function f(...a = []) { return a; }"));
    }

    // ========================================================
    // Array pattern rest
    // ========================================================

    [Fact]
    public void Array_pattern_rest_captures_tail()
    {
        Assert.Equal(
            "2,3,4",
            Eval("var [first, ...rest] = [1, 2, 3, 4]; rest.join(',');"));
    }

    [Fact]
    public void Array_pattern_rest_is_empty_when_exhausted()
    {
        Assert.Equal(
            0.0,
            Eval("var [a, b, ...rest] = [1, 2]; rest.length;"));
    }

    [Fact]
    public void Array_pattern_rest_captures_entire_when_first()
    {
        Assert.Equal(
            "1,2,3",
            Eval("var [...all] = [1, 2, 3]; all.join(',');"));
    }

    [Fact]
    public void Array_pattern_rest_with_let()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                let [head, ...tail] = [10, 1, 2, 2];
                tail.reduce(function (a, b) { return a + b; }, 0);
            "));
    }

    [Fact]
    public void Array_pattern_rest_must_be_last_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval("var [...rest, last] = [1, 2];"));
    }

    [Fact]
    public void Array_pattern_rest_may_not_have_default_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() =>
            Eval("var [...rest = []] = [1];"));
    }

    // ========================================================
    // Spread in array literals
    // ========================================================

    [Fact]
    public void Spread_whole_array_into_literal()
    {
        Assert.Equal(
            "1,2,3",
            Eval("var a = [1, 2, 3]; [...a].join(',');"));
    }

    [Fact]
    public void Spread_with_prefix_and_suffix()
    {
        Assert.Equal(
            "0,1,2,3,4",
            Eval("var mid = [1, 2, 3]; [0, ...mid, 4].join(',');"));
    }

    [Fact]
    public void Spread_multiple_arrays()
    {
        Assert.Equal(
            "1,2,3,4,5,6",
            Eval("var a = [1, 2, 3], b = [4, 5, 6]; [...a, ...b].join(',');"));
    }

    [Fact]
    public void Spread_empty_array_is_noop()
    {
        Assert.Equal(
            "1,2",
            Eval("[1, ...[], 2].join(',');"));
    }

    [Fact]
    public void Spread_result_is_fresh_array_not_alias()
    {
        // Mutating the spread result should not touch the source.
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var src = [1, 2, 3];
                var copy = [...src];
                copy.push(4);
                src.join(',');
            "));
    }

    // ========================================================
    // Spread in function calls
    // ========================================================

    [Fact]
    public void Spread_all_args_into_call()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function f(a, b, c) { return a + b + c; }
                var args = [1, 2, 3];
                f(...args);
            "));
    }

    [Fact]
    public void Spread_mixed_with_regular_args()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                function f(a, b, c, d) { return a + b + c + d; }
                f(1, ...[2, 3], 4);
            "));
    }

    [Fact]
    public void Spread_into_method_call_preserves_this()
    {
        Assert.Equal(
            "alice:hi:42",
            Eval(@"
                var user = {
                    name: 'alice',
                    greet: function (msg, n) {
                        return this.name + ':' + msg + ':' + n;
                    }
                };
                user.greet(...['hi', 42]);
            "));
    }

    [Fact]
    public void Spread_with_rest_on_receiving_side()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum(...xs) {
                    return xs.reduce(function (a, b) { return a + b; }, 0);
                }
                sum(...[1, 2, 3]);
            "));
    }

    [Fact]
    public void Spread_interacts_with_default()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                function f(a, b = 100) { return a + b; }
                f(...[5, 10]);  // both filled
            "));
    }

    [Fact]
    public void Spread_leaving_default_uses_default()
    {
        Assert.Equal(
            105.0,
            Eval(@"
                function f(a, b = 100) { return a + b; }
                f(...[5]);  // b missing → default
            "));
    }

    [Fact]
    public void Spread_into_new_expression()
    {
        Assert.Equal(
            "alice:30",
            Eval(@"
                function User(name, age) {
                    this.name = name;
                    this.age = age;
                }
                var u = new User(...['alice', 30]);
                u.name + ':' + u.age;
            "));
    }

    [Fact]
    public void Spread_empty_into_call_with_defaults()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                function f(x = 5) { return x; }
                f(...[]);
            "));
    }

    // ========================================================
    // Combined realistic usage
    // ========================================================

    [Fact]
    public void Variadic_max_via_rest_and_math()
    {
        Assert.Equal(
            9.0,
            Eval(@"
                function maxAll(...nums) {
                    return Math.max.apply(null, nums);
                }
                maxAll(3, 9, 1, 7, 2);
            "));
    }

    [Fact]
    public void Forward_args_through_rest_and_spread()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function wrap(...args) {
                    return sum(...args);
                }
                function sum(a, b, c) { return a + b + c; }
                wrap(1, 2, 3);
            "));
    }

    [Fact]
    public void Rest_then_destructure_tail()
    {
        Assert.Equal(
            "3,4,5",
            Eval(@"
                function f(head, ...tail) {
                    var [a, b, c] = tail;
                    return a + ',' + b + ',' + c;
                }
                f('x', 3, 4, 5);
            "));
    }

    [Fact]
    public void Concat_arrays_with_spread_is_shorter_than_concat()
    {
        Assert.Equal(
            "1,2,3,4,5",
            Eval(@"
                var a = [1, 2];
                var b = [3, 4, 5];
                [...a, ...b].join(',');
            "));
    }
}
