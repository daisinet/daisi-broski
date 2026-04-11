namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Equality comparer implementing the ES2015 SameValueZero
/// algorithm — the comparison used by <c>Map</c>, <c>Set</c>,
/// <c>Array.prototype.includes</c>, and friends. Differs from
/// strict <c>===</c> in exactly one place: <c>NaN</c> is
/// considered equal to itself (so a <c>Map</c> can use <c>NaN</c>
/// as a key once and retrieve the entry again). <c>+0</c> and
/// <c>-0</c> are still treated as equal, as with <c>===</c>.
/// Objects compare by reference identity.
/// </summary>
internal sealed class SameValueZeroComparer : IEqualityComparer<object>
{
    public static readonly SameValueZeroComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        if (x is double dx)
        {
            if (y is not double dy) return false;
            if (double.IsNaN(dx) && double.IsNaN(dy)) return true;
            return dx == dy;
        }
        if (x is string sx) return y is string sy && sx == sy;
        if (x is bool bx) return y is bool by && bx == by;
        if (x is JsUndefined) return y is JsUndefined;
        if (x is JsNull) return y is JsNull;
        // Everything else (objects, functions, arrays, symbols):
        // reference identity.
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(object obj)
    {
        if (obj is null) return 0;
        if (obj is double d)
        {
            if (double.IsNaN(d)) return int.MinValue;
            if (d == 0.0) return 0;
            return d.GetHashCode();
        }
        if (obj is string s) return s.GetHashCode();
        if (obj is bool b) return b.GetHashCode();
        if (obj is JsUndefined) return 1;
        if (obj is JsNull) return 2;
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// ES2015 <c>Map</c> — an ordered key/value collection
/// supporting any value as a key under SameValueZero
/// equality. Backed by a <see cref="Dictionary{TKey, TValue}"/>
/// keyed with <see cref="SameValueZeroComparer"/>; .NET's
/// dictionary preserves insertion order in its enumeration,
/// which matches the spec's requirement that <c>Map</c>
/// iterate entries in the order they were added.
///
/// The <c>size</c> property is intercepted in <see cref="Get"/>
/// so it always reflects the current entry count — we don't
/// yet support real property-descriptor getters.
/// </summary>
public sealed class JsMap : JsObject
{
    // Keys are typed as non-nullable `object` because our VM
    // never represents JS `null` or `undefined` as .NET null
    // — both are singleton wrapper instances, so the key
    // slot is always populated.
    internal Dictionary<object, object?> Entries { get; }
        = new(SameValueZeroComparer.Instance!);

    public override object? Get(string key)
    {
        if (key == "size") return (double)Entries.Count;
        return base.Get(key);
    }

    public override bool Has(string key)
    {
        if (key == "size") return true;
        return base.Has(key);
    }

    internal void MapPut(object? k, object? v)
    {
        Entries[k ?? JsValue.Undefined] = v;
    }

    internal object? MapGet(object? k) =>
        Entries.TryGetValue(k ?? JsValue.Undefined, out var v) ? v : JsValue.Undefined;

    internal bool MapHas(object? k) => Entries.ContainsKey(k ?? JsValue.Undefined);

    internal bool MapDelete(object? k) => Entries.Remove(k ?? JsValue.Undefined);

    internal void MapClear() => Entries.Clear();
}

/// <summary>
/// ES2015 <c>Set</c> — an ordered unique-value collection
/// with SameValueZero equality. Uses a single
/// <see cref="Dictionary{TKey, TValue}"/> (value stored is
/// always <c>null</c>) instead of <see cref="HashSet{T}"/>
/// because we need the insertion-order guarantee. .NET's
/// <c>HashSet</c> does not promise enumeration order; .NET's
/// <c>Dictionary</c> does, since Core 3.0.
/// </summary>
public sealed class JsSet : JsObject
{
    internal Dictionary<object, byte> Entries { get; }
        = new(SameValueZeroComparer.Instance!);

    public override object? Get(string key)
    {
        if (key == "size") return (double)Entries.Count;
        return base.Get(key);
    }

    public override bool Has(string key)
    {
        if (key == "size") return true;
        return base.Has(key);
    }

    internal void SetAdd(object? v) { Entries[v ?? JsValue.Undefined] = 0; }
    internal bool SetHas(object? v) => Entries.ContainsKey(v ?? JsValue.Undefined);
    internal bool SetDelete(object? v) => Entries.Remove(v ?? JsValue.Undefined);
    internal void SetClear() => Entries.Clear();
}

/// <summary>
/// ES2015 <c>WeakMap</c>. Our implementation is not actually
/// weak — every engine instance is short-lived relative to
/// GC boundaries in typical headless use, and our runtime
/// does not expose garbage collection to JS anyway. Users
/// observe the same API surface (get / set / has / delete
/// with mandatory object keys) and cannot observe the
/// difference. True weak references can be layered on later
/// via <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>
/// without breaking any JS code.
/// </summary>
public sealed class JsWeakMap : JsObject
{
    internal Dictionary<JsObject, object?> Entries { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// ES2015 <c>WeakSet</c>. Same "not actually weak" caveat as
/// <see cref="JsWeakMap"/>.
/// </summary>
public sealed class JsWeakSet : JsObject
{
    internal HashSet<JsObject> Entries { get; } = new(ReferenceEqualityComparer.Instance);
}
