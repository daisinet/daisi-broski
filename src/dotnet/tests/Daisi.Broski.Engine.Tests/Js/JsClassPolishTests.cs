using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsClassPolishTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Getters
    // ========================================================

    [Fact]
    public void Instance_getter_runs_on_access()
    {
        Assert.Equal(
            25.0,
            Eval(@"
                class Square {
                    constructor(side) { this.side = side; }
                    get area() { return this.side * this.side; }
                }
                new Square(5).area;
            "));
    }

    [Fact]
    public void Getter_inherited_through_extends()
    {
        Assert.Equal(
            "alice",
            Eval(@"
                class Base {
                    constructor(n) { this._n = n; }
                    get name() { return this._n; }
                }
                class Child extends Base {}
                new Child('alice').name;
            "));
    }

    [Fact]
    public void Getter_overridden_in_subclass()
    {
        Assert.Equal(
            "child:alice",
            Eval(@"
                class Base {
                    constructor(n) { this._n = n; }
                    get name() { return this._n; }
                }
                class Child extends Base {
                    get name() { return 'child:' + this._n; }
                }
                new Child('alice').name;
            "));
    }

    // ========================================================
    // Setters
    // ========================================================

    [Fact]
    public void Setter_runs_on_assignment()
    {
        Assert.Equal(
            "stored:42",
            Eval(@"
                class Box {
                    set value(v) { this._v = 'stored:' + v; }
                    get value() { return this._v; }
                }
                var b = new Box();
                b.value = 42;
                b.value;
            "));
    }

    [Fact]
    public void Getter_setter_pair_on_same_name()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                class Counter {
                    constructor() { this._n = 0; }
                    get n() { return this._n; }
                    set n(v) { this._n = v; }
                }
                var c = new Counter();
                c.n = 10;
                c.n;
            "));
    }

    [Fact]
    public void Setter_only_property_returns_undefined_on_read()
    {
        // A property with a setter but no getter reads as
        // undefined — there's nothing to return.
        Assert.Equal(
            "undefined",
            Eval(@"
                class X {
                    set it(v) { this._v = v; }
                }
                var x = new X();
                x.it = 1;
                typeof x.it;
            "));
    }

    // ========================================================
    // Static getters / setters
    // ========================================================

    [Fact]
    public void Static_getter()
    {
        Assert.Equal(
            "static-name",
            Eval(@"
                class Util {
                    static get name() { return 'static-name'; }
                }
                Util.name;
            "));
    }

    [Fact]
    public void Static_setter_modifies_class_state()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                class Store {
                    static set value(v) { Store._v = v; }
                    static get value() { return Store._v; }
                }
                Store.value = 42;
                Store.value;
            "));
    }

    // ========================================================
    // Static fields
    // ========================================================

    [Fact]
    public void Static_field_initialized_at_class_creation()
    {
        Assert.Equal(
            "hello",
            Eval(@"
                class Config {
                    static greeting = 'hello';
                }
                Config.greeting;
            "));
    }

    [Fact]
    public void Static_field_with_expression_initializer()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                class Math2 {
                    static base = 5;
                    static doubled = 10;
                }
                Math2.doubled;
            "));
    }

    [Fact]
    public void Static_field_without_initializer_is_undefined()
    {
        Assert.Equal(
            "undefined",
            Eval(@"
                class X {
                    static flag;
                }
                typeof X.flag;
            "));
    }

    [Fact]
    public void Static_field_can_be_read_and_mutated()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                class Counter {
                    static count = 0;
                }
                Counter.count++;
                Counter.count = Counter.count + 1;
                Counter.count;
            "));
    }

    [Fact]
    public void Static_field_inherited_via_extends()
    {
        // Static lookup on a subclass walks the static
        // prototype chain to find the parent's static
        // field.
        Assert.Equal(
            "parent",
            Eval(@"
                class Parent {
                    static tag = 'parent';
                }
                class Child extends Parent {}
                Child.tag;
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Class_with_accessor_driven_validation()
    {
        Assert.Equal(
            "cannot-be-negative",
            Eval(@"
                class Account {
                    constructor() { this._balance = 0; }
                    get balance() { return this._balance; }
                    set balance(v) {
                        if (v < 0) {
                            this._error = 'cannot-be-negative';
                            return;
                        }
                        this._balance = v;
                    }
                    error() { return this._error; }
                }
                var a = new Account();
                a.balance = -10;
                a.error();
            "));
    }

    [Fact]
    public void Static_factory_and_instance_methods()
    {
        Assert.Equal(
            "(3,4)",
            Eval(@"
                class Point {
                    constructor(x, y) { this.x = x; this.y = y; }
                    static zero = new Point(0, 0);
                    static of(x, y) { return new Point(x, y); }
                    toString() { return '(' + this.x + ',' + this.y + ')'; }
                }
                Point.of(3, 4).toString();
            "));
    }
}
