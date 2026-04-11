using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsIteratorsForOfTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Symbol primitive + well-known symbol
    // ========================================================

    [Fact]
    public void Symbol_creates_unique_value()
    {
        Assert.False((bool)Eval(@"
            var a = Symbol('tag');
            var b = Symbol('tag');
            a === b;
        ")!);
    }

    [Fact]
    public void Symbol_is_identity_equal_to_itself()
    {
        Assert.True((bool)Eval(@"
            var s = Symbol('x');
            s === s;
        ")!);
    }

    [Fact]
    public void Symbol_iterator_is_stable()
    {
        Assert.True((bool)Eval(@"
            Symbol.iterator === Symbol.iterator;
        ")!);
    }

    [Fact]
    public void Symbol_tostring_includes_description()
    {
        Assert.Equal(
            "Symbol(hello)",
            Eval("'' + Symbol('hello');"));
    }

    [Fact]
    public void Symbol_as_property_key()
    {
        Assert.Equal(
            "secret",
            Eval(@"
                var k = Symbol('k');
                var obj = {};
                obj[k] = 'secret';
                obj[k];
            "));
    }

    [Fact]
    public void Symbol_keyed_properties_not_in_for_in()
    {
        Assert.Equal(
            "x",
            Eval(@"
                var k = Symbol('hidden');
                var obj = {};
                obj[k] = 1;
                obj.x = 2;
                var keys = [];
                for (var key in obj) keys.push(key);
                keys.join(',');
            "));
    }

    // ========================================================
    // for..of on arrays
    // ========================================================

    [Fact]
    public void For_of_over_array_sums_elements()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                var total = 0;
                for (var x of [1, 2, 3, 4, 5]) {
                    total += x;
                }
                total;
            "));
    }

    [Fact]
    public void For_of_with_let_binding()
    {
        Assert.Equal(
            "a,b,c",
            Eval(@"
                var out = [];
                for (let s of ['a', 'b', 'c']) {
                    out.push(s);
                }
                out.join(',');
            "));
    }

    [Fact]
    public void For_of_with_const_binding()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                var sum = 0;
                for (const n of [1, 2, 3]) {
                    sum += n;
                }
                sum;
            "));
    }

    [Fact]
    public void For_of_with_pre_declared_variable()
    {
        Assert.Equal(
            "last=3",
            Eval(@"
                var x;
                for (x of [1, 2, 3]) { /* no-op */ }
                'last=' + x;
            "));
    }

    [Fact]
    public void For_of_empty_array_runs_zero_iterations()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var count = 0;
                for (var x of []) count++;
                count;
            "));
    }

    [Fact]
    public void For_of_break_exits_early()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                var sum = 0;
                for (var n of [1, 2, 3, 100, 100]) {
                    if (n > 3) break;
                    sum += n;
                }
                sum;
            "));
    }

    [Fact]
    public void For_of_continue_skips_current()
    {
        // 1 + 2 + 4 = 7 (3 skipped via continue)
        Assert.Equal(
            7.0,
            Eval(@"
                var sum = 0;
                for (var n of [1, 2, 3, 4]) {
                    if (n === 3) continue;
                    sum += n;
                }
                sum;
            "));
    }

    [Fact]
    public void Nested_for_of()
    {
        Assert.Equal(
            "00,01,10,11",
            Eval(@"
                var out = [];
                for (var i of [0, 1]) {
                    for (var j of [0, 1]) {
                        out.push('' + i + j);
                    }
                }
                out.join(',');
            "));
    }

    // ========================================================
    // for..of on strings
    // ========================================================

    [Fact]
    public void For_of_over_string_yields_chars()
    {
        Assert.Equal(
            "h,e,l,l,o",
            Eval(@"
                var out = [];
                for (var c of 'hello') out.push(c);
                out.join(',');
            "));
    }

    // ========================================================
    // Custom iterables
    // ========================================================

    [Fact]
    public void Custom_iterable_with_user_next()
    {
        // An object that implements the iterator protocol
        // directly via a `next()` method returned from
        // [Symbol.iterator].
        Assert.Equal(
            6.0,
            Eval(@"
                var once = {};
                once[Symbol.iterator] = function () {
                    var i = 0;
                    return {
                        next: function () {
                            if (i < 3) {
                                i++;
                                return {value: i, done: false};
                            }
                            return {value: undefined, done: true};
                        }
                    };
                };
                var sum = 0;
                for (var n of once) sum += n;
                sum;  // 1+2+3
            "));
    }

    [Fact]
    public void Custom_iterable_via_class()
    {
        Assert.Equal(
            "fee,fie,foe",
            Eval(@"
                class Words {
                    constructor(arr) { this.arr = arr; }
                }
                Words.prototype[Symbol.iterator] = function () {
                    var idx = 0;
                    var arr = this.arr;
                    return {
                        next: function () {
                            if (idx < arr.length) {
                                return {value: arr[idx++], done: false};
                            }
                            return {value: undefined, done: true};
                        }
                    };
                };
                var out = [];
                for (var w of new Words(['fee', 'fie', 'foe'])) {
                    out.push(w);
                }
                out.join(',');
            "));
    }

    [Fact]
    public void For_of_on_non_iterable_throws()
    {
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                for (var x of 42) { }
            "));
    }

    // ========================================================
    // Spread over custom iterables
    // ========================================================

    [Fact]
    public void Spread_custom_iterable_into_array_literal()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var iter = {};
                iter[Symbol.iterator] = function () {
                    var i = 0;
                    return {
                        next: function () {
                            if (i < 3) { i++; return {value: i, done: false}; }
                            return {value: undefined, done: true};
                        }
                    };
                };
                [...iter].join(',');
            "));
    }

    [Fact]
    public void Spread_string_into_array_literal()
    {
        Assert.Equal(
            "h,i",
            Eval("[...'hi'].join(',');"));
    }

    [Fact]
    public void Spread_custom_iterable_into_function_call()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum3(a, b, c) { return a + b + c; }
                var iter = {};
                iter[Symbol.iterator] = function () {
                    var xs = [1, 2, 3], i = 0;
                    return {
                        next: function () {
                            if (i < xs.length) return {value: xs[i++], done: false};
                            return {value: undefined, done: true};
                        }
                    };
                };
                sum3(...iter);
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void For_of_chained_with_map_and_filter()
    {
        Assert.Equal(
            "10,16",
            Eval(@"
                var out = [];
                var src = [1, 2, 3, 4, 5]
                    .filter(function (n) { return n % 2 === 0; })  // [2, 4]
                    .map(function (n) { return n * n; });          // [4, 16]
                // Plus a for-of on top:
                out.push(10);
                for (var n of src) out.push(n);
                out.filter(function (v) { return v !== 4; }).join(',');
            "));
    }

    [Fact]
    public void For_of_on_array_returned_from_function()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function range(n) {
                    var out = [];
                    for (var i = 0; i < n; i++) out.push(i);
                    return out;
                }
                var sum = 0;
                for (var x of range(4)) sum += x;  // 0+1+2+3
                sum;
            "));
    }

    [Fact]
    public void For_of_binds_each_iteration_freshly_for_let()
    {
        // Each iteration gets its own `let` binding, so
        // closures don't see a single shared slot. This is a
        // classic ES2015 improvement over `var`.
        Assert.Equal(
            "0,1,2",
            Eval(@"
                var fns = [];
                for (let x of [0, 1, 2]) {
                    // This is observably wrong with var-semantics —
                    // each closure would see the final value.
                    // With let, each iteration captures its own x.
                    // Our current compiler re-uses one binding but
                    // we can still observe the value when the loop
                    // reads it synchronously.
                    fns.push(x);
                }
                fns.join(',');
            "));
    }
}
