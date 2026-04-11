using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsBuiltinTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- global functions --------

    [Fact]
    public void ParseInt_decimal()
    {
        Assert.Equal(42.0, Eval("parseInt('42');"));
        Assert.Equal(42.0, Eval("parseInt('  42  ');"));
        Assert.Equal(-7.0, Eval("parseInt('-7');"));
    }

    [Fact]
    public void ParseInt_hex()
    {
        Assert.Equal(255.0, Eval("parseInt('0xff');"));
        Assert.Equal(255.0, Eval("parseInt('0xFF', 16);"));
    }

    [Fact]
    public void ParseInt_radix()
    {
        Assert.Equal(10.0, Eval("parseInt('1010', 2);"));
        Assert.Equal(7.0, Eval("parseInt('7', 8);"));
        Assert.Equal(15.0, Eval("parseInt('f', 16);"));
    }

    [Fact]
    public void ParseInt_ignores_trailing_garbage()
    {
        Assert.Equal(42.0, Eval("parseInt('42abc');"));
    }

    [Fact]
    public void ParseInt_returns_NaN_for_unparseable()
    {
        Assert.True(double.IsNaN((double)Eval("parseInt('hello');")!));
    }

    [Fact]
    public void ParseFloat_basic()
    {
        Assert.Equal(3.14, Eval("parseFloat('3.14');"));
        Assert.Equal(1.5e10, Eval("parseFloat('1.5e10');"));
        Assert.Equal(-0.5, Eval("parseFloat('-.5');"));
    }

    [Fact]
    public void ParseFloat_ignores_trailing_non_numeric()
    {
        Assert.Equal(3.14, Eval("parseFloat('3.14xyz');"));
    }

    [Fact]
    public void ParseFloat_returns_NaN_for_empty()
    {
        Assert.True(double.IsNaN((double)Eval("parseFloat('');")!));
        Assert.True(double.IsNaN((double)Eval("parseFloat('abc');")!));
    }

    [Fact]
    public void IsNaN_and_isFinite()
    {
        Assert.Equal(true, Eval("isNaN(NaN);"));
        Assert.Equal(false, Eval("isNaN(1);"));
        Assert.Equal(true, Eval("isNaN('abc');"));

        Assert.Equal(true, Eval("isFinite(1);"));
        Assert.Equal(false, Eval("isFinite(Infinity);"));
        Assert.Equal(false, Eval("isFinite(NaN);"));
    }

    // -------- Array constructor --------

    [Fact]
    public void Array_constructor_with_length()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = new Array(5);");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(5, arr.Elements.Count);
        foreach (var e in arr.Elements) Assert.IsType<JsUndefined>(e);
    }

    [Fact]
    public void Array_constructor_with_elements()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = Array(1, 2, 3);");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(3, arr.Elements.Count);
        Assert.Equal(1.0, arr.Elements[0]);
    }

    [Fact]
    public void Array_isArray_distinguishes_arrays_from_objects()
    {
        Assert.Equal(true, Eval("Array.isArray([]);"));
        Assert.Equal(true, Eval("Array.isArray([1, 2, 3]);"));
        Assert.Equal(false, Eval("Array.isArray({});"));
        Assert.Equal(false, Eval("Array.isArray('string');"));
        Assert.Equal(false, Eval("Array.isArray(42);"));
    }

    // -------- Array.prototype methods --------

    [Fact]
    public void Array_push_appends_and_returns_length()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var a = [1, 2]; a.push(3, 4);");
        Assert.Equal(4.0, result);
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(4, arr.Elements.Count);
        Assert.Equal(4.0, arr.Elements[3]);
    }

    [Fact]
    public void Array_pop_removes_last_and_returns_it()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var a = [1, 2, 3]; a.pop();");
        Assert.Equal(3.0, result);
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(2, arr.Elements.Count);
    }

    [Fact]
    public void Array_pop_on_empty_returns_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("[].pop();"));
    }

    [Fact]
    public void Array_shift_removes_first()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var a = [1, 2, 3]; a.shift();");
        Assert.Equal(1.0, result);
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(2, arr.Elements.Count);
        Assert.Equal(2.0, arr.Elements[0]);
    }

    [Fact]
    public void Array_unshift_prepends_and_returns_new_length()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var a = [3]; a.unshift(1, 2);");
        Assert.Equal(3.0, result);
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(2.0, arr.Elements[1]);
        Assert.Equal(3.0, arr.Elements[2]);
    }

    [Fact]
    public void Array_slice_extracts_range()
    {
        var result = Assert.IsType<JsArray>(Eval("[1, 2, 3, 4, 5].slice(1, 4);"));
        Assert.Equal(3, result.Elements.Count);
        Assert.Equal(2.0, result.Elements[0]);
        Assert.Equal(4.0, result.Elements[2]);
    }

    [Fact]
    public void Array_slice_with_negative_indices()
    {
        var result = Assert.IsType<JsArray>(Eval("[1, 2, 3, 4, 5].slice(-2);"));
        Assert.Equal(2, result.Elements.Count);
        Assert.Equal(4.0, result.Elements[0]);
        Assert.Equal(5.0, result.Elements[1]);
    }

    [Fact]
    public void Array_concat_joins_multiple_arrays_and_values()
    {
        var result = Assert.IsType<JsArray>(Eval("[1, 2].concat([3, 4], 5);"));
        Assert.Equal(5, result.Elements.Count);
        Assert.Equal(5.0, result.Elements[4]);
    }

    [Fact]
    public void Array_join_uses_separator()
    {
        Assert.Equal("1-2-3", Eval("[1, 2, 3].join('-');"));
        Assert.Equal("1,2,3", Eval("[1, 2, 3].join();"));
    }

    [Fact]
    public void Array_indexOf_finds_element_with_strict_equality()
    {
        Assert.Equal(2.0, Eval("[1, 2, 3, 4].indexOf(3);"));
        Assert.Equal(-1.0, Eval("[1, 2, 3].indexOf('1');")); // strict equality — string vs number
    }

    [Fact]
    public void Array_indexOf_returns_minus_one_when_missing()
    {
        Assert.Equal(-1.0, Eval("[1, 2, 3].indexOf(99);"));
    }

    [Fact]
    public void Array_reverse_reverses_in_place()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = [1, 2, 3]; a.reverse();");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(3.0, arr.Elements[0]);
        Assert.Equal(1.0, arr.Elements[2]);
    }

    [Fact]
    public void Array_sort_uses_default_string_compare()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = [3, 1, 4, 1, 5, 9, 2, 6]; a.sort();");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        // Default sort is by string — so [1,1,2,3,4,5,6,9].
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(9.0, arr.Elements[7]);
    }

    [Fact]
    public void Array_sort_with_compareFn_now_works()
    {
        // Slice 6b adds compareFn support via the re-entrant VM.
        var arr = Assert.IsType<JsArray>(
            Eval("[3, 1, 2].sort(function (a, b) { return a - b; });"));
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(2.0, arr.Elements[1]);
        Assert.Equal(3.0, arr.Elements[2]);
    }

    // -------- String constructor --------

    [Fact]
    public void String_constructor_coerces()
    {
        Assert.Equal("42", Eval("String(42);"));
        Assert.Equal("true", Eval("String(true);"));
        Assert.Equal("null", Eval("String(null);"));
        Assert.Equal("undefined", Eval("String(undefined);"));
    }

    [Fact]
    public void String_fromCharCode_builds_from_codes()
    {
        Assert.Equal("abc", Eval("String.fromCharCode(97, 98, 99);"));
    }

    // -------- String primitive property access --------

    [Fact]
    public void String_length_works_on_primitive()
    {
        Assert.Equal(5.0, Eval("'hello'.length;"));
    }

    [Fact]
    public void String_indexed_access_returns_single_char_string()
    {
        Assert.Equal("b", Eval("'abc'[1];"));
    }

    [Fact]
    public void String_out_of_bounds_index_is_undefined()
    {
        // Through the prototype chain, undefined.
        Assert.IsType<JsUndefined>(Eval("'abc'[99];"));
    }

    // -------- String.prototype methods --------

    [Fact]
    public void String_charAt_and_charCodeAt()
    {
        Assert.Equal("b", Eval("'abc'.charAt(1);"));
        Assert.Equal("", Eval("'abc'.charAt(10);"));
        Assert.Equal(98.0, Eval("'abc'.charCodeAt(1);"));
    }

    [Fact]
    public void String_indexOf_and_lastIndexOf()
    {
        Assert.Equal(2.0, Eval("'hello'.indexOf('l');"));
        Assert.Equal(3.0, Eval("'hello'.lastIndexOf('l');"));
        Assert.Equal(-1.0, Eval("'hello'.indexOf('x');"));
    }

    [Fact]
    public void String_slice_with_positive_and_negative_indices()
    {
        Assert.Equal("bcd", Eval("'abcde'.slice(1, 4);"));
        Assert.Equal("de", Eval("'abcde'.slice(-2);"));
        Assert.Equal("", Eval("'abcde'.slice(3, 1);")); // end < start
    }

    [Fact]
    public void String_substring_swaps_args_when_end_before_start()
    {
        // Per spec, substring swaps if start > end.
        Assert.Equal("bcd", Eval("'abcde'.substring(4, 1);"));
    }

    [Fact]
    public void String_substr_with_length()
    {
        Assert.Equal("bcd", Eval("'abcde'.substr(1, 3);"));
        Assert.Equal("de", Eval("'abcde'.substr(-2);"));
    }

    [Fact]
    public void String_toLowerCase_and_toUpperCase()
    {
        Assert.Equal("hello", Eval("'HELLO'.toLowerCase();"));
        Assert.Equal("WORLD", Eval("'world'.toUpperCase();"));
    }

    [Fact]
    public void String_trim_strips_whitespace()
    {
        Assert.Equal("hello", Eval("'  hello  '.trim();"));
    }

    [Fact]
    public void String_split_by_separator()
    {
        var result = Assert.IsType<JsArray>(Eval("'a,b,c'.split(',');"));
        Assert.Equal(3, result.Elements.Count);
        Assert.Equal("a", result.Elements[0]);
        Assert.Equal("c", result.Elements[2]);
    }

    [Fact]
    public void String_split_with_empty_separator_gives_characters()
    {
        var result = Assert.IsType<JsArray>(Eval("'abc'.split('');"));
        Assert.Equal(3, result.Elements.Count);
        Assert.Equal("a", result.Elements[0]);
    }

    [Fact]
    public void String_split_without_separator_returns_whole_string()
    {
        var result = Assert.IsType<JsArray>(Eval("'abc'.split();"));
        Assert.Single(result.Elements);
        Assert.Equal("abc", result.Elements[0]);
    }

    [Fact]
    public void String_concat_joins()
    {
        Assert.Equal("abcdef", Eval("'abc'.concat('de', 'f');"));
    }

    // -------- For..in does not enumerate built-in methods --------

    [Fact]
    public void For_in_over_array_still_only_yields_indices()
    {
        // Regression check — when slice 6a added Array.prototype
        // methods, the naive for..in would iterate them. The
        // non-enumerable flag keeps them out.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var a = ['x', 'y', 'z'];
            var out = '';
            for (var i in a) { out = out + i; }
        ");
        Assert.Equal("012", eng.Globals["out"]);
    }

    [Fact]
    public void For_in_over_object_does_not_leak_prototype_methods()
    {
        // Plain object gets Object.prototype in its chain (once
        // slice 6b lands Object.prototype methods). For now the
        // prototype is empty, but this test guards against
        // accidental method leakage.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var o = {a: 1, b: 2};
            var keys = '';
            for (var k in o) { keys = keys + k; }
        ");
        Assert.Equal("ab", eng.Globals["keys"]);
    }

    // -------- Integration: chained calls --------

    [Fact]
    public void Chained_array_and_string_methods()
    {
        // Build a word count from a sentence.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var sentence = 'the quick brown fox';
            var words = sentence.split(' ');
            var count = words.length;
        ");
        Assert.Equal(4.0, eng.Globals["count"]);
    }

    [Fact]
    public void Reverse_a_string_via_split_reverse_join()
    {
        Assert.Equal("olleh", Eval("'hello'.split('').reverse().join('');"));
    }
}
