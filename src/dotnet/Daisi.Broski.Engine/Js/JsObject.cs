using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

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
    /// Enumerate the own string-keyed property names in insertion
    /// order. Used by <c>for..in</c> (slice 4b) and by the
    /// <see cref="JsValue.ToJsString(object?)"/> fallback to
    /// render an object's shape during debugging.
    /// </summary>
    public virtual IEnumerable<string> OwnKeys() => Properties.Keys;
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

    public override IEnumerable<string> OwnKeys()
    {
        for (int i = 0; i < Elements.Count; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }
        yield return "length";
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
