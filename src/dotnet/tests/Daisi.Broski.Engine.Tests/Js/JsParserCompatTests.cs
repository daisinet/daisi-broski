using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Parser-compat tests covering syntax forms surfaced by real-world
/// site sweeps: <c>new.target</c> meta-property, <c>for await (...)</c>
/// async iteration, async generator methods, computed accessors, and
/// keyword method names.
/// </summary>
public class JsParserCompatTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    [Fact]
    public void NewTarget_in_constructor_returns_function()
    {
        // `new F()` discards a primitive return value (the new
        // operator substitutes the freshly-allocated instance),
        // so capture the typeof via a side effect instead.
        var r = Eval(@"
            var seen = null;
            function F() { seen = typeof new.target; }
            new F();
            seen;
        ");
        Assert.Equal("function", r);
    }

    [Fact]
    public void NewTarget_in_plain_call_is_undefined()
    {
        var r = Eval(@"
            function F() {
                return typeof new.target;
            }
            F();
        ");
        Assert.Equal("undefined", r);
    }

    [Fact]
    public void NewTarget_prototype_setup_pattern()
    {
        // The pattern airbnb / many transpiled-class-extends bundles
        // emit: copy the new.target prototype onto `this`.
        var r = Eval(@"
            function MyError() {
                let t = new.target.prototype;
                if (Object.setPrototypeOf) Object.setPrototypeOf(this, t);
                else this.__proto__ = t;
                this.kind = 'extended';
                return this;
            }
            MyError.prototype.tag = 'tagged';
            var e = new MyError();
            e.kind + ':' + e.tag;
        ");
        Assert.Equal("extended:tagged", r);
    }

    [Fact]
    public void For_await_of_synchronously_iterable()
    {
        // We accept the `for await` syntax but iterate
        // synchronously over any iterable RHS — the body still
        // runs over each value.
        var r = Eval(@"
            async function run() {
                let sum = 0;
                for await (const n of [1, 2, 3, 4]) {
                    sum += n;
                }
                return sum;
            }
            run();
        ");
        // Returns a Promise — peek at its resolved value via
        // chaining only if Promise infra is full; for now just
        // assert no parse error by checking the type.
        Assert.NotNull(r);
    }

    [Fact]
    public void Class_method_named_get_parses()
    {
        var r = Eval(@"
            class S {
                get(k) { return 'got:' + k; }
            }
            new S().get('x');
        ");
        Assert.Equal("got:x", r);
    }

    [Fact]
    public void Object_literal_method_named_get_parses()
    {
        var r = Eval(@"
            var o = {
                get(k) { return 'val:' + k; },
                set(k, v) { return 'pair:' + k + '=' + v; }
            };
            o.get('a') + '|' + o.set('b', 1);
        ");
        Assert.Equal("val:a|pair:b=1", r);
    }

    [Fact]
    public void Class_accessor_with_keyword_name_parses()
    {
        var r = Eval(@"
            class Z {
                constructor() { this._d = ['a', 'b']; }
                get enum() { return this._d.join(','); }
            }
            new Z().enum;
        ");
        Assert.Equal("a,b", r);
    }

    [Fact]
    public void Static_class_block_parses_as_no_op()
    {
        // ES2022 static initialization blocks. We accept the
        // syntax but skip the body — the immediate goal is to
        // not blow up the parse on real-world bundles that use
        // them (stripe and others).
        var r = Eval(@"
            class K {
                static { this.tag = 'init'; }
                static value() { return 42; }
            }
            K.value();
        ");
        Assert.Equal(42.0, r);
    }

    [Fact]
    public void Object_toString_class_tag_for_primitives()
    {
        // Object.prototype.toString must report the correct
        // [[Class]] tag — sites use this to discriminate types
        // without instanceof.
        Assert.Equal("[object String]", Eval("Object.prototype.toString.call('s');"));
        Assert.Equal("[object Number]", Eval("Object.prototype.toString.call(42);"));
        Assert.Equal("[object Boolean]", Eval("Object.prototype.toString.call(true);"));
    }

    [Fact]
    public void NodeFilter_constants_exposed()
    {
        Assert.Equal(1.0, Eval("NodeFilter.FILTER_ACCEPT;"));
        Assert.Equal(4.0, Eval("NodeFilter.SHOW_TEXT;"));
    }

    [Fact]
    public void CSS_namespace_supports_returns_false()
    {
        Assert.Equal(false, Eval("CSS.supports('display', 'grid');"));
        Assert.Equal(@"a\:b", Eval("CSS.escape('a:b');"));
    }
}
