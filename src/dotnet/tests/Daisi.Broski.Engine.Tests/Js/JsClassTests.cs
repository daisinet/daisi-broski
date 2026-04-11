using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsClassTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Basic class declaration
    // ========================================================

    [Fact]
    public void Empty_class_can_be_instantiated()
    {
        Assert.Equal(
            "object",
            Eval(@"
                class Foo {}
                var f = new Foo();
                typeof f;
            "));
    }

    [Fact]
    public void Class_with_constructor_stores_fields()
    {
        Assert.Equal(
            7.0,
            Eval(@"
                class Point {
                    constructor(x, y) {
                        this.x = x;
                        this.y = y;
                    }
                }
                var p = new Point(3, 4);
                p.x + p.y;
            "));
    }

    [Fact]
    public void Class_instance_method_reads_this_field()
    {
        Assert.Equal(
            25.0,
            Eval(@"
                class Square {
                    constructor(side) { this.side = side; }
                    area() { return this.side * this.side; }
                }
                var s = new Square(5);
                s.area();
            "));
    }

    [Fact]
    public void Class_multiple_methods()
    {
        Assert.Equal(
            "50:15",
            Eval(@"
                class Calc {
                    constructor(x, y) { this.x = x; this.y = y; }
                    sum() { return this.x + this.y; }
                    product() { return this.x * this.y; }
                }
                var c = new Calc(5, 10);
                c.product() + ':' + c.sum();
            "));
    }

    [Fact]
    public void Class_method_is_nonenumerable_in_for_in()
    {
        // Instance methods should not appear in for..in
        // enumeration of an instance.
        Assert.Equal(
            "x,y",
            Eval(@"
                class Pt {
                    constructor(x, y) { this.x = x; this.y = y; }
                    distance() { return 0; }
                }
                var p = new Pt(1, 2);
                var keys = [];
                for (var k in p) keys.push(k);
                keys.join(',');
            "));
    }

    [Fact]
    public void Instanceof_recognizes_class_instances()
    {
        Assert.True((bool)Eval(@"
            class Foo {}
            var f = new Foo();
            f instanceof Foo;
        ")!);
    }

    // ========================================================
    // Static methods
    // ========================================================

    [Fact]
    public void Static_method_called_on_class()
    {
        Assert.Equal(
            "hello",
            Eval(@"
                class Util {
                    static greet() { return 'hello'; }
                }
                Util.greet();
            "));
    }

    [Fact]
    public void Static_method_receives_arguments()
    {
        Assert.Equal(
            7.0,
            Eval(@"
                class Math2 {
                    static add(a, b) { return a + b; }
                }
                Math2.add(3, 4);
            "));
    }

    [Fact]
    public void Static_and_instance_methods_dont_collide()
    {
        // Same method name on static and instance sides.
        Assert.Equal(
            "static:instance",
            Eval(@"
                class Foo {
                    static what() { return 'static'; }
                    what() { return 'instance'; }
                }
                Foo.what() + ':' + new Foo().what();
            "));
    }

    // ========================================================
    // extends
    // ========================================================

    [Fact]
    public void Subclass_inherits_parent_methods()
    {
        Assert.Equal(
            "parent",
            Eval(@"
                class Parent {
                    greet() { return 'parent'; }
                }
                class Child extends Parent {}
                new Child().greet();
            "));
    }

    [Fact]
    public void Subclass_instanceof_parent_is_true()
    {
        Assert.True((bool)Eval(@"
            class A {}
            class B extends A {}
            var b = new B();
            b instanceof B && b instanceof A;
        ")!);
    }

    [Fact]
    public void Subclass_constructor_with_super_call()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                class Parent {
                    constructor(x) { this.x = x; }
                }
                class Child extends Parent {
                    constructor(x, y) {
                        super(x);
                        this.y = y;
                    }
                }
                var c = new Child(4, 6);
                c.x + c.y;
            "));
    }

    [Fact]
    public void Default_subclass_constructor_forwards_args()
    {
        // No explicit constructor → generated as
        // constructor(...args){super(...args)}.
        Assert.Equal(
            42.0,
            Eval(@"
                class Parent {
                    constructor(v) { this.v = v; }
                }
                class Child extends Parent {}
                new Child(42).v;
            "));
    }

    [Fact]
    public void Super_method_call_resolves_through_parent_prototype()
    {
        Assert.Equal(
            "hi-child",
            Eval(@"
                class A {
                    greet() { return 'hi'; }
                }
                class B extends A {
                    greet() { return super.greet() + '-child'; }
                }
                new B().greet();
            "));
    }

    [Fact]
    public void Super_method_call_sees_this_of_subclass()
    {
        Assert.Equal(
            "3+4=7",
            Eval(@"
                class Pt {
                    constructor(x, y) { this.x = x; this.y = y; }
                    describe() { return this.x + '+' + this.y; }
                }
                class LabeledPt extends Pt {
                    describe() { return super.describe() + '=' + (this.x + this.y); }
                }
                new LabeledPt(3, 4).describe();
            "));
    }

    [Fact]
    public void Subclass_adds_own_method_alongside_inherited()
    {
        Assert.Equal(
            "parent:extra",
            Eval(@"
                class Parent {
                    base() { return 'parent'; }
                }
                class Child extends Parent {
                    extra() { return 'extra'; }
                }
                var c = new Child();
                c.base() + ':' + c.extra();
            "));
    }

    [Fact]
    public void Three_level_inheritance_chain()
    {
        Assert.Equal(
            "a:b:c",
            Eval(@"
                class A { a() { return 'a'; } }
                class B extends A { b() { return 'b'; } }
                class C extends B { c() { return 'c'; } }
                var obj = new C();
                obj.a() + ':' + obj.b() + ':' + obj.c();
            "));
    }

    [Fact]
    public void Static_method_inherits_through_extends()
    {
        Assert.Equal(
            "parent-static",
            Eval(@"
                class Parent {
                    static tag() { return 'parent-static'; }
                }
                class Child extends Parent {}
                Child.tag();
            "));
    }

    // ========================================================
    // super in various forms
    // ========================================================

    [Fact]
    public void Super_in_method_can_be_called_with_args()
    {
        Assert.Equal(
            9.0,
            Eval(@"
                class A {
                    mult(x, y) { return x * y; }
                }
                class B extends A {
                    tripled(a) { return super.mult(a, 3); }
                }
                new B().tripled(3);
            "));
    }

    [Fact]
    public void Super_in_constructor_passes_positional_args()
    {
        Assert.Equal(
            "alice/30",
            Eval(@"
                class Person {
                    constructor(name, age) {
                        this.name = name;
                        this.age = age;
                    }
                }
                class Employee extends Person {
                    constructor(name, age, role) {
                        super(name, age);
                        this.role = role;
                    }
                }
                var e = new Employee('alice', 30, 'dev');
                e.name + '/' + e.age;
            "));
    }

    // ========================================================
    // class expressions
    // ========================================================

    [Fact]
    public void Class_expression_assigned_to_variable()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var Box = class {
                    constructor(v) { this.v = v; }
                };
                new Box(42).v;
            "));
    }

    [Fact]
    public void Named_class_expression()
    {
        // The name is visible as the class function's name
        // property. (Internal-to-body self-reference is
        // deferred in this slice.)
        Assert.Equal(
            7.0,
            Eval(@"
                var F = class Named { constructor(x) { this.x = x; } };
                new F(7).x;
            "));
    }

    // ========================================================
    // error cases
    // ========================================================

    [Fact]
    public void Multiple_constructors_is_compile_error()
    {
        Assert.ThrowsAny<JsCompileException>(() =>
            Eval(@"
                class Foo {
                    constructor() {}
                    constructor() {}
                }
            "));
    }

    [Fact]
    public void Super_outside_class_method_throws()
    {
        // Bare `super.foo` in a regular function — HomeSuper
        // is null → runtime SyntaxError.
        Assert.Throws<JsRuntimeException>(() =>
            Eval(@"
                function plain() { return super.foo; }
                plain();
            "));
    }

    // ========================================================
    // realistic usage
    // ========================================================

    [Fact]
    public void Animal_hierarchy_with_overrides()
    {
        Assert.Equal(
            "Dog Rex says Woof (via Mammal via Animal)",
            Eval(@"
                class Animal {
                    constructor(name) { this.name = name; }
                    describe() { return this.type() + ' ' + this.name + ' says ' + this.sound(); }
                    type() { return 'Animal'; }
                    sound() { return '...'; }
                }
                class Mammal extends Animal {
                    type() { return 'Mammal via Animal'; }
                }
                class Dog extends Mammal {
                    type() { return 'Dog'; }
                    sound() { return 'Woof'; }
                    describe() { return super.describe() + ' (via ' + super.type() + ')'; }
                }
                new Dog('Rex').describe();
            "));
    }

    [Fact]
    public void Class_with_static_factory()
    {
        Assert.Equal(
            "pt(1,2)",
            Eval(@"
                class Pt {
                    constructor(x, y) { this.x = x; this.y = y; }
                    toString() { return 'pt(' + this.x + ',' + this.y + ')'; }
                    static origin() { return new Pt(0, 0); }
                    static of(x, y) { return new Pt(x, y); }
                }
                Pt.of(1, 2).toString();
            "));
    }

    [Fact]
    public void Class_method_uses_array_builtins()
    {
        Assert.Equal(
            14.0,
            Eval(@"
                class Bag {
                    constructor() { this.items = []; }
                    add(v) { this.items.push(v); return this; }
                    sum() { return this.items.reduce(function (a, b) { return a + b; }, 0); }
                }
                new Bag().add(5).add(9).sum();
            "));
    }

    [Fact]
    public void Class_can_be_block_scoped()
    {
        // `class Foo` at block scope should not leak out.
        Assert.Equal(
            "inside",
            Eval(@"
                var label = 'outside';
                {
                    class Foo { tag() { return 'inside'; } }
                    label = new Foo().tag();
                }
                label;
            "));
    }
}
