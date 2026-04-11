using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsAssignDestructuringTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Array destructuring assignments
    // ========================================================

    [Fact]
    public void Array_assign_basic()
    {
        Assert.Equal(
            "1,2",
            Eval(@"
                var a, b;
                [a, b] = [1, 2];
                a + ',' + b;
            "));
    }

    [Fact]
    public void Array_assign_swap()
    {
        // Classic use of destructuring assignment: swap
        // without a temporary variable.
        Assert.Equal(
            "b,a",
            Eval(@"
                var x = 'a', y = 'b';
                [x, y] = [y, x];
                x + ',' + y;
            "));
    }

    [Fact]
    public void Array_assign_with_elision()
    {
        Assert.Equal(
            "1,3",
            Eval(@"
                var a, c;
                [a, , c] = [1, 2, 3];
                a + ',' + c;
            "));
    }

    [Fact]
    public void Array_assign_with_rest()
    {
        Assert.Equal(
            "1/2,3,4",
            Eval(@"
                var head, tail;
                [head, ...tail] = [1, 2, 3, 4];
                head + '/' + tail.join(',');
            "));
    }

    [Fact]
    public void Nested_array_assign()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var a, b, c;
                [a, [b, c]] = [1, [2, 3]];
                a + ',' + b + ',' + c;
            "));
    }

    // ========================================================
    // Object destructuring assignments
    // ========================================================

    [Fact]
    public void Object_assign_basic()
    {
        Assert.Equal(
            "alice:30",
            Eval(@"
                var name, age;
                ({name, age} = {name: 'alice', age: 30});
                name + ':' + age;
            "));
    }

    [Fact]
    public void Object_assign_with_rename()
    {
        Assert.Equal(
            7.0,
            Eval(@"
                var x;
                ({a: x} = {a: 7});
                x;
            "));
    }

    [Fact]
    public void Object_and_array_mix()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var a, b, c;
                ({x: a, y: [b, c]} = {x: 1, y: [2, 3]});
                a + ',' + b + ',' + c;
            "));
    }

    // ========================================================
    // Object shorthand in literals
    // ========================================================

    [Fact]
    public void Object_literal_shorthand_property()
    {
        Assert.Equal(
            "1/2",
            Eval(@"
                var a = 1, b = 2;
                var obj = {a, b};
                obj.a + '/' + obj.b;
            "));
    }

    [Fact]
    public void Object_literal_mixed_shorthand_and_colon()
    {
        Assert.Equal(
            "1/2/hi",
            Eval(@"
                var a = 1, b = 2;
                var obj = {a, b, greeting: 'hi'};
                obj.a + '/' + obj.b + '/' + obj.greeting;
            "));
    }

    // ========================================================
    // Object rest patterns
    // ========================================================

    [Fact]
    public void Object_rest_in_declaration()
    {
        Assert.Equal(
            "alice:30:admin",
            Eval(@"
                var {name, ...rest} = {name: 'alice', age: 30, role: 'admin'};
                name + ':' + rest.age + ':' + rest.role;
            "));
    }

    [Fact]
    public void Object_rest_leaves_no_keys()
    {
        Assert.Equal(
            "0",
            Eval(@"
                var {a, b, ...rest} = {a: 1, b: 2};
                Object.keys(rest).length + '';
            "));
    }

    [Fact]
    public void Object_rest_in_param()
    {
        Assert.Equal(
            "2,3",
            Eval(@"
                function pluck({first, ...others}) {
                    var keys = Object.keys(others);
                    keys.sort();
                    return keys.map(function (k) { return others[k]; }).join(',');
                }
                pluck({first: 1, b: 2, c: 3});
            "));
    }

    [Fact]
    public void Object_rest_with_rename()
    {
        Assert.Equal(
            "30:admin",
            Eval(@"
                var {name: n, ...rest} = {name: 'alice', age: 30, role: 'admin'};
                rest.age + ':' + rest.role;
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Destructure_from_function_return()
    {
        Assert.Equal(
            "alice/30",
            Eval(@"
                function getUser() { return {name: 'alice', age: 30}; }
                var name, age;
                ({name, age} = getUser());
                name + '/' + age;
            "));
    }

    [Fact]
    public void Object_spread_combined_with_rest()
    {
        Assert.Equal(
            "alice:manager:true",
            Eval(@"
                var base = {name: 'alice', role: 'admin'};
                var merged = {...base, role: 'manager', active: true};
                var {name, ...rest} = merged;
                name + ':' + rest.role + ':' + rest.active;
            "));
    }
}
