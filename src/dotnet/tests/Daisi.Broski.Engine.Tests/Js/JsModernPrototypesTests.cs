using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// ES2015+ prototype / static additions — Array, Object,
/// String, Number. Every method here was a gap in
/// "undefined is not a function" failures on real sites
/// before this slice.
/// </summary>
public class JsModernPrototypesTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Array.prototype
    // ========================================================

    [Fact]
    public void Array_find_returns_first_match()
    {
        Assert.Equal(
            3.0,
            Eval("[1, 2, 3, 4].find(function (n) { return n > 2; });"));
    }

    [Fact]
    public void Array_find_returns_undefined_when_no_match()
    {
        Assert.Equal(
            "undefined",
            Eval("typeof [1, 2, 3].find(function (n) { return n > 100; });"));
    }

    [Fact]
    public void Array_findIndex_returns_index()
    {
        Assert.Equal(
            1.0,
            Eval("['a', 'b', 'c'].findIndex(function (x) { return x === 'b'; });"));
    }

    [Fact]
    public void Array_findIndex_returns_negative_one_when_no_match()
    {
        Assert.Equal(
            -1.0,
            Eval("[1, 2].findIndex(function (n) { return n > 100; });"));
    }

    [Fact]
    public void Array_findLast_returns_last_matching()
    {
        Assert.Equal(
            4.0,
            Eval("[1, 2, 3, 4].findLast(function (n) { return n < 5; });"));
    }

    [Fact]
    public void Array_findLastIndex()
    {
        Assert.Equal(
            2.0,
            Eval("[10, 20, 30, 40, 50].findLastIndex(function (n) { return n < 31; });"));
    }

    [Fact]
    public void Array_includes_returns_true_for_match()
    {
        Assert.Equal(true, Eval("[1, 2, 3].includes(2);"));
    }

    [Fact]
    public void Array_includes_returns_false_for_missing()
    {
        Assert.Equal(false, Eval("[1, 2, 3].includes(99);"));
    }

    [Fact]
    public void Array_includes_handles_NaN_same_value_zero()
    {
        Assert.Equal(true, Eval("[NaN, 1, 2].includes(NaN);"));
    }

    [Fact]
    public void Array_lastIndexOf_returns_last_match()
    {
        Assert.Equal(
            3.0,
            Eval("[1, 2, 3, 2, 1].lastIndexOf(2);"));
    }

    [Fact]
    public void Array_at_positive_index()
    {
        Assert.Equal(30.0, Eval("[10, 20, 30, 40].at(2);"));
    }

    [Fact]
    public void Array_at_negative_index()
    {
        Assert.Equal(40.0, Eval("[10, 20, 30, 40].at(-1);"));
    }

    [Fact]
    public void Array_fill_entire_array()
    {
        Assert.Equal(
            "7,7,7",
            Eval("[1, 2, 3].fill(7).join(',');"));
    }

    [Fact]
    public void Array_fill_range()
    {
        Assert.Equal(
            "1,0,0,4",
            Eval("[1, 2, 3, 4].fill(0, 1, 3).join(',');"));
    }

    [Fact]
    public void Array_flat_default_depth_one()
    {
        Assert.Equal(
            "1,2,3,4",
            Eval("[1, [2, 3], [4]].flat().join(',');"));
    }

    [Fact]
    public void Array_flat_custom_depth()
    {
        Assert.Equal(
            "1,2,3,4",
            Eval("[1, [2, [3, [4]]]].flat(3).join(',');"));
    }

    [Fact]
    public void Array_flatMap()
    {
        Assert.Equal(
            "1,2,2,4,3,6",
            Eval("[1, 2, 3].flatMap(function (n) { return [n, n * 2]; }).join(',');"));
    }

    [Fact]
    public void Array_keys_values_entries()
    {
        Assert.Equal(
            "0:a,1:b,2:c",
            Eval(@"
                var out = [];
                for (var e of ['a', 'b', 'c'].entries()) {
                    out.push(e[0] + ':' + e[1]);
                }
                out.join(',');
            "));
    }

    [Fact]
    public void Array_from_array()
    {
        Assert.Equal(
            "1,2,3",
            Eval("Array.from([1, 2, 3]).join(',');"));
    }

    [Fact]
    public void Array_from_with_map_fn()
    {
        Assert.Equal(
            "2,4,6",
            Eval("Array.from([1, 2, 3], function (n) { return n * 2; }).join(',');"));
    }

    [Fact]
    public void Array_from_string()
    {
        Assert.Equal(
            "h,e,l,l,o",
            Eval("Array.from('hello').join(',');"));
    }

    [Fact]
    public void Array_from_array_like_object()
    {
        Assert.Equal(
            "a,b,c",
            Eval("Array.from({ length: 3, 0: 'a', 1: 'b', 2: 'c' }).join(',');"));
    }

    [Fact]
    public void Array_of_variadic()
    {
        Assert.Equal(
            "1,2,3",
            Eval("Array.of(1, 2, 3).join(',');"));
    }

    [Fact]
    public void Array_of_single_number_is_not_array_length()
    {
        // Array(7) creates an array of length 7;
        // Array.of(7) creates [7].
        Assert.Equal(
            1.0,
            Eval("Array.of(7).length;"));
    }

    // ========================================================
    // Object static
    // ========================================================

    [Fact]
    public void Object_assign_merges_sources()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                var target = { a: 1 };
                Object.assign(target, { b: 2 }, { c: 3 });
                target.a + target.b;
            "));
    }

    [Fact]
    public void Object_assign_later_overrides_earlier()
    {
        Assert.Equal(
            "late",
            Eval(@"
                var t = Object.assign({}, { x: 'early' }, { x: 'late' });
                t.x;
            "));
    }

    [Fact]
    public void Object_entries()
    {
        Assert.Equal(
            "a=1,b=2",
            Eval(@"
                Object.entries({ a: 1, b: 2 })
                    .map(function (p) { return p[0] + '=' + p[1]; })
                    .join(',');
            "));
    }

    [Fact]
    public void Object_values()
    {
        Assert.Equal(
            "3",
            Eval(@"
                var sum = 0;
                var vs = Object.values({ a: 1, b: 2 });
                for (var v of vs) sum += v;
                String(sum);
            "));
    }

    [Fact]
    public void Object_fromEntries_rebuilds_object()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var o = Object.fromEntries([['a', 1], ['b', 2]]);
                o.a;
            "));
    }

    [Fact]
    public void Object_freeze_is_noop_stub_but_doesnt_crash()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var o = { x: 42 };
                Object.freeze(o);
                o.x;
            "));
    }

    [Fact]
    public void Object_is_treats_NaN_as_equal()
    {
        Assert.Equal(true, Eval("Object.is(NaN, NaN);"));
    }

    [Fact]
    public void Object_is_distinguishes_plus_zero_minus_zero()
    {
        Assert.Equal(false, Eval("Object.is(0, -0);"));
    }

    [Fact]
    public void Object_getOwnPropertyNames_returns_keys_array()
    {
        Assert.Equal(
            "a,b,c",
            Eval("Object.getOwnPropertyNames({ a: 1, b: 2, c: 3 }).join(',');"));
    }

    [Fact]
    public void Object_defineProperty_data_descriptor()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var o = {};
                Object.defineProperty(o, 'x', { value: 42 });
                o.x;
            "));
    }

    [Fact]
    public void Object_defineProperty_accessor_descriptor()
    {
        Assert.Equal(
            "hello!",
            Eval(@"
                var o = { inner: 'hello' };
                Object.defineProperty(o, 'bang', {
                    get: function () { return this.inner + '!'; }
                });
                o.bang;
            "));
    }

    // ========================================================
    // String.prototype
    // ========================================================

    [Fact]
    public void String_startsWith_true()
    {
        Assert.Equal(true, Eval("'hello world'.startsWith('hello');"));
    }

    [Fact]
    public void String_startsWith_with_position()
    {
        Assert.Equal(true, Eval("'hello world'.startsWith('world', 6);"));
    }

    [Fact]
    public void String_endsWith_true()
    {
        Assert.Equal(true, Eval("'hello world'.endsWith('world');"));
    }

    [Fact]
    public void String_endsWith_with_endpos()
    {
        Assert.Equal(true, Eval("'hello world'.endsWith('hello', 5);"));
    }

    [Fact]
    public void String_includes_true()
    {
        Assert.Equal(true, Eval("'hello world'.includes('lo w');"));
    }

    [Fact]
    public void String_padStart_default_pad()
    {
        Assert.Equal("     42", Eval("'42'.padStart(7);"));
    }

    [Fact]
    public void String_padStart_custom_pad()
    {
        Assert.Equal("00042", Eval("'42'.padStart(5, '0');"));
    }

    [Fact]
    public void String_padEnd_custom_pad()
    {
        Assert.Equal("abc...", Eval("'abc'.padEnd(6, '.');"));
    }

    [Fact]
    public void String_repeat()
    {
        Assert.Equal("abcabcabc", Eval("'abc'.repeat(3);"));
    }

    [Fact]
    public void String_repeat_zero()
    {
        Assert.Equal("", Eval("'abc'.repeat(0);"));
    }

    [Fact]
    public void String_repeat_negative_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { 'abc'.repeat(-1); false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void String_trimStart()
    {
        Assert.Equal("hello  ", Eval("'  hello  '.trimStart();"));
    }

    [Fact]
    public void String_trimEnd()
    {
        Assert.Equal("  hello", Eval("'  hello  '.trimEnd();"));
    }

    [Fact]
    public void String_at_positive()
    {
        Assert.Equal("e", Eval("'hello'.at(1);"));
    }

    [Fact]
    public void String_at_negative()
    {
        Assert.Equal("o", Eval("'hello'.at(-1);"));
    }

    [Fact]
    public void String_codePointAt()
    {
        Assert.Equal(
            97.0,
            Eval("'abc'.codePointAt(0);"));
    }

    // ========================================================
    // Number static
    // ========================================================

    [Fact]
    public void Number_isInteger_integers()
    {
        Assert.Equal(true, Eval("Number.isInteger(42);"));
    }

    [Fact]
    public void Number_isInteger_non_integer()
    {
        Assert.Equal(false, Eval("Number.isInteger(3.14);"));
    }

    [Fact]
    public void Number_isInteger_strict_type_check()
    {
        // Number.isInteger is strict — strings are never integers.
        Assert.Equal(false, Eval("Number.isInteger('42');"));
    }

    [Fact]
    public void Number_isFinite_rejects_infinity()
    {
        Assert.Equal(false, Eval("Number.isFinite(Infinity);"));
    }

    [Fact]
    public void Number_isFinite_rejects_NaN()
    {
        Assert.Equal(false, Eval("Number.isFinite(NaN);"));
    }

    [Fact]
    public void Number_isFinite_strict_type_check()
    {
        // Unlike global isFinite, Number.isFinite doesn't
        // coerce strings — '42' is not a finite Number.
        Assert.Equal(false, Eval("Number.isFinite('42');"));
    }

    [Fact]
    public void Number_isNaN_true_for_NaN_only()
    {
        Assert.Equal(
            "true,false,false",
            Eval("[Number.isNaN(NaN), Number.isNaN('NaN'), Number.isNaN(42)].join(',');"));
    }

    [Fact]
    public void Number_isSafeInteger()
    {
        Assert.Equal(
            "true,false",
            Eval("[Number.isSafeInteger(42), Number.isSafeInteger(9007199254740993)].join(',');"));
    }

    [Fact]
    public void Number_MAX_SAFE_INTEGER_constant()
    {
        Assert.Equal(
            9007199254740991.0,
            Eval("Number.MAX_SAFE_INTEGER;"));
    }

    [Fact]
    public void Number_EPSILON_is_positive_small()
    {
        Assert.Equal(
            true,
            Eval("Number.EPSILON > 0 && Number.EPSILON < 1e-10;"));
    }

    // ========================================================
    // Function global
    // ========================================================

    [Fact]
    public void Function_global_exists()
    {
        Assert.Equal("function", Eval("typeof Function;"));
    }

    [Fact]
    public void Function_constructor_compiles_body()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                var add = new Function('a', 'b', 'return a + b;');
                add(2, 3);
            "));
    }

    [Fact]
    public void Function_constructor_no_params()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var f = new Function('return 42;');
                f();
            "));
    }

    [Fact]
    public void Function_instanceof_for_user_functions()
    {
        Assert.Equal(
            true,
            Eval(@"
                function myFn() {}
                myFn instanceof Function;
            "));
    }

    [Fact]
    public void Function_syntax_error_surfaces_as_SyntaxError()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new Function('this is not valid'); false; }
                catch (e) { e instanceof SyntaxError; }
            "));
    }
}
