using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsDestructuringTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- object destructuring: shorthand --------

    [Fact]
    public void Object_shorthand_two_names()
    {
        Assert.Equal(
            3.0,
            Eval("var {a, b} = {a: 1, b: 2}; a + b;"));
    }

    [Fact]
    public void Object_shorthand_missing_property_is_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval("var {a, b} = {a: 1}; typeof b;"));
    }

    [Fact]
    public void Object_shorthand_three_names_mixed_types()
    {
        Assert.Equal(
            "alice:30:true",
            Eval(@"
                var {name, age, active} = {name: 'alice', age: 30, active: true};
                name + ':' + age + ':' + active;
            "));
    }

    // -------- object destructuring: rename --------

    [Fact]
    public void Object_rename_single()
    {
        Assert.Equal(
            7.0,
            Eval("var {a: x} = {a: 7}; x;"));
    }

    [Fact]
    public void Object_rename_mixed_with_shorthand()
    {
        Assert.Equal(
            "a=1 b=2",
            Eval("var {a: x, b} = {a: 1, b: 2}; 'a=' + x + ' b=' + b;"));
    }

    // -------- object destructuring: defaults --------

    [Fact]
    public void Object_default_applies_when_missing()
    {
        Assert.Equal(
            5.0,
            Eval("var {a = 5} = {}; a;"));
    }

    [Fact]
    public void Object_default_applies_when_undefined_but_not_when_null()
    {
        // Strict-equal comparison against undefined — null stays null.
        Assert.Equal(
            5.0,
            Eval("var {a = 5} = {a: undefined}; a;"));
        // `null` is not strictly-equal to `undefined`, so the default
        // does not kick in. Detect via typeof (JS null is "object").
        Assert.Equal(
            "object",
            Eval("var {a = 5} = {a: null}; typeof a;"));
    }

    [Fact]
    public void Object_default_ignored_when_value_present()
    {
        Assert.Equal(
            2.0,
            Eval("var {a = 5} = {a: 2}; a;"));
    }

    [Fact]
    public void Object_default_with_rename()
    {
        Assert.Equal(
            9.0,
            Eval("var {a: x = 9} = {}; x;"));
    }

    [Fact]
    public void Object_default_is_lazy_only_evaluated_when_missing()
    {
        Assert.Equal(
            "call-count=1",
            Eval(@"
                var calls = 0;
                function d() { calls++; return 99; }
                var {a = d()} = {a: 1};
                var {b = d()} = {};
                'call-count=' + calls;
            "));
    }

    [Fact]
    public void Object_default_expression_is_full_expression()
    {
        Assert.Equal(
            10.0,
            Eval("var x = 5; var {a = x * 2} = {}; a;"));
    }

    // -------- nested object destructuring --------

    [Fact]
    public void Nested_object_pattern_one_level()
    {
        Assert.Equal(
            42.0,
            Eval("var {outer: {inner}} = {outer: {inner: 42}}; inner;"));
    }

    [Fact]
    public void Nested_object_pattern_with_rename()
    {
        Assert.Equal(
            "core",
            Eval("var {a: {b: c}} = {a: {b: 'core'}}; c;"));
    }

    [Fact]
    public void Nested_object_pattern_with_defaults_at_leaf()
    {
        Assert.Equal(
            3.0,
            Eval("var {outer: {inner = 3}} = {outer: {}}; inner;"));
    }

    // -------- array destructuring: basic --------

    [Fact]
    public void Array_two_elements()
    {
        Assert.Equal(
            3.0,
            Eval("var [a, b] = [1, 2]; a + b;"));
    }

    [Fact]
    public void Array_three_elements()
    {
        Assert.Equal(
            "a,b,c",
            Eval("var [x, y, z] = ['a', 'b', 'c']; x + ',' + y + ',' + z;"));
    }

    [Fact]
    public void Array_more_targets_than_source_gives_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval("var [a, b, c] = [1, 2]; typeof c;"));
    }

    [Fact]
    public void Array_fewer_targets_than_source_ignores_tail()
    {
        Assert.Equal(
            3.0,
            Eval("var [a, b] = [1, 2, 3, 4]; a + b;"));
    }

    // -------- array destructuring: elisions --------

    [Fact]
    public void Array_elision_in_middle()
    {
        Assert.Equal(
            "a,c",
            Eval("var [a, , c] = ['a', 'b', 'c']; a + ',' + c;"));
    }

    [Fact]
    public void Array_leading_elision()
    {
        Assert.Equal(
            2.0,
            Eval("var [, b] = [1, 2]; b;"));
    }

    // -------- array destructuring: defaults --------

    [Fact]
    public void Array_default_applies_when_element_missing()
    {
        Assert.Equal(
            9.0,
            Eval("var [a = 9] = []; a;"));
    }

    [Fact]
    public void Array_default_applies_when_element_undefined()
    {
        Assert.Equal(
            9.0,
            Eval("var [a = 9] = [undefined]; a;"));
    }

    [Fact]
    public void Array_default_ignored_when_element_present()
    {
        Assert.Equal(
            1.0,
            Eval("var [a = 9] = [1]; a;"));
    }

    [Fact]
    public void Array_mixed_defaults_and_present()
    {
        Assert.Equal(
            "1,2,99",
            Eval("var [a = 10, b = 20, c = 99] = [1, 2]; a + ',' + b + ',' + c;"));
    }

    // -------- nested array destructuring --------

    [Fact]
    public void Nested_array_pattern()
    {
        Assert.Equal(
            "1,2,3",
            Eval("var [a, [b, c]] = [1, [2, 3]]; a + ',' + b + ',' + c;"));
    }

    [Fact]
    public void Array_inside_object_pattern()
    {
        Assert.Equal(
            "1,2",
            Eval("var {pts: [a, b]} = {pts: [1, 2]}; a + ',' + b;"));
    }

    [Fact]
    public void Object_inside_array_pattern()
    {
        Assert.Equal(
            5.0,
            Eval("var [{val}] = [{val: 5}]; val;"));
    }

    // -------- let and const --------

    [Fact]
    public void Let_destructure_object()
    {
        Assert.Equal(
            4.0,
            Eval("let {a, b} = {a: 1, b: 3}; a + b;"));
    }

    [Fact]
    public void Const_destructure_array()
    {
        Assert.Equal(
            5.0,
            Eval("const [a, b] = [2, 3]; a + b;"));
    }

    [Fact]
    public void Let_destructure_nested_block_scoped()
    {
        // Block-scoping still works when the bindings come
        // from a pattern.
        Assert.Equal(
            "inner=1 outer=undefined",
            Eval(@"
                {
                    let {x} = {x: 1};
                    var inner = 'inner=' + x;
                }
                inner + ' outer=' + typeof x;
            "));
    }

    // -------- multiple declarators --------

    [Fact]
    public void Multiple_declarators_mixing_identifier_and_pattern()
    {
        Assert.Equal(
            "1:2:3",
            Eval(@"
                var a = 1, {b, c} = {b: 2, c: 3};
                a + ':' + b + ':' + c;
            "));
    }

    // -------- in a function body --------

    [Fact]
    public void Destructure_function_return_value()
    {
        Assert.Equal(
            "alice/30",
            Eval(@"
                function getUser() { return {name: 'alice', age: 30}; }
                var {name, age} = getUser();
                name + '/' + age;
            "));
    }

    [Fact]
    public void Destructure_inside_function_body_with_closure()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum3(arr) {
                    var [a, b, c] = arr;
                    return a + b + c;
                }
                sum3([1, 2, 3]);
            "));
    }

    // -------- realistic usage --------

    [Fact]
    public void Swap_via_array_destructuring_from_literal()
    {
        // A common idiom — note: this is swap via fresh literal,
        // not the assignment-expression form (which is deferred).
        Assert.Equal(
            "b,a",
            Eval(@"
                var x = 'a', y = 'b';
                var [x2, y2] = [y, x];
                x2 + ',' + y2;
            "));
    }

    [Fact]
    public void Pluck_fields_with_defaults_from_config()
    {
        Assert.Equal(
            "host=localhost port=8080 tls=true",
            Eval(@"
                var cfg = {host: 'localhost', tls: true};
                var {host = '0.0.0.0', port = 8080, tls = false} = cfg;
                'host=' + host + ' port=' + port + ' tls=' + tls;
            "));
    }

    [Fact]
    public void Destructure_inside_json_parse_result()
    {
        Assert.Equal(
            "ok:1",
            Eval(@"
                var {status, value} = JSON.parse('{""status"":""ok"",""value"":1}');
                status + ':' + value;
            "));
    }

    // -------- parse-time errors --------

    [Fact]
    public void Destructuring_declaration_without_initializer_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() => Eval("var {a};"));
    }

    [Fact]
    public void Array_destructuring_declaration_without_initializer_is_syntax_error()
    {
        Assert.Throws<JsParseException>(() => Eval("let [a];"));
    }

    // -------- destructuring-assignment to member targets --------
    // Regression guard for the Blazor-breaker: before 6av the
    // compiler rejected MemberExpression as a destructuring
    // target and blazor.web.js (which uses this pattern for its
    // DotNet/JS import table) failed to compile before any
    // Blazor bootstrap could run.

    [Fact]
    public void Assignment_destructuring_into_member_dot_access()
    {
        Assert.Equal(3.0, Eval(
            "var obj = {}; ({a: obj.x, b: obj.y} = {a: 1, b: 2}); obj.x + obj.y;"));
    }

    [Fact]
    public void Assignment_destructuring_into_computed_member()
    {
        Assert.Equal(30.0, Eval(
            "var arr = []; [arr[0], arr[1]] = [10, 20]; arr[0] + arr[1];"));
    }

    [Fact]
    public void Assignment_destructuring_mixes_members_and_identifiers()
    {
        Assert.Equal("alice:30", Eval(@"
            var obj = {};
            var age;
            ({name: obj.n, age} = {name: 'alice', age: 30});
            obj.n + ':' + age;
        "));
    }

    [Fact]
    public void Assignment_destructuring_with_nested_member_patterns()
    {
        Assert.Equal(42.0, Eval(@"
            var out = {inner: {}};
            ({a: {b: out.inner.c}} = {a: {b: 42}});
            out.inner.c;
        "));
    }
}
