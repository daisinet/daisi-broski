using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsDestructuringPolishTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Function parameter destructuring
    // ========================================================

    [Fact]
    public void Param_object_destructuring()
    {
        Assert.Equal(
            "alice:30",
            Eval(@"
                function greet({name, age}) { return name + ':' + age; }
                greet({name: 'alice', age: 30});
            "));
    }

    [Fact]
    public void Param_array_destructuring()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum3([a, b, c]) { return a + b + c; }
                sum3([1, 2, 3]);
            "));
    }

    [Fact]
    public void Param_object_with_rename()
    {
        Assert.Equal(
            "alice",
            Eval(@"
                function who({name: n}) { return n; }
                who({name: 'alice'});
            "));
    }

    [Fact]
    public void Param_with_default_on_leaf()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                function f({a = 5}) { return a; }
                f({});
            "));
    }

    [Fact]
    public void Param_with_default_on_whole_pattern()
    {
        Assert.Equal(
            7.0,
            Eval(@"
                function f({a, b} = {a: 3, b: 4}) { return a + b; }
                f();
            "));
    }

    [Fact]
    public void Nested_param_pattern()
    {
        Assert.Equal(
            "a,b",
            Eval(@"
                function f({outer: {inner: [x, y]}}) { return x + ',' + y; }
                f({outer: {inner: ['a', 'b']}});
            "));
    }

    [Fact]
    public void Param_destructuring_mixed_with_regular_params()
    {
        Assert.Equal(
            "10/alice",
            Eval(@"
                function f(n, {name}) { return n + '/' + name; }
                f(10, {name: 'alice'});
            "));
    }

    [Fact]
    public void Arrow_with_param_destructuring()
    {
        Assert.Equal(
            5.0,
            Eval(@"
                var f = ({x, y}) => x + y;
                f({x: 2, y: 3});
            "));
    }

    // ========================================================
    // Object spread in literals
    // ========================================================

    [Fact]
    public void Object_spread_copies_own_properties()
    {
        Assert.Equal(
            "alice:30",
            Eval(@"
                var base = {name: 'alice', age: 30};
                var clone = {...base};
                clone.name + ':' + clone.age;
            "));
    }

    [Fact]
    public void Object_spread_then_override()
    {
        Assert.Equal(
            "alice:31",
            Eval(@"
                var base = {name: 'alice', age: 30};
                var upgraded = {...base, age: 31};
                upgraded.name + ':' + upgraded.age;
            "));
    }

    [Fact]
    public void Object_spread_two_sources()
    {
        Assert.Equal(
            "1,2,3,4",
            Eval(@"
                var a = {p: 1, q: 2};
                var b = {r: 3, s: 4};
                var merged = {...a, ...b};
                merged.p + ',' + merged.q + ',' + merged.r + ',' + merged.s;
            "));
    }

    [Fact]
    public void Object_spread_later_wins()
    {
        Assert.Equal(
            "second",
            Eval(@"
                var a = {k: 'first'};
                var b = {k: 'second'};
                var m = {...a, ...b};
                m.k;
            "));
    }

    [Fact]
    public void Object_spread_of_null_is_noop()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var m = {...null, a: 1};
                m.a;
            "));
    }

    [Fact]
    public void Object_spread_of_undefined_is_noop()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var m = {...undefined, a: 1};
                m.a;
            "));
    }

    [Fact]
    public void Object_spread_preserves_source()
    {
        Assert.Equal(
            "alice",
            Eval(@"
                var base = {name: 'alice'};
                var clone = {...base};
                clone.name = 'bob';
                base.name;
            "));
    }

    // ========================================================
    // Destructuring in for-of head
    // ========================================================

    [Fact]
    public void For_of_object_pattern_head()
    {
        Assert.Equal(
            "alice+bob",
            Eval(@"
                var users = [{name: 'alice'}, {name: 'bob'}];
                var out = [];
                for (var {name} of users) out.push(name);
                out.join('+');
            "));
    }

    [Fact]
    public void For_of_array_pattern_head()
    {
        Assert.Equal(
            "1+2,3+4",
            Eval(@"
                var pairs = [[1, 2], [3, 4]];
                var out = [];
                for (var [a, b] of pairs) out.push(a + '+' + b);
                out.join(',');
            "));
    }

    [Fact]
    public void For_of_with_let_pattern()
    {
        Assert.Equal(
            3.0,
            Eval(@"
                var sum = 0;
                for (let {value} of [{value: 1}, {value: 2}]) {
                    sum += value;
                }
                sum;
            "));
    }

    [Fact]
    public void For_of_pattern_with_rename()
    {
        Assert.Equal(
            "alice,bob",
            Eval(@"
                var items = [{name: 'alice', age: 30}, {name: 'bob', age: 25}];
                var out = [];
                for (var {name: n} of items) out.push(n);
                out.join(',');
            "));
    }

    // ========================================================
    // Realistic combinations
    // ========================================================

    [Fact]
    public void Param_pattern_plus_object_spread()
    {
        Assert.Equal(
            "alice:30:true",
            Eval(@"
                function makeUser({name, age}) {
                    return {...arguments[0], active: true};
                }
                var u = makeUser({name: 'alice', age: 30});
                u.name + ':' + u.age + ':' + u.active;
            "));
    }

    [Fact]
    public void For_of_destructure_entries()
    {
        Assert.Equal(
            "a=1,b=2",
            Eval(@"
                var m = new Map([['a', 1], ['b', 2]]);
                var out = [];
                for (var [k, v] of m) out.push(k + '=' + v);
                out.join(',');
            "));
    }
}
