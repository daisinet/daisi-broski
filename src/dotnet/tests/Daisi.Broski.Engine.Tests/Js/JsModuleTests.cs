using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsModuleTests
{
    /// <summary>
    /// Build an engine with an in-memory resolver over the
    /// given url → source map. All tests share this shape so
    /// each case can spell out its whole module graph as a
    /// dictionary literal.
    /// </summary>
    private static JsEngine MakeEngine(Dictionary<string, string> modules)
    {
        var eng = new JsEngine();
        eng.ModuleResolver = (specifier, referrer) =>
        {
            if (!modules.TryGetValue(specifier, out var src)) return null;
            return new ResolvedModule(specifier, src);
        };
        return eng;
    }

    // ========================================================
    // Basic named exports / imports
    // ========================================================

    [Fact]
    public void Named_export_and_import()
    {
        var eng = MakeEngine(new()
        {
            ["./greet.js"] = @"
                export const greeting = 'hello';
                export function greet(name) { return greeting + ' ' + name; }
            ",
        });
        var mod = eng.ImportModule("./greet.js");
        Assert.Equal("hello", mod.Exports.Get("greeting"));
        var fn = mod.Exports.Get("greet");
        Assert.IsType<JsFunction>(fn);
    }

    [Fact]
    public void Import_named_and_use_from_main()
    {
        var eng = MakeEngine(new()
        {
            ["./math.js"] = @"
                export function add(a, b) { return a + b; }
                export function mul(a, b) { return a * b; }
            ",
            ["./main.js"] = @"
                import { add, mul } from './math.js';
                export const result = add(2, 3) * mul(4, 5);
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(100.0, mod.Exports.Get("result"));
    }

    [Fact]
    public void Import_rename_with_as()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const x = 42;",
            ["./b.js"] = @"
                import { x as value } from './a.js';
                export const doubled = value * 2;
            ",
        });
        var mod = eng.ImportModule("./b.js");
        Assert.Equal(84.0, mod.Exports.Get("doubled"));
    }

    // ========================================================
    // Default exports
    // ========================================================

    [Fact]
    public void Default_export_value()
    {
        var eng = MakeEngine(new()
        {
            ["./config.js"] = @"export default { env: 'prod' };",
            ["./main.js"] = @"
                import config from './config.js';
                export const env = config.env;
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal("prod", mod.Exports.Get("env"));
    }

    [Fact]
    public void Default_export_function()
    {
        var eng = MakeEngine(new()
        {
            ["./add.js"] = @"export default function (a, b) { return a + b; }",
            ["./main.js"] = @"
                import add from './add.js';
                export const result = add(7, 8);
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(15.0, mod.Exports.Get("result"));
    }

    [Fact]
    public void Default_export_class()
    {
        var eng = MakeEngine(new()
        {
            ["./point.js"] = @"
                export default class Point {
                    constructor(x, y) { this.x = x; this.y = y; }
                    sum() { return this.x + this.y; }
                }
            ",
            ["./main.js"] = @"
                import Point from './point.js';
                var p = new Point(3, 4);
                export const result = p.sum();
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(7.0, mod.Exports.Get("result"));
    }

    [Fact]
    public void Default_and_named_exports_coexist()
    {
        var eng = MakeEngine(new()
        {
            ["./mixed.js"] = @"
                export const helper = 1;
                export default function () { return 'default'; }
            ",
            ["./main.js"] = @"
                import fn, { helper } from './mixed.js';
                export const result = fn() + ':' + helper;
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal("default:1", mod.Exports.Get("result"));
    }

    // ========================================================
    // Namespace imports
    // ========================================================

    [Fact]
    public void Namespace_import_exposes_all_named()
    {
        var eng = MakeEngine(new()
        {
            ["./lib.js"] = @"
                export const a = 1;
                export const b = 2;
                export function c() { return 3; }
            ",
            ["./main.js"] = @"
                import * as lib from './lib.js';
                export const sum = lib.a + lib.b + lib.c();
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(6.0, mod.Exports.Get("sum"));
    }

    // ========================================================
    // Export-specifier lists
    // ========================================================

    [Fact]
    public void Export_specifier_list_with_rename()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"
                var internal = 99;
                function helper() { return internal * 2; }
                export { internal as value, helper };
            ",
            ["./main.js"] = @"
                import { value, helper } from './a.js';
                export const out = value + '/' + helper();
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal("99/198", mod.Exports.Get("out"));
    }

    // ========================================================
    // Export forms: const / let / var / function / class
    // ========================================================

    [Fact]
    public void Export_let_is_exported()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export let counter = 5;",
            ["./main.js"] = @"
                import { counter } from './a.js';
                export const result = counter;
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(5.0, mod.Exports.Get("result"));
    }

    [Fact]
    public void Export_var_is_exported()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export var flag = true;",
            ["./main.js"] = @"
                import { flag } from './a.js';
                export const v = flag;
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(true, mod.Exports.Get("v"));
    }

    // ========================================================
    // Module caching
    // ========================================================

    [Fact]
    public void Module_is_cached_and_body_runs_once()
    {
        // If a module's body were re-run per import, the
        // counter it emits would go up on each load. We
        // verify it stays at 1 across two imports.
        var eng = MakeEngine(new()
        {
            ["./counted.js"] = @"
                export var count = 0;
                count++;
            ",
        });
        var mod1 = eng.ImportModule("./counted.js");
        var mod2 = eng.ImportModule("./counted.js");
        Assert.Same(mod1, mod2);
        Assert.Equal(1.0, mod1.Exports.Get("count"));
    }

    [Fact]
    public void Multiple_importers_see_same_instance()
    {
        var eng = MakeEngine(new()
        {
            ["./shared.js"] = @"export const id = {};",
            ["./a.js"] = @"
                import { id } from './shared.js';
                export const ref = id;
            ",
            ["./b.js"] = @"
                import { id } from './shared.js';
                export const ref = id;
            ",
        });
        var a = eng.ImportModule("./a.js");
        var b = eng.ImportModule("./b.js");
        // Both imports point at the exact same object
        // because the module is evaluated once.
        Assert.Same(a.Exports.Get("ref"), b.Exports.Get("ref"));
    }

    // ========================================================
    // Side-effect-only imports
    // ========================================================

    [Fact]
    public void Side_effect_import_evaluates_module()
    {
        var eng = MakeEngine(new()
        {
            ["./side.js"] = @"console.log('side loaded');",
            ["./main.js"] = @"
                import './side.js';
                export const done = true;
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(true, mod.Exports.Get("done"));
        Assert.Contains("side loaded", eng.ConsoleOutput.ToString());
    }

    // ========================================================
    // Error paths
    // ========================================================

    [Fact]
    public void Unresolved_specifier_throws()
    {
        var eng = MakeEngine(new());
        Assert.Throws<JsRuntimeException>(() =>
            eng.ImportModule("./missing.js"));
    }

    [Fact]
    public void Throws_when_no_resolver_set()
    {
        var eng = new JsEngine();
        Assert.Throws<InvalidOperationException>(() =>
            eng.ImportModule("./anything.js"));
    }

    // ========================================================
    // Multi-level imports + classes + Promise
    // ========================================================

    [Fact]
    public void Transitive_imports_and_rebind()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const base = 10;",
            ["./b.js"] = @"
                import { base } from './a.js';
                export const mid = base * 2;
            ",
            ["./c.js"] = @"
                import { mid } from './b.js';
                export const result = mid + 1;
            ",
        });
        var mod = eng.ImportModule("./c.js");
        Assert.Equal(21.0, mod.Exports.Get("result"));
    }

    [Fact]
    public void Module_with_class_export_and_new_call()
    {
        var eng = MakeEngine(new()
        {
            ["./counter.js"] = @"
                export class Counter {
                    constructor() { this.n = 0; }
                    inc() { this.n++; return this; }
                    value() { return this.n; }
                }
            ",
            ["./main.js"] = @"
                import { Counter } from './counter.js';
                var c = new Counter();
                c.inc().inc().inc();
                export const final = c.value();
            ",
        });
        var mod = eng.ImportModule("./main.js");
        Assert.Equal(3.0, mod.Exports.Get("final"));
    }

    [Fact]
    public void Module_exports_participate_in_promise_chain()
    {
        var eng = MakeEngine(new()
        {
            ["./api.js"] = @"
                export function fetchValue() {
                    return Promise.resolve(42);
                }
            ",
            ["./main.js"] = @"
                import { fetchValue } from './api.js';
                export const pending = fetchValue();
            ",
        });
        var mod = eng.ImportModule("./main.js");
        eng.DrainEventLoop();
        var pending = mod.Exports.Get("pending");
        Assert.IsType<JsPromise>(pending);
        Assert.Equal(PromiseState.Fulfilled, ((JsPromise)pending!).State);
        Assert.Equal(42.0, ((JsPromise)pending!).Value);
    }
}
