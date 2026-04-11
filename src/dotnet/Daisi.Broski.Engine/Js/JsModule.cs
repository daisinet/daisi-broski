namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Linker state of a <see cref="JsModule"/>. Modules flow
/// one-way from <see cref="Loading"/> through
/// <see cref="Evaluating"/> to <see cref="Evaluated"/>. The
/// <see cref="Loading"/> state exists briefly so that the
/// loader can install a module into the cache before it
/// finishes evaluating — required for future circular
/// import support (not yet implemented).
/// </summary>
public enum ModuleState
{
    Loading,
    Evaluating,
    Evaluated,
}

/// <summary>
/// Runtime descriptor for a single ES2015 module instance.
/// Each import specifier resolves to exactly one JsModule —
/// the engine caches modules by their resolved URL so
/// multiple imports of the same specifier share bindings.
///
/// The <see cref="Exports"/> object is the module's "exports
/// namespace": its own properties are the module's named
/// exports plus a <c>default</c> property when the module
/// has a default export. The loader installs it before
/// evaluation so circular-import future work can see a
/// partially-populated object without crashing.
/// </summary>
public sealed class JsModule
{
    /// <summary>
    /// Resolved URL or identifier the engine uses as the
    /// cache key. Whatever the host resolver returned when
    /// asked to resolve a bare specifier against a referrer.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Exports namespace object. Property accesses like
    /// <c>ns.foo</c> read each named export; <c>default</c>
    /// holds the default export value if present.
    /// </summary>
    public JsObject Exports { get; }

    public ModuleState State { get; internal set; } = ModuleState.Loading;

    public JsModule(string url, JsObject exports)
    {
        Url = url;
        Exports = exports;
    }
}

/// <summary>
/// Host-pluggable module resolver hook. Takes the specifier
/// literal from the source (<c>"./foo"</c>, <c>"lodash"</c>,
/// a URL, etc.) and the URL of the module doing the import
/// (or <c>null</c> at the top-level entry). Returns the
/// resolved URL and the raw source text of the target
/// module, or <c>null</c> if the specifier can't be
/// resolved. The engine throws a <c>TypeError</c> when the
/// resolver returns null.
/// </summary>
public delegate ResolvedModule? ModuleResolver(string specifier, string? referrerUrl);

/// <summary>
/// What a <see cref="ModuleResolver"/> returns when a
/// specifier resolves successfully.
/// </summary>
public readonly struct ResolvedModule
{
    public string Url { get; }
    public string Source { get; }

    public ResolvedModule(string url, string source)
    {
        Url = url;
        Source = source;
    }
}
