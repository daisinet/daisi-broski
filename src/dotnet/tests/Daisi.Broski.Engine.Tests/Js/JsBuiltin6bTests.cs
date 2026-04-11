using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsBuiltin6bTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- Array.prototype.forEach --------

    [Fact]
    public void ForEach_iterates_over_elements()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                var sum = 0;
                [1, 2, 3, 4, 5].forEach(function (x) { sum = sum + x; });
                sum;
            "));
    }

    [Fact]
    public void ForEach_passes_index_and_array()
    {
        Assert.Equal(
            "0:1,1:2,2:3,",
            Eval(@"
                var out = '';
                [1, 2, 3].forEach(function (v, i, a) {
                    out = out + i + ':' + v + ',';
                });
                out;
            "));
    }

    // -------- map / filter --------

    [Fact]
    public void Map_transforms_each_element()
    {
        var arr = Assert.IsType<JsArray>(Eval("[1, 2, 3].map(function (x) { return x * 2; });"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.Equal(2.0, arr.Elements[0]);
        Assert.Equal(6.0, arr.Elements[2]);
    }

    [Fact]
    public void Filter_keeps_matching_elements()
    {
        var arr = Assert.IsType<JsArray>(
            Eval("[1, 2, 3, 4, 5].filter(function (x) { return x % 2 === 0; });"));
        Assert.Equal(2, arr.Elements.Count);
        Assert.Equal(2.0, arr.Elements[0]);
        Assert.Equal(4.0, arr.Elements[1]);
    }

    [Fact]
    public void Map_result_is_itself_an_array_with_prototype_methods()
    {
        // The array returned by map should inherit from
        // Array.prototype, so follow-up method calls work.
        Assert.Equal(
            "2,4,6",
            Eval("[1, 2, 3].map(function (x) { return x * 2; }).join(',');"));
    }

    // -------- reduce / reduceRight --------

    [Fact]
    public void Reduce_with_initial_value()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                [1, 2, 3, 4, 5].reduce(function (acc, x) { return acc + x; }, 0);
            "));
    }

    [Fact]
    public void Reduce_without_initial_uses_first_element_as_seed()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                [1, 2, 3, 4, 5].reduce(function (acc, x) { return acc + x; });
            "));
    }

    [Fact]
    public void Reduce_of_empty_with_no_initial_throws_TypeError()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval("[].reduce(function (a, b) { return a + b; });"));
        var obj = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("TypeError", obj.Get("name"));
    }

    [Fact]
    public void ReduceRight_folds_from_the_right()
    {
        Assert.Equal(
            "cba",
            Eval(@"
                ['a', 'b', 'c'].reduceRight(function (acc, x) { return acc + x; });
            "));
    }

    // -------- every / some --------

    [Fact]
    public void Every_returns_true_when_all_pass()
    {
        Assert.Equal(true, Eval("[1, 2, 3].every(function (x) { return x > 0; });"));
        Assert.Equal(false, Eval("[1, 2, 3].every(function (x) { return x > 1; });"));
    }

    [Fact]
    public void Some_returns_true_when_any_passes()
    {
        Assert.Equal(true, Eval("[1, 2, 3].some(function (x) { return x > 2; });"));
        Assert.Equal(false, Eval("[1, 2, 3].some(function (x) { return x > 5; });"));
    }

    // -------- sort with compareFn --------

    [Fact]
    public void Sort_numeric_with_compareFn()
    {
        var arr = Assert.IsType<JsArray>(
            Eval("[10, 1, 5, 2].sort(function (a, b) { return a - b; });"));
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(2.0, arr.Elements[1]);
        Assert.Equal(5.0, arr.Elements[2]);
        Assert.Equal(10.0, arr.Elements[3]);
    }

    [Fact]
    public void Sort_descending_with_compareFn()
    {
        var arr = Assert.IsType<JsArray>(
            Eval("[1, 10, 2, 5].sort(function (a, b) { return b - a; });"));
        Assert.Equal(10.0, arr.Elements[0]);
        Assert.Equal(5.0, arr.Elements[1]);
    }

    // -------- callback exception propagation --------

    [Fact]
    public void Callback_throw_propagates_to_outer_try_catch()
    {
        Assert.Equal(
            "caught boom",
            Eval(@"
                var result;
                try {
                    [1, 2, 3].forEach(function (x) {
                        if (x === 2) { throw 'boom'; }
                    });
                    result = 'unreached';
                } catch (e) {
                    result = 'caught ' + e;
                }
                result;
            "));
    }

    [Fact]
    public void Nested_callback_throw_caught_by_middle_try()
    {
        // Callback in forEach can throw, and an inner try/catch
        // in the callback should catch before it escapes.
        Assert.Equal(
            "inner-ok",
            Eval(@"
                var result = '';
                [1, 2, 3].forEach(function (x) {
                    try {
                        if (x === 2) { throw 'e'; }
                    } catch (err) {
                        result = 'inner-ok';
                    }
                });
                result;
            "));
    }

    // -------- Object built-in --------

    [Fact]
    public void Object_constructor_creates_empty_object()
    {
        var obj = Assert.IsType<JsObject>(Eval("new Object();"));
        Assert.Empty(obj.Properties);
    }

    [Fact]
    public void Object_keys_returns_own_enumerable_keys_in_insertion_order()
    {
        var arr = Assert.IsType<JsArray>(
            Eval("Object.keys({a: 1, b: 2, c: 3});"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.Equal("a", arr.Elements[0]);
        Assert.Equal("c", arr.Elements[2]);
    }

    [Fact]
    public void Object_keys_ignores_inherited_properties()
    {
        // Slice 6b can't easily test this without functions or
        // create, so use Object.create.
        var arr = Assert.IsType<JsArray>(
            Eval("Object.keys(Object.create({a: 1}));"));
        Assert.Empty(arr.Elements);
    }

    [Fact]
    public void Object_create_sets_prototype_chain()
    {
        Assert.Equal(
            "parent",
            Eval(@"
                var parent = {tag: 'parent'};
                var child = Object.create(parent);
                child.tag;
            "));
    }

    [Fact]
    public void Object_create_with_null_prototype()
    {
        // Property access falls back to undefined because there's
        // no prototype chain.
        Assert.IsType<JsUndefined>(
            Eval("var o = Object.create(null); o.anything;"));
    }

    [Fact]
    public void Object_getPrototypeOf_returns_prototype_or_null()
    {
        // Slice 6a already puts ObjectPrototype at the root of an
        // object literal's chain; getPrototypeOf should reach it.
        var proto = Eval("Object.getPrototypeOf({});");
        Assert.IsType<JsObject>(proto);
    }

    [Fact]
    public void HasOwnProperty_distinguishes_own_from_inherited()
    {
        Assert.Equal(true, Eval("({a: 1}).hasOwnProperty('a');"));
        Assert.Equal(false, Eval("({a: 1}).hasOwnProperty('toString');"));
    }

    [Fact]
    public void IsPrototypeOf_walks_the_chain()
    {
        Assert.Equal(
            true,
            Eval(@"
                var parent = {tag: 'p'};
                var child = Object.create(parent);
                parent.isPrototypeOf(child);
            "));
    }

    [Fact]
    public void PropertyIsEnumerable_respects_non_enumerable_flag()
    {
        // User properties are enumerable by default.
        Assert.Equal(true, Eval("({a: 1}).propertyIsEnumerable('a');"));
        // Prototype methods installed non-enumerable are NOT.
        Assert.Equal(
            false,
            Eval("({}).propertyIsEnumerable('toString');"));
    }

    // -------- Math built-in --------

    [Fact]
    public void Math_has_constants()
    {
        Assert.Equal(Math.PI, Eval("Math.PI;"));
        Assert.Equal(Math.E, Eval("Math.E;"));
        Assert.Equal(Math.Sqrt(2), Eval("Math.SQRT2;"));
    }

    [Fact]
    public void Math_abs_ceil_floor_round()
    {
        Assert.Equal(5.0, Eval("Math.abs(-5);"));
        Assert.Equal(5.0, Eval("Math.ceil(4.3);"));
        Assert.Equal(4.0, Eval("Math.floor(4.9);"));
        Assert.Equal(1.0, Eval("Math.round(0.5);"));
        Assert.Equal(0.0, Eval("Math.round(-0.5);"));
    }

    [Fact]
    public void Math_sqrt_pow_exp_log()
    {
        Assert.Equal(3.0, Eval("Math.sqrt(9);"));
        Assert.Equal(8.0, Eval("Math.pow(2, 3);"));
        Assert.Equal(Math.E, Eval("Math.exp(1);"));
        Assert.Equal(0.0, Eval("Math.log(1);"));
    }

    [Fact]
    public void Math_min_max_variadic()
    {
        Assert.Equal(1.0, Eval("Math.min(3, 1, 5, 2);"));
        Assert.Equal(5.0, Eval("Math.max(3, 1, 5, 2);"));
    }

    [Fact]
    public void Math_min_no_args_is_positive_Infinity()
    {
        Assert.Equal(double.PositiveInfinity, Eval("Math.min();"));
        Assert.Equal(double.NegativeInfinity, Eval("Math.max();"));
    }

    [Fact]
    public void Math_min_max_NaN_propagates()
    {
        Assert.True(double.IsNaN((double)Eval("Math.min(1, NaN, 2);")!));
        Assert.True(double.IsNaN((double)Eval("Math.max(1, NaN, 2);")!));
    }

    [Fact]
    public void Math_random_returns_value_in_range()
    {
        var v = (double)Eval("Math.random();")!;
        Assert.InRange(v, 0.0, 1.0);
    }

    [Fact]
    public void Math_sign()
    {
        Assert.Equal(1.0, Eval("Math.sign(5);"));
        Assert.Equal(-1.0, Eval("Math.sign(-3);"));
        Assert.Equal(0.0, Eval("Math.sign(0);"));
    }

    // -------- Error constructors --------

    [Fact]
    public void Error_constructor_stores_message()
    {
        var err = Assert.IsType<JsObject>(Eval("new Error('something broke');"));
        Assert.Equal("something broke", err.Get("message"));
        Assert.Equal("Error", err.Get("name"));
    }

    [Fact]
    public void TypeError_is_instanceof_Error()
    {
        Assert.Equal(
            true,
            Eval(@"
                var e = new TypeError('bad');
                e instanceof TypeError;
            "));
        Assert.Equal(
            true,
            Eval(@"
                var e = new TypeError('bad');
                e instanceof Error;
            "));
    }

    [Fact]
    public void VM_ReferenceError_is_instanceof_ReferenceError()
    {
        // Internal errors raised by the VM (undeclared read) now
        // inherit from the right Error subclass prototype so
        // script-level instanceof checks work.
        Assert.Equal(
            true,
            Eval(@"
                var r;
                try { undeclared; } catch (e) { r = e instanceof ReferenceError; }
                r;
            "));
    }

    [Fact]
    public void VM_TypeError_on_call_of_non_function_is_instanceof_TypeError()
    {
        Assert.Equal(
            true,
            Eval(@"
                var r;
                try { var x = 1; x(); } catch (e) { r = e instanceof TypeError; }
                r;
            "));
    }

    [Fact]
    public void Error_toString_formats_name_colon_message()
    {
        Assert.Equal(
            "TypeError: bad thing",
            Eval("(new TypeError('bad thing')).toString();"));
    }

    [Fact]
    public void Error_toString_omits_empty_message()
    {
        Assert.Equal(
            "Error",
            Eval("(new Error()).toString();"));
    }

    // -------- chained fluent usage --------

    [Fact]
    public void Fluent_pipeline_map_filter_reduce()
    {
        // Classic: sum of squares of even numbers.
        Assert.Equal(
            220.0,
            Eval(@"
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
                    .filter(function (x) { return x % 2 === 0; })
                    .map(function (x) { return x * x; })
                    .reduce(function (a, b) { return a + b; }, 0);
            "));
    }

    [Fact]
    public void Map_with_closure_captures_outer_variable()
    {
        Assert.Equal(
            "3,6,9",
            Eval(@"
                var mul = 3;
                [1, 2, 3].map(function (x) { return x * mul; }).join(',');
            "));
    }

    [Fact]
    public void Callback_can_be_a_nested_function_declaration()
    {
        Assert.Equal(
            "1,4,9",
            Eval(@"
                function square(x) { return x * x; }
                [1, 2, 3].map(square).join(',');
            "));
    }
}
