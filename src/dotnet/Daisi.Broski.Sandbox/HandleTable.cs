using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Js.Dom;
using Daisi.Broski.Ipc;

namespace Daisi.Broski.Sandbox;

/// <summary>
/// In-process table of live JS / DOM object references the host
/// can address across the IPC boundary.
///
/// <para>
/// The table maps monotonically-allocated <c>long</c> ids to the
/// boxed JS values the engine exposes. Strong references — the
/// host is responsible for explicit release via
/// <see cref="Methods.ReleaseHandles"/>. A future slice could
/// swap to <see cref="WeakReference{T}"/> once the JS GC is
/// weak-root aware; until then strong refs are simpler and the
/// worst-case leak is bounded by (a) page lifetime — the table
/// is cleared on navigate — and (b) host discipline.
/// </para>
///
/// <para>
/// The table does not do the IpcValue ↔ live-value translation
/// itself; that lives in <see cref="IpcValueCodec"/> so the
/// conversion can recurse (arrays, nested property reads) without
/// needing table access at every level.
/// </para>
/// </summary>
internal sealed class HandleTable
{
    private readonly Dictionary<long, object?> _byId = new();
    private long _nextId;

    /// <summary>Register <paramref name="value"/> and return the
    /// fresh id the host can use to refer to it later. The
    /// caller is responsible for deciding what gets registered —
    /// typically any boxed JS object (JsObject, JsFunction,
    /// JsArray, JsDomElement, ...) the host might want to reach
    /// again.</summary>
    public long Register(object? value)
    {
        long id = ++_nextId;
        _byId[id] = value;
        return id;
    }

    public bool TryGet(long id, out object? value) =>
        _byId.TryGetValue(id, out value);

    public bool Release(long id) => _byId.Remove(id);

    /// <summary>Drop every registration. Called on navigate so
    /// stale handles don't outlive their page.</summary>
    public void Clear() => _byId.Clear();

    public int Count => _byId.Count;
}

/// <summary>
/// Converts between <see cref="IpcValue"/> (wire form) and the
/// boxed JS values the engine uses internally. Object / array /
/// function values materialize as handles via the supplied
/// <see cref="HandleTable"/>; primitives round-trip losslessly.
/// </summary>
internal static class IpcValueCodec
{
    /// <summary>Box a JS-side value into an <see cref="IpcValue"/>.
    /// Object values are registered in the handle table and
    /// returned as <c>handle</c> kinds tagged with the best-fit
    /// type string (<c>"Element"</c>, <c>"Function"</c>,
    /// <c>"Array"</c>, <c>"Object"</c>).</summary>
    public static IpcValue Encode(object? value, HandleTable handles)
    {
        switch (value)
        {
            case null: return IpcValue.Null();
            case JsUndefined: return IpcValue.Undefined();
            case JsNull: return IpcValue.Null();
            case bool b: return IpcValue.Of(b);
            case double d: return IpcValue.Of(d);
            case string s: return IpcValue.Of(s);
            case System.Numerics.BigInteger bi:
                return IpcValue.BigInt(bi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Object values: register in the handle table and return
        // a typed handle. The type tag is informational — the
        // host uses it to decide which wrapper (ElementHandle,
        // JsHandle, etc.) to construct. DOM bridge wrappers are
        // checked first so a JsDomElement is reported as
        // "Element" rather than falling through to the JsObject
        // branch and being tagged as a plain "Object".
        string type = value switch
        {
            JsDomElement => "Element",
            JsDomDocument => "Document",
            JsDomText => "Text",
            JsDomNode => "Node",
            Element => "Element",
            Node => "Node",
            JsFunction => "Function",
            JsArray => "Array",
            _ => "Object",
        };

        // DOM nodes returned by the JS bridge are wrapped in
        // JsDomNode subclasses; those already look like JsObjects
        // with prototype access. Anything else is a plain JsObject
        // or an exotic builtin — all accessible via .Get()/.Set()
        // methods on the base JsObject class.
        long id = handles.Register(value);
        return IpcValue.Handle(id, type);
    }

    /// <summary>Resolve an <see cref="IpcValue"/> back into a
    /// JS-side boxed value. Primitives unbox directly; handle
    /// kinds look up the boxed object in the table (returns
    /// <c>null</c> if the handle was released).</summary>
    public static object? Decode(IpcValue value, HandleTable handles)
    {
        return value.Kind switch
        {
            "undefined" => JsValue.Undefined,
            "null" => JsValue.Null,
            "bool" => value.Boolean ?? false,
            "number" => value.Number ?? 0.0,
            "string" => value.String ?? "",
            "bigint" => System.Numerics.BigInteger.Parse(
                value.String ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            "handle" => value.HandleId is long id && handles.TryGet(id, out var v)
                ? v
                : JsValue.Undefined,
            _ => JsValue.Undefined,
        };
    }
}
