using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsBuiltin6cTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- JSON.parse --------

    [Fact]
    public void JsonParse_primitives()
    {
        Assert.Equal(42.0, Eval("JSON.parse('42');"));
        Assert.Equal("hi", Eval("JSON.parse('\"hi\"');"));
        Assert.Equal(true, Eval("JSON.parse('true');"));
        Assert.Equal(false, Eval("JSON.parse('false');"));
        Assert.IsType<JsNull>(Eval("JSON.parse('null');"));
    }

    [Fact]
    public void JsonParse_array_of_numbers()
    {
        var arr = Assert.IsType<JsArray>(Eval("JSON.parse('[1, 2, 3]');"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(3.0, arr.Elements[2]);
    }

    [Fact]
    public void JsonParse_nested_object()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = JSON.parse('{\"a\": 1, \"b\": {\"c\": \"x\"}}');");
        var outer = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(1.0, outer.Properties["a"]);
        var inner = Assert.IsType<JsObject>(outer.Properties["b"]);
        Assert.Equal("x", inner.Properties["c"]);
    }

    [Fact]
    public void JsonParse_handles_string_escapes()
    {
        Assert.Equal("hello\nworld", Eval("JSON.parse('\"hello\\\\nworld\"');"));
        Assert.Equal("a\tb", Eval("JSON.parse('\"a\\\\tb\"');"));
        Assert.Equal("\"quoted\"", Eval("JSON.parse('\"\\\\\"quoted\\\\\"\"');"));
    }

    [Fact]
    public void JsonParse_unicode_escape()
    {
        Assert.Equal("\u00e9", Eval("JSON.parse('\"\\\\u00e9\"');"));
    }

    [Fact]
    public void JsonParse_scientific_notation()
    {
        Assert.Equal(1.5e10, Eval("JSON.parse('1.5e10');"));
        Assert.Equal(-0.5, Eval("JSON.parse('-0.5');"));
    }

    [Fact]
    public void JsonParse_invalid_throws_SyntaxError()
    {
        var ex = Assert.Throws<JsRuntimeException>(() => Eval("JSON.parse('{bad}');"));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("SyntaxError", err.Get("name"));
    }

    [Fact]
    public void JsonParse_empty_object_and_array()
    {
        var obj = Assert.IsType<JsObject>(Eval("JSON.parse('{}');"));
        Assert.Empty(obj.Properties);
        var arr = Assert.IsType<JsArray>(Eval("JSON.parse('[]');"));
        Assert.Empty(arr.Elements);
    }

    // -------- JSON.stringify --------

    [Fact]
    public void JsonStringify_primitives()
    {
        Assert.Equal("42", Eval("JSON.stringify(42);"));
        Assert.Equal("\"hello\"", Eval("JSON.stringify('hello');"));
        Assert.Equal("true", Eval("JSON.stringify(true);"));
        Assert.Equal("null", Eval("JSON.stringify(null);"));
    }

    [Fact]
    public void JsonStringify_undefined_at_top_level_is_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("JSON.stringify(undefined);"));
    }

    [Fact]
    public void JsonStringify_simple_object()
    {
        Assert.Equal(
            "{\"a\":1,\"b\":2}",
            Eval("JSON.stringify({a: 1, b: 2});"));
    }

    [Fact]
    public void JsonStringify_nested()
    {
        Assert.Equal(
            "{\"x\":[1,2,{\"y\":3}]}",
            Eval("JSON.stringify({x: [1, 2, {y: 3}]});"));
    }

    [Fact]
    public void JsonStringify_escapes_strings()
    {
        Assert.Equal(
            "\"hello\\nworld\"",
            Eval("JSON.stringify('hello\\nworld');"));
        Assert.Equal(
            "\"\\\"quoted\\\"\"",
            Eval("JSON.stringify('\"quoted\"');"));
    }

    [Fact]
    public void JsonStringify_NaN_and_Infinity_as_null()
    {
        Assert.Equal("null", Eval("JSON.stringify(NaN);"));
        Assert.Equal("null", Eval("JSON.stringify(Infinity);"));
    }

    [Fact]
    public void JsonStringify_omits_functions_from_object()
    {
        Assert.Equal(
            "{\"a\":1}",
            Eval("JSON.stringify({a: 1, b: function () {}});"));
    }

    [Fact]
    public void JsonStringify_function_in_array_becomes_null()
    {
        Assert.Equal(
            "[1,null,3]",
            Eval("JSON.stringify([1, function () {}, 3]);"));
    }

    [Fact]
    public void JsonStringify_throws_on_cycle()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval("var o = {}; o.self = o; JSON.stringify(o);"));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("TypeError", err.Get("name"));
    }

    [Fact]
    public void JsonStringify_roundtrip()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate(@"
            var data = {name: 'alice', age: 30, hobbies: ['reading', 'hiking']};
            var json = JSON.stringify(data);
            var parsed = JSON.parse(json);
            parsed.name + ':' + parsed.age + ':' + parsed.hobbies.length;
        ");
        Assert.Equal("alice:30:2", result);
    }

    // -------- Function.prototype.call --------

    [Fact]
    public void Call_invokes_with_bound_this()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                function getVal() { return this.val; }
                var obj = {val: 42};
                getVal.call(obj);
            "));
    }

    [Fact]
    public void Call_passes_arguments()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function add(a, b) { return a + b; }
                add.call(null, 2, 4);
            "));
    }

    [Fact]
    public void Call_on_non_function_throws_TypeError()
    {
        Assert.Throws<JsRuntimeException>(() =>
            Eval("Function.prototype.call.call({});"));
    }

    // -------- Function.prototype.apply --------

    [Fact]
    public void Apply_passes_array_as_arguments()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum() {
                    var total = 0;
                    for (var i = 0; i < arguments.length; i++) {
                        total = total + arguments[i];
                    }
                    return total;
                }
                sum.apply(null, [1, 2, 3]);
            "));
    }

    [Fact]
    public void Apply_with_null_array_is_zero_args()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                function count() { return arguments.length; }
                count.apply(null, null);
            "));
    }

    // -------- Function.prototype.bind --------

    [Fact]
    public void Bind_returns_new_function_with_fixed_this()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                function get() { return this.v; }
                var obj = {v: 10};
                var bound = get.bind(obj);
                bound();
            "));
    }

    [Fact]
    public void Bind_with_preset_args_are_prepended()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                function add(a, b, c) { return a + b + c; }
                var add5 = add.bind(null, 5);
                add5(4, 6);
            "));
    }

    [Fact]
    public void Bind_is_itself_a_function()
    {
        Assert.Equal(
            "function",
            Eval(@"
                function f() {}
                typeof f.bind({});
            "));
    }

    // -------- Number.prototype --------

    [Fact]
    public void Number_toString_default_radix()
    {
        Assert.Equal("42", Eval("(42).toString();"));
        Assert.Equal("3.14", Eval("(3.14).toString();"));
    }

    [Fact]
    public void Number_toString_binary_and_hex()
    {
        Assert.Equal("1010", Eval("(10).toString(2);"));
        Assert.Equal("ff", Eval("(255).toString(16);"));
        Assert.Equal("-101", Eval("(-5).toString(2);"));
    }

    [Fact]
    public void Number_toString_radix_out_of_range_throws()
    {
        Assert.Throws<JsRuntimeException>(() => Eval("(10).toString(1);"));
        Assert.Throws<JsRuntimeException>(() => Eval("(10).toString(37);"));
    }

    [Fact]
    public void Number_toFixed_basic()
    {
        Assert.Equal("3.14", Eval("(3.14159).toFixed(2);"));
        Assert.Equal("3", Eval("(3).toFixed(0);"));
        Assert.Equal("3.00", Eval("(3).toFixed(2);"));
    }

    [Fact]
    public void Number_toFixed_NaN_and_Infinity()
    {
        Assert.Equal("NaN", Eval("NaN.toFixed(2);"));
        Assert.Equal("Infinity", Eval("Infinity.toFixed(2);"));
    }

    [Fact]
    public void Number_valueOf_returns_primitive()
    {
        Assert.Equal(42.0, Eval("(42).valueOf();"));
    }

    [Fact]
    public void Number_constructor_coerces()
    {
        Assert.Equal(42.0, Eval("Number('42');"));
        Assert.Equal(0.0, Eval("Number();"));
        Assert.Equal(1.0, Eval("Number(true);"));
    }

    [Fact]
    public void Number_statics()
    {
        Assert.Equal(double.MaxValue, Eval("Number.MAX_VALUE;"));
        Assert.Equal(double.PositiveInfinity, Eval("Number.POSITIVE_INFINITY;"));
        Assert.Equal(true, Eval("Number.isNaN(NaN);"));
        Assert.Equal(false, Eval("Number.isNaN('not a number');")); // strict — string isn't NaN
        Assert.Equal(true, Eval("Number.isInteger(5);"));
        Assert.Equal(false, Eval("Number.isInteger(5.5);"));
    }

    // -------- Boolean.prototype --------

    [Fact]
    public void Boolean_toString()
    {
        Assert.Equal("true", Eval("(true).toString();"));
        Assert.Equal("false", Eval("(false).toString();"));
    }

    [Fact]
    public void Boolean_valueOf()
    {
        Assert.Equal(true, Eval("(true).valueOf();"));
    }

    [Fact]
    public void Boolean_constructor_coerces()
    {
        Assert.Equal(true, Eval("Boolean(1);"));
        Assert.Equal(false, Eval("Boolean(0);"));
        Assert.Equal(false, Eval("Boolean('');"));
        Assert.Equal(true, Eval("Boolean('hi');"));
    }

    // -------- Integration: JSON + bind + method composition --------

    [Fact]
    public void Build_object_via_map_and_stringify()
    {
        Assert.Equal(
            "[{\"n\":1},{\"n\":2},{\"n\":3}]",
            Eval(@"
                var result = [1, 2, 3].map(function (x) { return {n: x}; });
                JSON.stringify(result);
            "));
    }

    [Fact]
    public void Bind_composes_with_forEach()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                var acc = {total: 0};
                function addTo(x) { this.total = this.total + x; }
                [1, 2, 3, 4, 5].forEach(addTo.bind(acc));
                acc.total;
            "));
    }
}
