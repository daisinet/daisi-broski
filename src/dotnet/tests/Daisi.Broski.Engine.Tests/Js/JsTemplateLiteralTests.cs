using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsTemplateLiteralTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- no-substitution templates --------

    [Fact]
    public void Plain_template_with_no_interpolations()
    {
        Assert.Equal("hello", Eval("`hello`;"));
    }

    [Fact]
    public void Empty_template_is_empty_string()
    {
        Assert.Equal("", Eval("``;"));
    }

    [Fact]
    public void Template_with_standard_escapes()
    {
        Assert.Equal("a\nb\tc", Eval("`a\\nb\\tc`;"));
    }

    [Fact]
    public void Template_escapes_for_backtick_and_dollar_brace()
    {
        Assert.Equal("`", Eval("`\\``;"));
        Assert.Equal("${x}", Eval("`\\${x}`;"));
    }

    [Fact]
    public void Multi_line_template_preserves_newlines()
    {
        var src = "`line1\nline2`;";
        Assert.Equal("line1\nline2", Eval(src));
    }

    // -------- single interpolation --------

    [Fact]
    public void Single_interpolation_of_variable()
    {
        Assert.Equal(
            "hello alice",
            Eval("var name = 'alice'; `hello ${name}`;"));
    }

    [Fact]
    public void Single_interpolation_at_start()
    {
        Assert.Equal(
            "5 items",
            Eval("var n = 5; `${n} items`;"));
    }

    [Fact]
    public void Single_interpolation_at_end()
    {
        Assert.Equal(
            "count=5",
            Eval("var n = 5; `count=${n}`;"));
    }

    [Fact]
    public void Interpolation_coerces_number_to_string()
    {
        Assert.Equal("x=42", Eval("`x=${42}`;"));
    }

    [Fact]
    public void Interpolation_coerces_null_and_undefined()
    {
        Assert.Equal("v=null", Eval("`v=${null}`;"));
        Assert.Equal("v=undefined", Eval("`v=${undefined}`;"));
    }

    [Fact]
    public void Interpolation_coerces_boolean()
    {
        Assert.Equal("ok=true", Eval("`ok=${true}`;"));
    }

    // -------- arithmetic / expressions inside --------

    [Fact]
    public void Interpolation_runs_arithmetic_expression()
    {
        Assert.Equal("3", Eval("`${1 + 2}`;"));
    }

    [Fact]
    public void Interpolation_runs_function_call()
    {
        Assert.Equal(
            "squared: 9",
            Eval("function sq(x) { return x * x; } `squared: ${sq(3)}`;"));
    }

    [Fact]
    public void Interpolation_runs_method_call()
    {
        Assert.Equal("HELLO", Eval("`${'hello'.toUpperCase()}`;"));
    }

    // -------- multiple interpolations --------

    [Fact]
    public void Two_interpolations()
    {
        Assert.Equal(
            "alice is 30",
            Eval("var name = 'alice'; var age = 30; `${name} is ${age}`;"));
    }

    [Fact]
    public void Adjacent_interpolations_no_text_between()
    {
        Assert.Equal(
            "ab",
            Eval("var x = 'a'; var y = 'b'; `${x}${y}`;"));
    }

    [Fact]
    public void Three_interpolations()
    {
        Assert.Equal(
            "a,b,c",
            Eval("`${'a'},${'b'},${'c'}`;"));
    }

    // -------- nested structures --------

    [Fact]
    public void Template_nested_inside_interpolation()
    {
        Assert.Equal(
            "outer:inner:1-done",
            Eval("`outer:${`inner:${1}`}-done`;"));
    }

    [Fact]
    public void Object_literal_inside_interpolation()
    {
        // Object braces are distinct from template braces —
        // the lexer's brace-depth tracking handles both.
        Assert.Equal(
            "v=42",
            Eval("`v=${ {val: 42}.val }`;"));
    }

    [Fact]
    public void Array_and_join_inside_interpolation()
    {
        Assert.Equal(
            "list: a, b, c",
            Eval("`list: ${['a', 'b', 'c'].join(', ')}`;"));
    }

    // -------- with arrow functions --------

    [Fact]
    public void Arrow_function_body_returning_template_literal()
    {
        Assert.Equal(
            "hello world",
            Eval("var greet = name => `hello ${name}`; greet('world');"));
    }

    [Fact]
    public void Template_containing_arrow_call()
    {
        Assert.Equal(
            "10",
            Eval("var double = x => x * 2; `${double(5)}`;"));
    }

    // -------- with let/const --------

    [Fact]
    public void Template_reads_let_binding()
    {
        Assert.Equal(
            "block val = 7",
            Eval(@"
                {
                    let v = 7;
                    `block val = ${v}`;
                }
            "));
    }

    // -------- realistic usage --------

    [Fact]
    public void Build_greeting_with_map_and_join()
    {
        Assert.Equal(
            "Hello, Alice and Bob!",
            Eval(@"
                var names = ['Alice', 'Bob'];
                `Hello, ${names.join(' and ')}!`;
            "));
    }

    [Fact]
    public void JSON_stringify_and_interpolate()
    {
        Assert.Equal(
            "data={\"a\":1}",
            Eval("`data=${JSON.stringify({a: 1})}`;"));
    }
}
