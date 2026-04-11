using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsModulePolishTests
{
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
    // Re-exports
    // ========================================================

    [Fact]
    public void ExportFrom_specifier_list()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const value = 42; export function helper() { return 'h'; }",
            ["./b.js"] = @"export { value, helper } from './a.js';",
        });
        var mod = eng.ImportModule("./b.js");
        Assert.Equal(42.0, mod.Exports.Get("value"));
        Assert.IsType<JsFunction>(mod.Exports.Get("helper"));
    }

    [Fact]
    public void ExportFrom_with_rename()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const original = 'hello';",
            ["./b.js"] = @"export { original as greeting } from './a.js';",
        });
        var mod = eng.ImportModule("./b.js");
        Assert.Equal("hello", mod.Exports.Get("greeting"));
        // Original name isn't exposed.
        Assert.IsType<JsUndefined>(mod.Exports.Get("original"));
    }

    [Fact]
    public void ExportFrom_default_re_export()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export default 'the-thing';",
            ["./b.js"] = @"export { default as thing } from './a.js';",
        });
        var mod = eng.ImportModule("./b.js");
        Assert.Equal("the-thing", mod.Exports.Get("thing"));
    }

    [Fact]
    public void ExportStar_wildcard_copies_all_named()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"
                export const x = 1;
                export const y = 2;
                export default 'defaulted';  // NOT copied by wildcard
            ",
            ["./b.js"] = @"export * from './a.js';",
        });
        var mod = eng.ImportModule("./b.js");
        Assert.Equal(1.0, mod.Exports.Get("x"));
        Assert.Equal(2.0, mod.Exports.Get("y"));
        // default is excluded from `export *` per spec.
        Assert.IsType<JsUndefined>(mod.Exports.Get("default"));
    }

    [Fact]
    public void ExportStar_as_namespace()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const x = 1; export const y = 2;",
            ["./b.js"] = @"export * as inner from './a.js';",
        });
        var mod = eng.ImportModule("./b.js");
        var inner = mod.Exports.Get("inner") as JsObject;
        Assert.NotNull(inner);
        Assert.Equal(1.0, inner!.Get("x"));
        Assert.Equal(2.0, inner!.Get("y"));
    }

    [Fact]
    public void ReExport_chain_three_levels_deep()
    {
        var eng = MakeEngine(new()
        {
            ["./a.js"] = @"export const leaf = 'leaf-value';",
            ["./b.js"] = @"export { leaf } from './a.js';",
            ["./c.js"] = @"export { leaf } from './b.js';",
        });
        var mod = eng.ImportModule("./c.js");
        Assert.Equal("leaf-value", mod.Exports.Get("leaf"));
    }

    // ========================================================
    // Dynamic import()
    // ========================================================

    [Fact]
    public void Dynamic_import_returns_promise_of_namespace()
    {
        // Use the engine to call a top-level import() and
        // then call the returned promise's then() with our
        // own C# callback — simpler than trying to route
        // through a module-defined callback.
        var eng = new JsEngine();
        var modules = new Dictionary<string, string>
        {
            ["./a.js"] = "export const value = 42;",
        };
        eng.ModuleResolver = (specifier, referrer) =>
            modules.TryGetValue(specifier, out var src)
                ? new ResolvedModule(specifier, src)
                : (ResolvedModule?)null;
        // Trigger dynamic import via a script-level expression.
        var completion = eng.Evaluate("import('./a.js');");
        Assert.IsType<JsPromise>(completion);
        var p = (JsPromise)completion!;
        Assert.Equal(PromiseState.Fulfilled, p.State);
        var ns = p.Value as JsObject;
        Assert.NotNull(ns);
        Assert.Equal(42.0, ns!.Get("value"));
    }

    [Fact]
    public void Dynamic_import_missing_rejects()
    {
        var eng = new JsEngine();
        eng.ModuleResolver = (s, r) => null;
        var completion = eng.Evaluate("import('./missing.js');");
        var p = Assert.IsType<JsPromise>(completion);
        Assert.Equal(PromiseState.Rejected, p.State);
    }

    [Fact]
    public void Dynamic_import_inside_async_function()
    {
        var eng = new JsEngine();
        var modules = new Dictionary<string, string>
        {
            ["./math.js"] = "export function square(x) { return x * x; }",
        };
        eng.ModuleResolver = (specifier, referrer) =>
            modules.TryGetValue(specifier, out var src)
                ? new ResolvedModule(specifier, src)
                : (ResolvedModule?)null;
        // Run an async function that awaits a dynamic
        // import, and observe the resolved value via the
        // returned promise.
        var completion = eng.Evaluate(@"
            (async function () {
                var m = await import('./math.js');
                return m.square(7);
            })();
        ");
        eng.DrainEventLoop();
        var p = Assert.IsType<JsPromise>(completion);
        Assert.Equal(PromiseState.Fulfilled, p.State);
        Assert.Equal(49.0, p.Value);
    }
}
