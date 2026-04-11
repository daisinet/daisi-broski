using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsCollectionsTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Map
    // ========================================================

    [Fact]
    public void Map_basic_set_and_get()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var m = new Map();
                m.set('k', 42);
                m.get('k');
            "));
    }

    [Fact]
    public void Map_has_and_delete()
    {
        // has=true before delete, delete returns true on
        // success, has=false after.
        Assert.Equal(
            "true,true,false",
            Eval(@"
                var m = new Map();
                m.set('k', 1);
                var a = m.has('k');
                var b = m.delete('k');
                var c = m.has('k');
                a + ',' + b + ',' + c;
            "));
    }

    [Fact]
    public void Map_size_reflects_entry_count()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                var m = new Map();
                m.set('a', 1);
                m.set('b', 2);
                m.set('c', 3);
                m.size;
            "));
    }

    [Fact]
    public void Map_overwrite_keeps_size()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var m = new Map();
                m.set('k', 1);
                m.set('k', 2);
                m.size;
            "));
    }

    [Fact]
    public void Map_clear_empties_the_map()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var m = new Map();
                m.set('a', 1); m.set('b', 2);
                m.clear();
                m.size;
            "));
    }

    [Fact]
    public void Map_set_returns_map_for_chaining()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var m = new Map();
                m.set('a', 1).set('b', 2).set('c', 3);
                [...m.values()].join(',');
            "));
    }

    [Fact]
    public void Map_NaN_key_works_via_SameValueZero()
    {
        // Strict-equal NaN !== NaN, but Map uses SameValueZero
        // so NaN can be used as a retrieval key.
        Assert.Equal(
            "present",
            Eval(@"
                var m = new Map();
                m.set(NaN, 'present');
                m.get(NaN);
            "));
    }

    [Fact]
    public void Map_object_key_by_reference()
    {
        Assert.Equal(
            "hit:miss",
            Eval(@"
                var k1 = {};
                var k2 = {};
                var m = new Map();
                m.set(k1, 'hit');
                var a = m.get(k1) || 'miss';
                var b = m.get(k2) || 'miss';
                a + ':' + b;
            "));
    }

    [Fact]
    public void Map_plus_zero_and_minus_zero_collide()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var m = new Map();
                m.set(0, 'a');
                m.set(-0, 'b');
                m.size;  // 1: +0 and -0 collapse
            "));
    }

    [Fact]
    public void Map_from_iterable_of_pairs()
    {
        Assert.Equal(
            "alice:30,bob:25",
            Eval(@"
                var m = new Map([['alice', 30], ['bob', 25]]);
                var out = [];
                m.forEach(function (v, k) { out.push(k + ':' + v); });
                out.join(',');
            "));
    }

    [Fact]
    public void Map_iteration_order_is_insertion_order()
    {
        Assert.Equal(
            "c,a,b",
            Eval(@"
                var m = new Map();
                m.set('c', 3);
                m.set('a', 1);
                m.set('b', 2);
                [...m.keys()].join(',');
            "));
    }

    [Fact]
    public void Map_for_of_yields_entries()
    {
        Assert.Equal(
            "a=1;b=2",
            Eval(@"
                var m = new Map();
                m.set('a', 1);
                m.set('b', 2);
                var out = [];
                for (var e of m) {
                    out.push(e[0] + '=' + e[1]);
                }
                out.join(';');
            "));
    }

    [Fact]
    public void Map_foreach_callback_args()
    {
        // ES spec: callback is called with (value, key, map).
        Assert.Equal(
            "1->a,2->b",
            Eval(@"
                var m = new Map();
                m.set('a', 1);
                m.set('b', 2);
                var out = [];
                m.forEach(function (v, k) { out.push(v + '->' + k); });
                out.join(',');
            "));
    }

    [Fact]
    public void Map_values_iterator()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                var m = new Map([['a', 1], ['b', 2], ['c', 3]]);
                var total = 0;
                for (var v of m.values()) total += v;
                total;
            "));
    }

    [Fact]
    public void Map_entries_method()
    {
        Assert.Equal(
            "a:1,b:2",
            Eval(@"
                var m = new Map([['a', 1], ['b', 2]]);
                var out = [];
                for (var e of m.entries()) out.push(e[0] + ':' + e[1]);
                out.join(',');
            "));
    }

    // ========================================================
    // Set
    // ========================================================

    [Fact]
    public void Set_add_and_has()
    {
        Assert.Equal(
            "true,false",
            Eval(@"
                var s = new Set();
                s.add('x');
                s.has('x') + ',' + s.has('y');
            "));
    }

    [Fact]
    public void Set_duplicates_are_ignored()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var s = new Set();
                s.add('x');
                s.add('x');
                s.add('x');
                s.size;
            "));
    }

    [Fact]
    public void Set_add_returns_set_for_chaining()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var s = new Set();
                s.add(1).add(2).add(3);
                [...s.values()].join(',');
            "));
    }

    [Fact]
    public void Set_NaN_deduplicates()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var s = new Set();
                s.add(NaN);
                s.add(NaN);
                s.size;
            "));
    }

    [Fact]
    public void Set_delete_and_clear()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var s = new Set([1, 2, 3]);
                s.delete(2);
                s.clear();
                s.size;
            "));
    }

    [Fact]
    public void Set_from_iterable()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                var s = new Set([1, 2, 2, 3, 3, 3]);
                s.size;
            "));
    }

    [Fact]
    public void Set_for_of_yields_values()
    {
        Assert.Equal(
            "a,b,c",
            Eval(@"
                var s = new Set(['a', 'b', 'c']);
                var out = [];
                for (var v of s) out.push(v);
                out.join(',');
            "));
    }

    [Fact]
    public void Set_spread_to_array_deduplicates_source()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                [...new Set([1, 1, 2, 2, 3, 3])].join(',');
            "));
    }

    [Fact]
    public void Set_foreach_receives_value_twice()
    {
        // Spec quirk: Set.forEach passes (value, value, set).
        Assert.Equal(
            "1=1,2=2,3=3",
            Eval(@"
                var s = new Set([1, 2, 3]);
                var out = [];
                s.forEach(function (v, k) { out.push(v + '=' + k); });
                out.join(',');
            "));
    }

    // ========================================================
    // WeakMap
    // ========================================================

    [Fact]
    public void WeakMap_set_and_get_with_object_key()
    {
        Assert.Equal(
            "ok",
            Eval(@"
                var k = {};
                var m = new WeakMap();
                m.set(k, 'ok');
                m.get(k);
            "));
    }

    [Fact]
    public void WeakMap_has_and_delete()
    {
        Assert.Equal(
            "true,true,false",
            Eval(@"
                var k = {};
                var m = new WeakMap();
                m.set(k, 1);
                var h1 = m.has(k);
                var d = m.delete(k);
                var h2 = m.has(k);
                h1 + ',' + d + ',' + h2;
            "));
    }

    [Fact]
    public void WeakMap_rejects_primitive_key()
    {
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                var m = new WeakMap();
                m.set('not-an-object', 1);
            "));
    }

    [Fact]
    public void WeakMap_different_objects_stored_separately()
    {
        Assert.Equal(
            "a:b",
            Eval(@"
                var k1 = {}, k2 = {};
                var m = new WeakMap();
                m.set(k1, 'a');
                m.set(k2, 'b');
                m.get(k1) + ':' + m.get(k2);
            "));
    }

    [Fact]
    public void WeakMap_from_iterable_of_pairs()
    {
        Assert.Equal(
            "v1",
            Eval(@"
                var k = {};
                var m = new WeakMap([[k, 'v1']]);
                m.get(k);
            "));
    }

    // ========================================================
    // WeakSet
    // ========================================================

    [Fact]
    public void WeakSet_add_and_has()
    {
        Assert.Equal(
            "true,false",
            Eval(@"
                var a = {}, b = {};
                var s = new WeakSet();
                s.add(a);
                s.has(a) + ',' + s.has(b);
            "));
    }

    [Fact]
    public void WeakSet_rejects_primitive_value()
    {
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                var s = new WeakSet();
                s.add(42);
            "));
    }

    [Fact]
    public void WeakSet_delete()
    {
        Assert.Equal(
            "true,false",
            Eval(@"
                var a = {};
                var s = new WeakSet();
                s.add(a);
                var b = s.delete(a);
                var c = s.has(a);
                b + ',' + c;
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Count_word_frequencies_via_map()
    {
        Assert.Equal(
            "the:2,quick:1,brown:1,fox:1",
            Eval(@"
                var words = 'the quick brown fox the'.split(' ');
                var counts = new Map();
                for (var w of words) {
                    counts.set(w, (counts.get(w) || 0) + 1);
                }
                var out = [];
                counts.forEach(function (n, w) { out.push(w + ':' + n); });
                out.join(',');
            "));
    }

    [Fact]
    public void Set_intersection_via_filter()
    {
        Assert.Equal(
            "2,3",
            Eval(@"
                var a = new Set([1, 2, 3]);
                var b = new Set([2, 3, 4]);
                var both = [...a].filter(function (v) { return b.has(v); });
                both.join(',');
            "));
    }

    [Fact]
    public void Map_as_object_metadata_via_object_keys()
    {
        Assert.Equal(
            "user1:admin",
            Eval(@"
                var user1 = { name: 'alice' };
                var roles = new Map();
                roles.set(user1, 'admin');
                'user1:' + roles.get(user1);
            "));
    }
}
