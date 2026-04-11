using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsObjectTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- object literals --------

    [Fact]
    public void Empty_object_literal_is_a_JsObject()
    {
        var o = Assert.IsType<JsObject>(Eval("({});"));
        Assert.Empty(o.Properties);
    }

    [Fact]
    public void Object_literal_with_identifier_keys()
    {
        var o = Assert.IsType<JsObject>(Eval("({a: 1, b: 2});"));
        Assert.Equal(1.0, o.Properties["a"]);
        Assert.Equal(2.0, o.Properties["b"]);
    }

    [Fact]
    public void Object_literal_with_string_and_number_keys_normalized_to_strings()
    {
        var o = Assert.IsType<JsObject>(Eval("({'foo': 1, 42: 'bar'});"));
        Assert.Equal(1.0, o.Properties["foo"]);
        Assert.Equal("bar", o.Properties["42"]);
    }

    [Fact]
    public void Object_literal_with_reserved_word_keys()
    {
        var o = Assert.IsType<JsObject>(Eval("({if: 1, for: 2, delete: 3});"));
        Assert.Equal(1.0, o.Properties["if"]);
        Assert.Equal(2.0, o.Properties["for"]);
        Assert.Equal(3.0, o.Properties["delete"]);
    }

    [Fact]
    public void Nested_object_literal()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = {a: {b: {c: 42}}};");
        var outer = Assert.IsType<JsObject>(eng.Globals["o"]);
        var a = Assert.IsType<JsObject>(outer.Properties["a"]);
        var b = Assert.IsType<JsObject>(a.Properties["b"]);
        Assert.Equal(42.0, b.Properties["c"]);
    }

    // -------- member access (read) --------

    [Fact]
    public void Dot_access_reads_property()
    {
        Assert.Equal(7.0, Eval("var o = {x: 7}; o.x;"));
    }

    [Fact]
    public void Computed_access_reads_property()
    {
        Assert.Equal(7.0, Eval("var o = {x: 7}; o['x'];"));
    }

    [Fact]
    public void Missing_property_reads_as_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("var o = {}; o.missing;"));
    }

    [Fact]
    public void Computed_key_coerces_to_string()
    {
        // Number key coerces to "1"; lookup against string-keyed property.
        Assert.Equal("one", Eval("var o = {'1': 'one'}; o[1];"));
    }

    [Fact]
    public void Reserved_word_dot_access()
    {
        Assert.Equal(1.0, Eval("var o = {if: 1}; o.if;"));
    }

    // -------- member access (write) --------

    [Fact]
    public void Dot_assignment_sets_property_and_returns_value()
    {
        var eng = new JsEngine();
        var r = eng.Evaluate("var o = {}; o.x = 5;");
        Assert.Equal(5.0, r);
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(5.0, o.Properties["x"]);
    }

    [Fact]
    public void Computed_assignment_sets_property()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = {}; var k = 'name'; o[k] = 'bob';");
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal("bob", o.Properties["name"]);
    }

    [Fact]
    public void Compound_assignment_reads_and_writes()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = {n: 10}; o.n += 5;");
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(15.0, o.Properties["n"]);
    }

    [Fact]
    public void Compound_assignment_on_computed_target()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = {n: 10}; var k = 'n'; o[k] *= 3;");
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(30.0, o.Properties["n"]);
    }

    [Fact]
    public void Prefix_update_on_member()
    {
        var eng = new JsEngine();
        var r = eng.Evaluate("var o = {x: 5}; ++o.x;");
        Assert.Equal(6.0, r);
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(6.0, o.Properties["x"]);
    }

    [Fact]
    public void Postfix_update_on_member_returns_old_value()
    {
        var eng = new JsEngine();
        var r = eng.Evaluate("var o = {x: 5}; o.x++;");
        Assert.Equal(5.0, r);
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(6.0, o.Properties["x"]);
    }

    [Fact]
    public void Postfix_update_on_computed_member()
    {
        var eng = new JsEngine();
        var r = eng.Evaluate("var o = {n: 2}; var k = 'n'; o[k]++;");
        Assert.Equal(2.0, r);
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.Equal(3.0, o.Properties["n"]);
    }

    // -------- delete --------

    [Fact]
    public void Delete_member_removes_it_and_returns_true()
    {
        var eng = new JsEngine();
        var r = eng.Evaluate("var o = {x: 1, y: 2}; delete o.x;");
        Assert.Equal(true, r);
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.False(o.Properties.ContainsKey("x"));
        Assert.True(o.Properties.ContainsKey("y"));
    }

    [Fact]
    public void Delete_computed_member_removes_it()
    {
        var eng = new JsEngine();
        eng.Evaluate("var o = {x: 1}; delete o['x'];");
        var o = Assert.IsType<JsObject>(eng.Globals["o"]);
        Assert.False(o.Properties.ContainsKey("x"));
    }

    // -------- in operator --------

    [Fact]
    public void In_operator_checks_own_property()
    {
        Assert.Equal(true, Eval("var o = {a: 1}; 'a' in o;"));
        Assert.Equal(false, Eval("var o = {a: 1}; 'b' in o;"));
    }

    [Fact]
    public void In_operator_coerces_key_to_string()
    {
        // Numeric key "42" stored, number 42 looks up same slot.
        Assert.Equal(true, Eval("var o = {42: 'x'}; 42 in o;"));
    }

    [Fact]
    public void In_on_non_object_throws()
    {
        Assert.Throws<JsRuntimeException>(() => Eval("'x' in 1;"));
    }

    // -------- array literals --------

    [Fact]
    public void Empty_array_literal()
    {
        var arr = Assert.IsType<JsArray>(Eval("[];"));
        Assert.Empty(arr.Elements);
    }

    [Fact]
    public void Array_literal_with_values()
    {
        var arr = Assert.IsType<JsArray>(Eval("[1, 2, 3];"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.Equal(1.0, arr.Elements[0]);
        Assert.Equal(2.0, arr.Elements[1]);
        Assert.Equal(3.0, arr.Elements[2]);
    }

    [Fact]
    public void Array_length_is_a_number()
    {
        Assert.Equal(3.0, Eval("[1, 2, 3].length;"));
    }

    [Fact]
    public void Array_index_access()
    {
        Assert.Equal("b", Eval("var a = ['a', 'b', 'c']; a[1];"));
    }

    [Fact]
    public void Array_out_of_bounds_access_is_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("var a = [1, 2]; a[99];"));
    }

    [Fact]
    public void Array_index_assignment_updates_slot()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = [1, 2, 3]; a[1] = 42;");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(42.0, arr.Elements[1]);
    }

    [Fact]
    public void Array_index_assignment_extends_length()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = []; a[3] = 'x';");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(4, arr.Elements.Count);
        Assert.Equal("x", arr.Elements[3]);
        // Slots 0-2 are undefined.
        Assert.IsType<JsUndefined>(arr.Elements[0]);
    }

    [Fact]
    public void Array_length_truncation_drops_elements()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = [1, 2, 3, 4, 5]; a.length = 2;");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(2, arr.Elements.Count);
    }

    [Fact]
    public void Array_element_update_expression()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = [1, 2, 3]; a[0]++;");
        var arr = Assert.IsType<JsArray>(eng.Globals["a"]);
        Assert.Equal(2.0, arr.Elements[0]);
    }

    [Fact]
    public void Array_string_coercion_joins_with_comma()
    {
        Assert.Equal("1,2,3", Eval("'' + [1, 2, 3];"));
    }

    [Fact]
    public void Empty_array_string_coerces_to_empty_string()
    {
        Assert.Equal("", Eval("'' + [];"));
    }

    // -------- typeof / equality --------

    [Fact]
    public void TypeOf_object_literal_is_object()
    {
        Assert.Equal("object", Eval("typeof {};"));
    }

    [Fact]
    public void TypeOf_array_literal_is_object()
    {
        Assert.Equal("object", Eval("typeof [];"));
    }

    [Fact]
    public void Strict_equality_between_objects_is_reference_identity()
    {
        Assert.Equal(false, Eval("({} === {});"));
        Assert.Equal(true, Eval("var o = {}; o === o;"));
    }

    [Fact]
    public void Object_in_boolean_context_is_truthy()
    {
        Assert.Equal(true, Eval("!!{};"));
        Assert.Equal(true, Eval("!![];"));
    }

    // -------- fluent usage --------

    [Fact]
    public void Build_a_counter_dictionary_by_iteration()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var counts = {a: 0, b: 0, c: 0};
            var letters = ['a', 'b', 'a', 'c', 'a', 'b'];
            for (var i = 0; i < letters.length; i++) {
                var k = letters[i];
                counts[k] = counts[k] + 1;
            }
        ");
        var counts = Assert.IsType<JsObject>(eng.Globals["counts"]);
        Assert.Equal(3.0, counts.Properties["a"]);
        Assert.Equal(2.0, counts.Properties["b"]);
        Assert.Equal(1.0, counts.Properties["c"]);
    }

    [Fact]
    public void Build_fibonacci_into_an_array()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var fib = [0, 1];
            for (var i = 2; i < 10; i++) {
                fib[i] = fib[i - 1] + fib[i - 2];
            }
        ");
        var arr = Assert.IsType<JsArray>(eng.Globals["fib"]);
        Assert.Equal(10, arr.Elements.Count);
        Assert.Equal(34.0, arr.Elements[9]);
    }
}
