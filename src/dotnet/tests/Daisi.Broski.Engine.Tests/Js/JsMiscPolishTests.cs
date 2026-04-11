using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsMiscPolishTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Symbol.for / keyFor
    // ========================================================

    [Fact]
    public void SymbolFor_returns_shared_instance_for_same_key()
    {
        Assert.True((bool)Eval(@"
            var a = Symbol.for('tag');
            var b = Symbol.for('tag');
            a === b;
        ")!);
    }

    [Fact]
    public void SymbolFor_different_keys_are_distinct()
    {
        Assert.False((bool)Eval(@"
            var a = Symbol.for('one');
            var b = Symbol.for('two');
            a === b;
        ")!);
    }

    [Fact]
    public void SymbolKeyFor_registered_returns_key()
    {
        Assert.Equal(
            "hello",
            Eval(@"
                var s = Symbol.for('hello');
                Symbol.keyFor(s);
            "));
    }

    [Fact]
    public void SymbolKeyFor_unregistered_returns_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                var s = Symbol('not-registered');
                typeof Symbol.keyFor(s);
            "));
    }

    // ========================================================
    // TypedArray.from / of
    // ========================================================

    [Fact]
    public void TypedArrayFrom_iterable()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var a = Uint8Array.from([1, 2, 3]);
                a.join(',');
            "));
    }

    [Fact]
    public void TypedArrayFrom_with_map_fn()
    {
        Assert.Equal(
            "2,4,6",
            Eval(@"
                var a = Int32Array.from([1, 2, 3], function (v) { return v * 2; });
                a.join(',');
            "));
    }

    [Fact]
    public void TypedArrayFrom_set_as_source()
    {
        Assert.Equal(
            "10,20,30",
            Eval(@"
                var s = new Set([10, 20, 30]);
                var a = Uint16Array.from(s);
                a.join(',');
            "));
    }

    [Fact]
    public void TypedArrayOf_varargs()
    {
        Assert.Equal(
            "5,6,7",
            Eval(@"
                var a = Float32Array.of(5, 6, 7);
                a.join(',');
            "));
    }

    // ========================================================
    // Tagged templates
    // ========================================================

    [Fact]
    public void Tagged_template_receives_strings_and_values()
    {
        Assert.Equal(
            "a|1|b|2|c",
            Eval(@"
                function tag(strings) {
                    var out = [strings[0]];
                    for (var i = 1; i < arguments.length; i++) {
                        out.push('' + arguments[i]);
                        out.push(strings[i]);
                    }
                    return out.join('|');
                }
                tag`a${1}b${2}c`;
            "));
    }

    [Fact]
    public void Tagged_template_with_no_interpolations()
    {
        Assert.Equal(
            "[hello]",
            Eval(@"
                function tag(strings) { return '[' + strings[0] + ']'; }
                tag`hello`;
            "));
    }

    [Fact]
    public void Tagged_template_strings_array_length()
    {
        // n interpolations → n+1 strings.
        Assert.Equal(
            3.0,
            Eval(@"
                function tag(strings) { return strings.length; }
                tag`a${1}b${2}c`;
            "));
    }

    [Fact]
    public void Tagged_template_passes_expression_values()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum(strings, a, b) { return a + b; }
                sum`${2}${4}`;
            "));
    }

    [Fact]
    public void Tagged_template_with_method_tag()
    {
        Assert.Equal(
            "joined:a,b",
            Eval(@"
                var obj = {
                    tag: function (strings, x, y) {
                        return 'joined:' + x + ',' + y;
                    }
                };
                obj.tag`${'a'} and ${'b'}`;
            "));
    }
}
