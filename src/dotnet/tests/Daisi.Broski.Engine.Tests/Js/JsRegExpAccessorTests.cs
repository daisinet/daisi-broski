using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Tests for real accessor-shaped flag getters on
/// <c>RegExp.prototype</c>. Core-js / stripe / mongodb
/// polyfills inspect these via
/// <c>Object.getOwnPropertyDescriptor(RegExp.prototype, 'flags').get</c>,
/// expecting a function (accessor getter) rather than a
/// data descriptor — if we reported a data descriptor, the
/// polyfill installs its own (mis-configured) replacement
/// that then fails on our JsRegExp instances.
/// </summary>
public class JsRegExpAccessorTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    [Fact]
    public void Flags_getter_is_accessor_on_prototype()
    {
        Assert.Equal("function", Eval(
            "var d = Object.getOwnPropertyDescriptor(RegExp.prototype, 'flags'); typeof (d && d.get);"));
    }

    [Fact]
    public void HasIndices_getter_is_accessor()
    {
        Assert.Equal("function", Eval(
            "var d = Object.getOwnPropertyDescriptor(RegExp.prototype, 'hasIndices'); typeof (d && d.get);"));
    }

    [Fact]
    public void Flags_getter_called_with_regex_returns_string()
    {
        Assert.Equal("gi", Eval(
            "Object.getOwnPropertyDescriptor(RegExp.prototype, 'flags').get.call(/abc/gi);"));
    }

    [Fact]
    public void HasIndices_returns_true_for_d_flag()
    {
        Assert.Equal(true, Eval("new RegExp('x', 'd').hasIndices;"));
    }

    [Fact]
    public void HasIndices_returns_false_without_d_flag()
    {
        Assert.Equal(false, Eval("/x/.hasIndices;"));
    }

    [Fact]
    public void ObjectToString_tag_for_regex()
    {
        Assert.Equal("[object RegExp]", Eval("Object.prototype.toString.call(/x/);"));
    }

    [Fact]
    public void ObjectToString_tag_for_date()
    {
        Assert.Equal("[object Date]", Eval("Object.prototype.toString.call(new Date(0));"));
    }

    [Fact]
    public void For_in_var_does_not_clobber_enclosing_function()
    {
        // This is the exact pattern from stripe's polyfill that
        // used to silently corrupt state: the outer `a` is a
        // helper function, the inner for-in iterates with `var
        // a` — without function-scope hoisting the iteration
        // walks up and overwrites the outer `a`.
        var r = Eval(@"
            var a = function() { return 'outer'; };
            (function() {
                var i = {x: 1, y: 2};
                for (var a in i) { /* nothing */ }
            })();
            a();
        ");
        Assert.Equal("outer", r);
    }

    [Fact]
    public void InfiniteRecursion_surfaces_as_RangeError()
    {
        // A runaway recursion should raise a script-visible
        // RangeError rather than crashing the host with a CLR
        // StackOverflowException.
        var e = new JsEngine();
        Assert.Throws<JsRuntimeException>(() => e.Evaluate(@"
            function f() { return f(); }
            f();
        "));
    }
}
