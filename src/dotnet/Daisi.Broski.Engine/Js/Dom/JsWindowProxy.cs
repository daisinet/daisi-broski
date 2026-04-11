namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// Minimal <c>window</c> shim for scripts that reference it.
/// Reads and writes route through the engine's
/// <see cref="JsEngine.Globals"/> dictionary, which is already
/// how script code accesses plain global variables —
/// <c>window.foo</c> and plain <c>foo</c> resolve to the same
/// binding. Plus a <c>document</c> accessor that mirrors the
/// engine's attached document.
///
/// This is not a spec-faithful <c>Window</c>. It's a minimum
/// viable facade so UMD / IIFE-style scripts that sniff for
/// <c>typeof window !== 'undefined'</c> keep working without
/// crashing the engine. A richer implementation — with
/// <c>location</c>, <c>navigator</c>, <c>localStorage</c>,
/// etc. — lands as its own slice when real sites need it.
/// </summary>
public sealed class JsWindowProxy : JsObject
{
    private readonly JsEngine _engine;

    public JsWindowProxy(JsEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "window" || key == "self" || key == "globalThis")
        {
            return this;
        }
        if (_engine.Globals.TryGetValue(key, out var v))
        {
            return v;
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        _engine.Globals[key] = value;
    }

    /// <inheritdoc />
    public override bool Has(string key)
    {
        if (_engine.Globals.ContainsKey(key)) return true;
        return base.Has(key);
    }
}
