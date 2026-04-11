using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// ES2015 Symbol value. We represent every JS symbol as a
/// unique .NET object — identity is the symbol's identity,
/// regardless of description. This is not a spec-faithful
/// primitive type (there is no real "symbol" typeof tag), but
/// it gives us the only feature symbols are actually used for
/// in practice: as unique keys for well-known extension points
/// like <c>Symbol.iterator</c>. Phase 3b-7a ships this as the
/// minimum-viable Symbol implementation. A proper primitive
/// representation can be layered later without user-visible
/// breakage if a site ever needs it.
/// </summary>
public sealed class JsSymbol
{
    /// <summary>
    /// Human-readable description used by <c>toString</c> and
    /// error messages. Has no effect on identity — two
    /// symbols with the same description are still distinct.
    /// </summary>
    public string? Description { get; }

    public JsSymbol(string? description = null)
    {
        Description = description;
    }

    public override string ToString() =>
        Description is null ? "Symbol()" : $"Symbol({Description})";
}

/// <summary>
/// Internal iterator state used by the <c>for..in</c> bytecode.
/// Holds a pre-collected snapshot of the enumerable string keys
/// in enumeration order (own properties first, then up the
/// prototype chain, skipping already-seen names) and an index
/// into the snapshot.
///
/// Snapshotting at <see cref="OpCode.ForInStart"/> time means
/// mutations to the object during iteration (adding or removing
/// keys in the loop body) do not affect the iteration order.
/// The real spec has more nuance — deleted-during-iteration keys
/// must be skipped — but browsers all differ on the corner cases
/// and phase 3a ships the predictable snapshot behavior.
/// </summary>
internal sealed class ForInIterator
{
    public List<string> Keys { get; }
    public int Index { get; set; }

    public ForInIterator(List<string> keys)
    {
        Keys = keys;
        Index = 0;
    }

    public static ForInIterator From(object? value)
    {
        if (value is not JsObject obj)
        {
            // null / undefined / primitives: iterate zero keys.
            return new ForInIterator(new List<string>());
        }
        var seen = new HashSet<string>();
        var keys = new List<string>();
        for (var cursor = obj; cursor is not null; cursor = cursor.Prototype)
        {
            foreach (var k in cursor.OwnKeys())
            {
                if (seen.Add(k)) keys.Add(k);
            }
        }
        return new ForInIterator(keys);
    }
}

/// <summary>
/// Base type for every JavaScript object value the VM sees —
/// object literals, arrays (see <see cref="JsArray"/>), and later
/// functions and host wrappers. Backed by a <see cref="Dictionary{TKey, TValue}"/>
/// of string-keyed properties, with an optional prototype reference
/// for chain lookup.
///
/// Slice-4a object literals have a null prototype — there is no
/// <c>Object.prototype</c> yet because slice 6 ships the built-in
/// library. Once that lands, literal objects will get their
/// prototype set on construction.
///
/// The property bag uses <see cref="Dictionary{TKey, TValue}"/>
/// directly and relies on .NET's insertion-order iteration
/// guarantee (documented since .NET Core 3.0) to preserve the
/// enumeration order that ES2015+ mandates for <c>for..in</c> /
/// <c>Object.keys</c>. If we need stricter ordering semantics
/// (e.g. integer keys before string keys, per the real spec)
/// we can switch to a dual-table representation when a test
/// regresses.
/// </summary>
public class JsObject
{
    public JsObject? Prototype { get; set; }

    /// <summary>
    /// Own properties of this object. Insertion-order preserving.
    /// Iteration via <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>
    /// matches the order in which keys were first inserted.
    /// </summary>
    public Dictionary<string, object?> Properties { get; } = new();

    /// <summary>
    /// Names that have been marked non-enumerable (skipped by
    /// <c>for..in</c> and <see cref="OwnKeys"/>). Phase 3a does
    /// not yet implement the full ES5 property descriptor model
    /// (configurable / writable / enumerable triples); this
    /// single-dimension flag is enough to keep built-in
    /// prototype methods from showing up during script-level
    /// enumeration, which is the only case that matters before
    /// slice 6b lands property descriptors properly.
    /// </summary>
    private HashSet<string>? _nonEnumerable;

    /// <summary>
    /// Symbol-keyed properties. Lazily allocated because most
    /// objects never use symbol keys. Slice 3b-7a introduces
    /// <c>Symbol.iterator</c>, which is currently the only
    /// well-known symbol we consult; the bag exists to make
    /// future symbol keys work without further plumbing.
    /// </summary>
    private Dictionary<JsSymbol, object?>? _symbolProperties;

    /// <summary>
    /// Get a property by name, walking the prototype chain.
    /// Returns <see cref="JsValue.Undefined"/> if the property
    /// is not found anywhere along the chain — JavaScript does
    /// not distinguish "missing" from "explicitly undefined" via
    /// the <c>[[Get]]</c> operation; callers that need that
    /// distinction use <c>hasOwnProperty</c> / <c>in</c>.
    /// </summary>
    public virtual object? Get(string key)
    {
        if (Properties.TryGetValue(key, out var v)) return v;
        return Prototype?.Get(key) ?? JsValue.Undefined;
    }

    /// <summary>
    /// Assign a property. Slice 4a does not yet implement the
    /// full <c>[[Set]]</c> machinery (no property descriptors,
    /// no accessors, no non-writable detection); assignment
    /// always goes to the own-property bag, shadowing any
    /// prototype-chain property.
    /// </summary>
    public virtual void Set(string key, object? value)
    {
        Properties[key] = value;
    }

    /// <summary>
    /// Assign a property and mark it non-enumerable (so
    /// <c>for..in</c> skips it). Used by the built-in library
    /// to install prototype methods without polluting script
    /// enumeration, matching the spec's
    /// <c>[[Enumerable]] = false</c> on standard prototypes.
    /// </summary>
    public void SetNonEnumerable(string key, object? value)
    {
        Properties[key] = value;
        _nonEnumerable ??= new HashSet<string>();
        _nonEnumerable.Add(key);
    }

    /// <summary>
    /// True if the given own property name is enumerable.
    /// Defaults to true for everything except names registered
    /// via <see cref="SetNonEnumerable"/>.
    /// </summary>
    public bool IsEnumerable(string key)
    {
        if (_nonEnumerable is null) return true;
        return !_nonEnumerable.Contains(key);
    }

    /// <summary>
    /// ES <c>in</c> operator: does the object (or any prototype
    /// in its chain) have an own or inherited property with this
    /// key?
    /// </summary>
    public virtual bool Has(string key)
    {
        if (Properties.ContainsKey(key)) return true;
        return Prototype?.Has(key) ?? false;
    }

    /// <summary>
    /// ES <c>delete</c> operator applied to an object member.
    /// Returns <c>true</c> if the own property was removed (or
    /// didn't exist). Prototype-chain properties are never
    /// touched — <c>delete</c> is an own-property operation.
    /// </summary>
    public virtual bool Delete(string key)
    {
        Properties.Remove(key);
        return true;
    }

    /// <summary>
    /// Enumerate the own <i>enumerable</i> string-keyed property
    /// names in insertion order. Used by <c>for..in</c> and by
    /// <see cref="ForInIterator.From"/>. Non-enumerable
    /// properties (installed via <see cref="SetNonEnumerable"/>)
    /// are skipped.
    /// </summary>
    public virtual IEnumerable<string> OwnKeys()
    {
        foreach (var k in Properties.Keys)
        {
            if (IsEnumerable(k)) yield return k;
        }
    }

    // -------- Symbol-keyed property access (slice 3b-7a) --------

    /// <summary>
    /// Look up a symbol-keyed property, walking the prototype
    /// chain. Returns <see cref="JsValue.Undefined"/> when the
    /// symbol is not found.
    /// </summary>
    public object? GetSymbol(JsSymbol key)
    {
        if (_symbolProperties is not null && _symbolProperties.TryGetValue(key, out var v))
        {
            return v;
        }
        return Prototype?.GetSymbol(key) ?? JsValue.Undefined;
    }

    /// <summary>
    /// True if this object (or any prototype in its chain) has
    /// a property under the given symbol key.
    /// </summary>
    public bool HasSymbol(JsSymbol key)
    {
        if (_symbolProperties is not null && _symbolProperties.ContainsKey(key))
        {
            return true;
        }
        return Prototype?.HasSymbol(key) ?? false;
    }

    /// <summary>
    /// Store a value under a symbol key. Always goes to this
    /// object's own symbol bag, shadowing any inherited
    /// property.
    /// </summary>
    public void SetSymbol(JsSymbol key, object? value)
    {
        _symbolProperties ??= new Dictionary<JsSymbol, object?>();
        _symbolProperties[key] = value;
    }
}

/// <summary>
/// Array object with integer-indexed dense storage and a virtual
/// <c>length</c> property. Inherits the string-keyed property bag
/// from <see cref="JsObject"/> for non-integer keys (JavaScript
/// arrays are objects, so <c>arr.foo = 1</c> is legal and stores
/// on the prototype's property bag).
///
/// Phase-3a deferrals:
///
/// - <b>Sparse storage.</b> Assigning <c>arr[1000000] = 'x'</c>
///   extends the backing list to 1,000,001 slots. A real engine
///   would detect sparsity and switch to a dictionary
///   representation; we will add that when a real site trips
///   the resulting memory blowup.
/// - <b>Holes.</b> The phase-3a parser represents
///   <c>[1, , 3]</c> with a null slot in
///   <see cref="ArrayExpression.Elements"/>; the compiler lowers
///   that to <c>undefined</c>, so arrays have no true "hole"
///   state where <c>0 in arr</c> would be false for an
///   explicit-undefined slot. <c>for..in</c> (slice 4b) will
///   enumerate all dense indices, matching the spec's treatment
///   of dense-with-undefined.
/// - <b><c>length</c> write coercion.</b> Writing a non-integer
///   <c>length</c> should throw <c>RangeError</c>; we currently
///   truncate via <see cref="JsValue.ToUint32(object?)"/>.
/// </summary>
public sealed class JsArray : JsObject
{
    /// <summary>Dense integer-indexed storage.</summary>
    public List<object?> Elements { get; } = new();

    public override object? Get(string key)
    {
        if (key == "length") return (double)Elements.Count;
        if (TryParseIndex(key, out int idx))
        {
            if (idx >= 0 && idx < Elements.Count) return Elements[idx];
            return JsValue.Undefined;
        }
        return base.Get(key);
    }

    public override void Set(string key, object? value)
    {
        if (key == "length")
        {
            int newLen = (int)JsValue.ToUint32(value);
            if (newLen < Elements.Count)
            {
                Elements.RemoveRange(newLen, Elements.Count - newLen);
            }
            else
            {
                while (Elements.Count < newLen) Elements.Add(JsValue.Undefined);
            }
            return;
        }
        if (TryParseIndex(key, out int idx) && idx >= 0)
        {
            while (Elements.Count <= idx) Elements.Add(JsValue.Undefined);
            Elements[idx] = value;
            return;
        }
        base.Set(key, value);
    }

    public override bool Has(string key)
    {
        if (key == "length") return true;
        if (TryParseIndex(key, out int idx))
        {
            return idx >= 0 && idx < Elements.Count;
        }
        return base.Has(key);
    }

    public override bool Delete(string key)
    {
        if (key == "length") return false; // non-configurable in the spec
        if (TryParseIndex(key, out int idx) && idx >= 0 && idx < Elements.Count)
        {
            // Per ECMA, delete on an array index just sets the slot
            // to undefined — it does not shift subsequent indices.
            Elements[idx] = JsValue.Undefined;
            return true;
        }
        return base.Delete(key);
    }

    /// <summary>
    /// Enumerable own property names. Matches what <c>for..in</c>
    /// should iterate: dense integer indices plus any
    /// string-keyed properties added via <c>arr.name = ...</c>.
    /// <c>length</c> is omitted because it is non-enumerable in
    /// ES5; <see cref="Get"/> still returns it, but
    /// <see cref="ForInIterator.From"/> will skip it.
    /// </summary>
    public override IEnumerable<string> OwnKeys()
    {
        for (int i = 0; i < Elements.Count; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var k in Properties.Keys) yield return k;
    }

    /// <summary>
    /// Element-separated comma join used by
    /// <see cref="JsValue.ToJsString(object?)"/> — matches
    /// <c>Array.prototype.toString</c>, which delegates to
    /// <c>join(',')</c>. Elements that are <c>null</c> or
    /// <c>undefined</c> render as the empty string, per spec.
    /// </summary>
    internal string Join(string separator)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Elements.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            var e = Elements[i];
            if (e is JsUndefined || e is JsNull) continue;
            sb.Append(JsValue.ToJsString(e));
        }
        return sb.ToString();
    }

    /// <summary>
    /// ES <c>ToUint32</c>-valid index check. Accepts only the
    /// canonical forms <c>"0"</c>, <c>"1"</c>, ... — not
    /// <c>"01"</c>, not <c>"  1"</c>, not <c>"1.0"</c>.
    /// </summary>
    private static bool TryParseIndex(string s, out int idx)
    {
        idx = 0;
        if (s.Length == 0) return false;
        if (s == "0") { idx = 0; return true; }
        if (s[0] == '0') return false; // leading zero
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out idx);
    }
}
