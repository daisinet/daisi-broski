using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Repros for JS engine issues surfaced while trying to
/// bootstrap blazor.web.js. Keeping them close to the
/// Blazor minified shapes so regressions are obvious.
/// </summary>
public class JsBlazorReproTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    [Fact]
    public void Basic_class_method_this_binding()
    {
        Assert.Equal(42.0, Eval(@"
            class H { constructor(v) { this._v = v; } get() { return this._v; } }
            var h = new H(42);
            h.get();
        "));
    }

    [Fact]
    public void Strict_mode_class_method_this_binding()
    {
        Assert.Equal("hello", Eval(@"
            'use strict';
            class H { constructor(v) { this._v = v; } get() { return this._v; } }
            var h = new H('hello');
            h.get();
        "));
    }

    [Fact]
    public void Blazor_cache_pattern_class_method()
    {
        // Minimal version of the Blazor
        // resolveInvocationHandler pattern that's crashing.
        Assert.Equal("v1", Eval(@"
            'use strict';
            class H {
                constructor() { this._cache = new Map(); }
                resolve(k) { return this._cache.get(k); }
            }
            var h = new H();
            h._cache.set('k1', 'v1');
            h.resolve('k1');
        "));
    }

    [Fact]
    public void Blazor_exact_call_pattern()
    {
        // Literal call shape from blazor.web.js:
        //   const o = u[t]; if(o) return o.resolveInvocationHandler(e, ...)
        // where u is a map/array of instances.
        Assert.Equal("v1", Eval(@"
            'use strict';
            class H {
                constructor() { this._cache = new Map(); }
                resolve(k) { return this._cache.get(k); }
            }
            var u = {};
            u['x'] = new H();
            u['x']._cache.set('k', 'v1');
            function S(e, t) {
                const o = u[t];
                if (o) return o.resolve(e);
                return null;
            }
            S('k', 'x');
        "));
    }

    [Fact]
    public void Computed_property_key_with_string_variable()
    {
        Assert.Equal(42.0, Eval(@"
            const d = 'x';
            const u = {[d]: 42};
            u.x;
        "));
    }

    [Fact]
    public void Computed_property_key_with_number_variable()
    {
        Assert.Equal(42.0, Eval(@"
            const d = 0;
            const u = {[d]: 42};
            u[0];
        "));
    }

    [Fact]
    public void Computed_property_key_with_string_literal()
    {
        // Bracketed string literal is literally equivalent
        // to the non-computed form {x: 42}. This one should
        // have been working already.
        Assert.Equal(42.0, Eval(@"
            const u = {['x']: 42};
            u.x;
        "));
    }

    [Fact]
    public void Numeric_key_stored_and_retrieved()
    {
        // Baseline: a number-literal key without computed
        // syntax. Ensures the engine treats 0 and '0' the
        // same on both set and get sides.
        Assert.Equal(42.0, Eval(@"
            const u = {0: 42};
            u[0];
        "));
    }

    [Fact]
    public void Computed_property_key_inside_iife()
    {
        Assert.Equal(42.0, Eval(@"
            (function() {
                const d = 0, u = {[d]: 42};
                globalThis.__r = u[0];
            })();
            globalThis.__r;
        "));
    }

    [Fact]
    public void Class_inside_iife_instance_via_computed_key()
    {
        Assert.Equal("v1", Eval(@"
            (function() {
                class H { constructor() { this._v = 'v1'; } }
                const d = 0, u = {[d]: new H()};
                globalThis.__r = u[0]._v;
            })();
            globalThis.__r;
        "));
    }

    [Fact]
    public void Blazor_module_iife_shape_with_computed_object_key()
    {
        // Direct shape from blazor.web.js:
        //   !function() {
        //     class h { ... }
        //     const d = 0, u = {[d]: new h(window)};
        //     u[0]._cachedHandlers.set(...)
        //     function S(e, t) { const o = u[t]; if(o) return o.method(e); }
        //   }();
        Assert.Equal("v1", Eval(@"
            (function() {
                'use strict';
                class H {
                    constructor(w) { this._w = w; this._cache = new Map(); }
                    resolve(k) { return this._cache.get(k); }
                }
                const d = 0, u = {[d]: new H('win')};
                u[0]._cache.set('import', 'v1');
                function S(e, t) {
                    const o = u[t];
                    if (o) return o.resolve(e);
                    return 'no-o';
                }
                globalThis.__r = S('import', 0);
            })();
            globalThis.__r;
        "));
    }

    [Fact]
    public void Class_method_inside_iife_shape()
    {
        // Blazor wraps everything in !function(){...}(); with
        // nested class defs — verify `this` binding survives
        // that wrapping.
        Assert.Equal(7.0, Eval(@"
            !function() {
                'use strict';
                class H {
                    constructor(v) { this._v = v; this._cache = new Map(); }
                    resolve(k) {
                        const hit = this._cache.get(k);
                        if (hit !== undefined) return hit;
                        const v = this._v + k.length;
                        this._cache.set(k, v);
                        return v;
                    }
                }
                var h = new H(3);
                globalThis.__result = h.resolve('abcd');
            }();
            globalThis.__result;
        "));
    }

    [Fact]
    public void Optional_chain_with_cached_getter_pattern()
    {
        // Literal shape from the Blazor minification:
        //   const o = null === (n = this._cachedHandlers.get(e)) || void 0 === n ? void 0 : n[t];
        Assert.Equal("found-val", Eval(@"
            'use strict';
            class H {
                constructor() { this._m = new Map(); }
                res(e, t) {
                    var n;
                    const o = null === (n = this._m.get(e)) || void 0 === n ? void 0 : n[t];
                    return o;
                }
            }
            var h = new H();
            h._m.set('k', { prop: 'found-val' });
            h.res('k', 'prop');
        "));
    }
}
