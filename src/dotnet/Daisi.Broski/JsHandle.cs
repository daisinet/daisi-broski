using System.Runtime.Versioning;
using Daisi.Broski.Ipc;

namespace Daisi.Broski;

/// <summary>
/// Host-side reference to a live JS object living inside the
/// sandbox child. Created by <see cref="BrowserSession.EvaluateAsync"/>
/// when the evaluation result is an object, and by
/// <see cref="BrowserSession.QuerySelectorAllHandlesAsync"/> /
/// <see cref="BrowserSession.QuerySelectorHandleAsync"/> for DOM
/// elements. Supports reading / writing properties and calling
/// methods on the remote object, keeping the JS↔DOM interaction
/// model fully symmetric across the IPC boundary.
///
/// <para>
/// Dispose (async) to release the handle in the sandbox. Leaving
/// a handle undisposed is a small memory leak inside the child
/// until the next navigate clears the table wholesale — so
/// explicit release is preferred but not load-bearing for
/// correctness.
/// </para>
///
/// <para>
/// <see cref="ElementHandle"/> is a subclass that adds
/// convenience shortcuts for DOM elements (<c>ClickAsync</c>,
/// <c>GetAttributeAsync</c>, <c>SetAttributeAsync</c>, etc.)
/// built on top of the generic property / method helpers.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class JsHandle : IAsyncDisposable
{
    /// <summary>The session the handle lives inside. Protected so
    /// <see cref="ElementHandle"/> and other subclasses can mint
    /// follow-on handles (e.g. <c>el.querySelector</c>) without
    /// re-plumbing the IPC transport.</summary>
    protected readonly BrowserSession _session;
    private bool _released;

    /// <summary>Opaque id the sandbox uses to find the backing
    /// object. Exposed for advanced host code that wants to
    /// build its own IPC payloads (e.g. passing a handle as
    /// an argument to another sandbox operation).</summary>
    public long Id { get; }

    /// <summary>Informational type tag the sandbox attached when
    /// it minted this handle: <c>"Element"</c>, <c>"Array"</c>,
    /// <c>"Function"</c>, <c>"Object"</c>, or a DOM subclass name.</summary>
    public string Type { get; }

    internal JsHandle(BrowserSession session, long id, string type)
    {
        _session = session;
        Id = id;
        Type = type;
    }

    /// <summary>Read a property off the remote object. Primitives
    /// come back as <see cref="IpcValue"/>; nested object values
    /// are re-registered as a fresh handle.</summary>
    public Task<IpcValue> GetPropertyRawAsync(string name, CancellationToken ct = default)
        => _session.RawGetPropertyAsync(Id, name, ct);

    /// <summary>Read a property and unbox to a strongly-typed
    /// primitive. Supported unboxings: <see cref="string"/>,
    /// <see cref="double"/>, <see cref="bool"/>. For object
    /// values use <see cref="GetHandleAsync"/> instead.</summary>
    public async Task<T?> GetPropertyAsync<T>(string name, CancellationToken ct = default)
    {
        var v = await GetPropertyRawAsync(name, ct).ConfigureAwait(false);
        return (T?)UnboxPrimitive(v, typeof(T));
    }

    /// <summary>Read a property whose value is known to be an
    /// object, wrap it in a fresh <see cref="JsHandle"/>. Returns
    /// <c>null</c> when the property is missing / null / a
    /// primitive.</summary>
    public async Task<JsHandle?> GetHandleAsync(string name, CancellationToken ct = default)
    {
        var v = await GetPropertyRawAsync(name, ct).ConfigureAwait(false);
        if (v.Kind != "handle" || v.HandleId is not long id) return null;
        return new JsHandle(_session, id, v.HandleType ?? "Object");
    }

    /// <summary>Write a property value on the remote object.
    /// Primitive values round-trip losslessly; to pass an
    /// existing handle as the value, use the
    /// <see cref="SetPropertyAsync(string, JsHandle, CancellationToken)"/>
    /// overload.</summary>
    public Task SetPropertyAsync(string name, string value, CancellationToken ct = default) =>
        _session.RawSetPropertyAsync(Id, name, IpcValue.Of(value), ct);

    public Task SetPropertyAsync(string name, double value, CancellationToken ct = default) =>
        _session.RawSetPropertyAsync(Id, name, IpcValue.Of(value), ct);

    public Task SetPropertyAsync(string name, bool value, CancellationToken ct = default) =>
        _session.RawSetPropertyAsync(Id, name, IpcValue.Of(value), ct);

    public Task SetPropertyAsync(string name, JsHandle value, CancellationToken ct = default) =>
        _session.RawSetPropertyAsync(Id, name, IpcValue.Handle(value.Id, value.Type), ct);

    /// <summary>Invoke a method on the remote object. The method
    /// is resolved via the normal JS property lookup, so
    /// prototype-chain methods (<c>obj.toString</c>,
    /// <c>el.getAttribute</c>, ...) are reachable. Args can be
    /// primitives or <see cref="JsHandle"/>s.</summary>
    public Task<IpcValue> CallMethodRawAsync(
        string name, IReadOnlyList<IpcValue>? args = null, CancellationToken ct = default) =>
        _session.RawCallMethodAsync(Id, name, args ?? Array.Empty<IpcValue>(), ct);

    public async Task<T?> CallMethodAsync<T>(
        string name, IReadOnlyList<IpcValue>? args = null, CancellationToken ct = default)
    {
        var v = await CallMethodRawAsync(name, args, ct).ConfigureAwait(false);
        return (T?)UnboxPrimitive(v, typeof(T));
    }

    /// <summary>Release the remote handle. Safe to call multiple
    /// times and from <c>DisposeAsync</c>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        try { await _session.RawReleaseHandlesAsync([Id]).ConfigureAwait(false); }
        catch { /* sandbox already gone */ }
    }

    /// <summary>Convert an IpcValue to the requested CLR type.
    /// Handles / objects / null return the default value for
    /// <typeparamref name="T"/>; use <see cref="GetHandleAsync"/>
    /// when you expect an object back.</summary>
    internal static object? UnboxPrimitive(IpcValue v, Type clrType)
    {
        switch (v.Kind)
        {
            case "undefined":
            case "null":
                return clrType.IsValueType ? Activator.CreateInstance(clrType) : null;
            case "bool":
                return v.Boolean ?? false;
            case "number":
                {
                    var d = v.Number ?? 0.0;
                    if (clrType == typeof(int)) return (int)d;
                    if (clrType == typeof(long)) return (long)d;
                    if (clrType == typeof(float)) return (float)d;
                    return d; // double / object
                }
            case "string":
                return v.String;
            case "bigint":
                return v.String;
            case "handle":
            default:
                return clrType.IsValueType ? Activator.CreateInstance(clrType) : null;
        }
    }
}
