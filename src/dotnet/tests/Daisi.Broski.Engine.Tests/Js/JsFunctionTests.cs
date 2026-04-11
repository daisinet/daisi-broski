using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsFunctionTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- function declarations / expressions --------

    [Fact]
    public void Function_declaration_creates_callable_binding()
    {
        var eng = new JsEngine();
        eng.Evaluate("function greet() { return 'hello'; }");
        Assert.IsType<JsFunction>(eng.Globals["greet"]);
    }

    [Fact]
    public void Function_declaration_is_hoisted()
    {
        // Call before source-order definition.
        Assert.Equal(42.0, Eval("f(); function f() { return 42; }"));
    }

    [Fact]
    public void Function_expression_yields_a_function_value()
    {
        var fn = Assert.IsType<JsFunction>(Eval("(function () { return 1; });"));
        Assert.Null(fn.Name);
    }

    [Fact]
    public void Named_function_expression_records_its_name()
    {
        var fn = Assert.IsType<JsFunction>(Eval("(function fact(n) { return n; });"));
        Assert.Equal("fact", fn.Name);
    }

    [Fact]
    public void TypeOf_function_is_function()
    {
        Assert.Equal("function", Eval("typeof (function () {});"));
    }

    // -------- calling functions --------

    [Fact]
    public void Call_with_no_arguments()
    {
        Assert.Equal(7.0, Eval("function f() { return 7; } f();"));
    }

    [Fact]
    public void Call_with_arguments()
    {
        Assert.Equal(5.0, Eval("function add(a, b) { return a + b; } add(2, 3);"));
    }

    [Fact]
    public void Missing_arguments_are_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("function f(a, b) { return b; } f(1);"));
    }

    [Fact]
    public void Extra_arguments_are_silently_dropped()
    {
        Assert.Equal(3.0, Eval("function f(a, b) { return a + b; } f(1, 2, 99);"));
    }

    [Fact]
    public void Return_without_value_yields_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("function f() { return; } f();"));
    }

    [Fact]
    public void Falling_off_function_body_yields_undefined()
    {
        Assert.IsType<JsUndefined>(Eval("function f() {} f();"));
    }

    [Fact]
    public void Early_return_skips_remaining_statements()
    {
        Assert.Equal(1.0, Eval("function f() { return 1; return 2; } f();"));
    }

    // -------- recursion --------

    [Fact]
    public void Recursive_factorial()
    {
        Assert.Equal(
            120.0,
            Eval("function fact(n) { if (n <= 1) { return 1; } return n * fact(n - 1); } fact(5);"));
    }

    [Fact]
    public void Recursive_fibonacci()
    {
        Assert.Equal(
            55.0,
            Eval("function fib(n) { if (n < 2) { return n; } return fib(n-1) + fib(n-2); } fib(10);"));
    }

    // -------- closures --------

    [Fact]
    public void Counter_closure_captures_outer_variable()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function makeCounter() {
                var count = 0;
                return function () {
                    count = count + 1;
                    return count;
                };
            }
            var c = makeCounter();
            var a = c();
            var b = c();
            var cc = c();
        ");
        Assert.Equal(1.0, eng.Globals["a"]);
        Assert.Equal(2.0, eng.Globals["b"]);
        Assert.Equal(3.0, eng.Globals["cc"]);
    }

    [Fact]
    public void Two_counters_have_independent_state()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function make() { var n = 0; return function () { return ++n; }; }
            var a = make();
            var b = make();
            var r1 = a();
            var r2 = a();
            var r3 = b();
        ");
        Assert.Equal(1.0, eng.Globals["r1"]);
        Assert.Equal(2.0, eng.Globals["r2"]);
        Assert.Equal(1.0, eng.Globals["r3"]);
    }

    [Fact]
    public void Closure_reads_enclosing_var_after_outer_return()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                function outer() {
                    var x = 42;
                    function inner() { return x; }
                    return inner;
                }
                outer()();
            "));
    }

    // -------- this --------

    [Fact]
    public void Method_call_binds_this_to_receiver()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                var obj = {
                    val: 10,
                    get: function () { return this.val; }
                };
                obj.get();
            "));
    }

    [Fact]
    public void Detached_method_loses_this()
    {
        // Assigning the method to a var and calling it as a plain
        // function means `this` is undefined (strict-ish behavior).
        Assert.IsType<JsUndefined>(Eval(@"
            var obj = { val: 10, get: function () { return this; } };
            var g = obj.get;
            g();
        "));
    }

    [Fact]
    public void Computed_method_call_binds_this()
    {
        Assert.Equal(
            'X' + "",
            Eval(@"
                var obj = { k: function () { return 'X'; } };
                obj['k']();
            "));
    }

    // -------- new --------

    [Fact]
    public void New_allocates_an_instance_and_calls_constructor()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function Point(x, y) { this.x = x; this.y = y; }
            var p = new Point(3, 4);
        ");
        var p = Assert.IsType<JsObject>(eng.Globals["p"]);
        Assert.Equal(3.0, p.Properties["x"]);
        Assert.Equal(4.0, p.Properties["y"]);
    }

    [Fact]
    public void New_sets_prototype_chain()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function Thing() {}
            Thing.prototype.color = 'red';
            var t = new Thing();
        ");
        var t = Assert.IsType<JsObject>(eng.Globals["t"]);
        // Property resolution walks the prototype chain.
        Assert.Equal("red", t.Get("color"));
        // Own properties do not include the inherited one.
        Assert.False(t.Properties.ContainsKey("color"));
    }

    [Fact]
    public void Constructor_return_of_object_wins_over_new_instance()
    {
        // Constructors can return a different object; `new` uses
        // that instead of the freshly allocated `this`.
        var eng = new JsEngine();
        eng.Evaluate(@"
            function Weird() {
                this.x = 1;
                return { x: 99 };
            }
            var w = new Weird();
        ");
        var w = Assert.IsType<JsObject>(eng.Globals["w"]);
        Assert.Equal(99.0, w.Properties["x"]);
    }

    [Fact]
    public void Constructor_return_of_primitive_is_ignored()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function K() { this.tag = 'kept'; return 42; }
            var k = new K();
        ");
        var k = Assert.IsType<JsObject>(eng.Globals["k"]);
        Assert.Equal("kept", k.Properties["tag"]);
    }

    // -------- instanceof --------

    [Fact]
    public void Instanceof_returns_true_for_direct_instance()
    {
        Assert.Equal(
            true,
            Eval("function F() {} var f = new F(); f instanceof F;"));
    }

    [Fact]
    public void Instanceof_returns_false_for_unrelated_constructor()
    {
        Assert.Equal(
            false,
            Eval("function F() {} function G() {} var f = new F(); f instanceof G;"));
    }

    [Fact]
    public void Instanceof_walks_prototype_chain()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function Animal() {}
            function Dog() {}
            Dog.prototype = new Animal();
            var d = new Dog();
        ");
        Assert.Equal(true, eng.Evaluate("d instanceof Dog;"));
        Assert.Equal(true, eng.Evaluate("d instanceof Animal;"));
    }

    [Fact]
    public void Instanceof_of_primitive_is_false()
    {
        Assert.Equal(
            false,
            Eval("function F() {} 1 instanceof F;"));
    }

    // -------- arguments object --------

    [Fact]
    public void Arguments_holds_received_values_and_length()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function sum() {
                    var s = 0;
                    for (var i = 0; i < arguments.length; i++) {
                        s = s + arguments[i];
                    }
                    return s;
                }
                sum(1, 2, 3);
            "));
    }

    [Fact]
    public void Arguments_length_can_exceed_declared_parameters()
    {
        Assert.Equal(
            4.0,
            Eval("function f(a) { return arguments.length; } f(1, 2, 3, 4);"));
    }

    // -------- higher-order functions --------

    [Fact]
    public void Higher_order_function_returned_and_invoked()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                function multiplier(m) {
                    return function (x) { return x * m; };
                }
                var times5 = multiplier(5);
                times5(3);
            "));
    }

    [Fact]
    public void Function_as_argument_lets_a_callback_fire()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function apply(fn, a, b) { return fn(a, b); }
                apply(function (x, y) { return x + y; }, 2, 4);
            "));
    }

    // -------- var hoisting inside functions --------

    [Fact]
    public void Var_hoisting_inside_function_gives_undefined_before_init()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                function f() {
                    var before = typeof x;
                    var x = 1;
                    return before;
                }
                f();
            "));
    }

    [Fact]
    public void Nested_function_declaration_is_hoisted_in_its_enclosing_function()
    {
        Assert.Equal(
            "inner",
            Eval(@"
                function outer() {
                    return inner();
                    function inner() { return 'inner'; }
                }
                outer();
            "));
    }

    // -------- native function host integration --------

    [Fact]
    public void Host_installed_native_function_is_callable_from_script()
    {
        var eng = new JsEngine();
        object? capturedArg = null;
        eng.Globals["log"] = new JsFunction("log", (thisVal, args) =>
        {
            capturedArg = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        });
        eng.Evaluate("log('hello from script');");
        Assert.Equal("hello from script", capturedArg);
    }
}
