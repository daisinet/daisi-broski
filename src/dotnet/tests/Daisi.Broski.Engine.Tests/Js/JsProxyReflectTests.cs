using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-7: <c>Proxy</c> + <c>Reflect</c> — the
/// meta-object hooks frameworks use for reactivity (Vue 3,
/// MobX, Preact Signals). Covers the five traps we
/// implement (get / set / has / deleteProperty / ownKeys)
/// and the <c>Reflect</c> mirror surface.
/// </summary>
public class JsProxyReflectTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Proxy basics
    // ========================================================

    [Fact]
    public void Proxy_without_traps_falls_through_to_target()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var p = new Proxy({ x: 1 }, {});
                p.x;
            "));
    }

    [Fact]
    public void Proxy_get_trap_intercepts_read()
    {
        Assert.Equal(
            "trapped:x",
            Eval(@"
                var p = new Proxy({}, {
                    get: function (target, key) { return 'trapped:' + key; }
                });
                p.x;
            "));
    }

    [Fact]
    public void Proxy_get_trap_receives_target_and_key()
    {
        Assert.Equal(
            "target.val=42 key=val",
            Eval(@"
                var src = { val: 42 };
                var seenTarget;
                var seenKey;
                var p = new Proxy(src, {
                    get: function (t, k) { seenTarget = t; seenKey = k; return t[k]; }
                });
                p.val;
                'target.val=' + seenTarget.val + ' key=' + seenKey;
            "));
    }

    [Fact]
    public void Proxy_set_trap_intercepts_write()
    {
        Assert.Equal(
            "wrote:name=alice",
            Eval(@"
                var log = '';
                var p = new Proxy({}, {
                    set: function (target, key, value) {
                        log = 'wrote:' + key + '=' + value;
                        target[key] = value;
                    }
                });
                p.name = 'alice';
                log;
            "));
    }

    [Fact]
    public void Proxy_set_without_trap_writes_to_target()
    {
        Assert.Equal(
            "hello",
            Eval(@"
                var target = {};
                var p = new Proxy(target, {});
                p.greeting = 'hello';
                target.greeting;
            "));
    }

    [Fact]
    public void Proxy_has_trap_intercepts_in_operator()
    {
        Assert.Equal(
            false,
            Eval(@"
                var p = new Proxy({ x: 1 }, {
                    has: function () { return false; }
                });
                'x' in p;
            "));
    }

    [Fact]
    public void Proxy_has_without_trap_falls_through()
    {
        Assert.Equal(
            true,
            Eval(@"
                var p = new Proxy({ x: 1 }, {});
                'x' in p;
            "));
    }

    [Fact]
    public void Proxy_deleteProperty_trap_intercepts_delete()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var target = { x: 1 };
                var p = new Proxy(target, {
                    deleteProperty: function () { return false; }
                });
                delete p.x;
                target.x;
            "));
    }

    [Fact]
    public void Proxy_ownKeys_trap_controls_for_in()
    {
        Assert.Equal(
            "a,b",
            Eval(@"
                var p = new Proxy({}, {
                    ownKeys: function () { return ['a', 'b']; }
                });
                var out = [];
                for (var k in p) out.push(k);
                out.join(',');
            "));
    }

    [Fact]
    public void Proxy_construct_rejects_non_object_target()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new Proxy(42, {}); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void Proxy_construct_rejects_non_object_handler()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new Proxy({}, null); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    // ========================================================
    // Proxy + reactivity pattern
    // ========================================================

    [Fact]
    public void Proxy_observes_mutation_pattern()
    {
        // Emulates Vue-style reactivity: every property write
        // pushes an entry to a log for observers to inspect.
        Assert.Equal(
            "[x=1][y=2]",
            Eval(@"
                var log = '';
                function reactive(obj) {
                    return new Proxy(obj, {
                        set: function (t, k, v) {
                            t[k] = v;
                            log += '[' + k + '=' + v + ']';
                        }
                    });
                }
                var state = reactive({});
                state.x = 1;
                state.y = 2;
                log;
            "));
    }

    [Fact]
    public void Proxy_get_trap_can_wrap_nested_objects()
    {
        // A deep-reactive pattern: each read of a nested
        // object returns a fresh proxy wrapping the child.
        Assert.Equal(
            "nested:leaf",
            Eval(@"
                function reactive(obj) {
                    return new Proxy(obj, {
                        get: function (t, k) {
                            var v = t[k];
                            if (typeof v === 'object' && v !== null) {
                                return reactive(v);
                            }
                            return v;
                        }
                    });
                }
                var state = reactive({ nested: { child: 'leaf' } });
                'nested:' + state.nested.child;
            "));
    }

    // ========================================================
    // Reflect
    // ========================================================

    [Fact]
    public void Reflect_get_reads_property()
    {
        Assert.Equal(
            5.0,
            Eval("Reflect.get({ x: 5 }, 'x');"));
    }

    [Fact]
    public void Reflect_set_writes_property()
    {
        Assert.Equal(
            "written",
            Eval(@"
                var o = {};
                Reflect.set(o, 'k', 'written');
                o.k;
            "));
    }

    [Fact]
    public void Reflect_set_returns_true()
    {
        Assert.Equal(
            true,
            Eval("Reflect.set({}, 'k', 'v');"));
    }

    [Fact]
    public void Reflect_has_returns_boolean()
    {
        Assert.Equal(
            true,
            Eval("Reflect.has({ x: 1 }, 'x');"));
    }

    [Fact]
    public void Reflect_has_false_for_missing()
    {
        Assert.Equal(
            false,
            Eval("Reflect.has({}, 'x');"));
    }

    [Fact]
    public void Reflect_deleteProperty_removes_key()
    {
        Assert.Equal(
            true,
            Eval(@"
                var o = { x: 1 };
                var removed = Reflect.deleteProperty(o, 'x');
                removed && !('x' in o);
            "));
    }

    [Fact]
    public void Reflect_ownKeys_returns_array()
    {
        Assert.Equal(
            "a,b,c",
            Eval("Reflect.ownKeys({ a: 1, b: 2, c: 3 }).join(',');"));
    }

    [Fact]
    public void Reflect_getPrototypeOf_returns_prototype()
    {
        Assert.Equal(
            true,
            Eval(@"
                function Foo() {}
                var f = new Foo();
                Reflect.getPrototypeOf(f) === Foo.prototype;
            "));
    }

    [Fact]
    public void Reflect_setPrototypeOf_updates_chain()
    {
        Assert.Equal(
            "shared",
            Eval(@"
                var proto = { shared: 'shared' };
                var o = {};
                Reflect.setPrototypeOf(o, proto);
                o.shared;
            "));
    }

    [Fact]
    public void Reflect_apply_calls_function_with_args()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                function add(a, b) { return a + b; }
                Reflect.apply(add, null, [2, 4]);
            "));
    }

    [Fact]
    public void Reflect_apply_binds_this()
    {
        Assert.Equal(
            "ctx-42",
            Eval(@"
                function describe() { return this.tag + '-' + this.n; }
                Reflect.apply(describe, { tag: 'ctx', n: 42 }, []);
            "));
    }

    // ========================================================
    // Proxy + Reflect composition (framework pattern)
    // ========================================================

    [Fact]
    public void Proxy_trap_forwarding_via_Reflect()
    {
        // Canonical pattern: the handler forwards to
        // Reflect to preserve default behavior while
        // adding a side effect.
        Assert.Equal(
            "read:x,read:y",
            Eval(@"
                var log = [];
                var src = { x: 1, y: 2 };
                var p = new Proxy(src, {
                    get: function (t, k) {
                        log.push('read:' + k);
                        return Reflect.get(t, k);
                    }
                });
                var sum = p.x + p.y;
                log.join(',');
            "));
    }
}
