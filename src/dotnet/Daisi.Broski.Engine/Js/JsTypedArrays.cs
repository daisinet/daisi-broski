using System.Globalization;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Element type of a <see cref="JsTypedArray"/>. Determines
/// both the byte stride per element and how values are
/// clamped / coerced / reinterpreted on read and write.
/// </summary>
public enum TypedArrayKind
{
    Int8,
    Uint8,
    Uint8Clamped,
    Int16,
    Uint16,
    Int32,
    Uint32,
    Float32,
    Float64,
}

/// <summary>
/// ES2015 <c>ArrayBuffer</c> — a fixed-length raw byte
/// region. User code interacts with the bytes via typed
/// array views (<see cref="JsTypedArray"/>) or a
/// <see cref="JsDataView"/>. The buffer itself has no
/// integer-indexed access; reading <c>buf[0]</c> from JS
/// returns <c>undefined</c>, matching spec.
/// </summary>
public sealed class JsArrayBuffer : JsObject
{
    /// <summary>Raw backing storage. Always non-null.</summary>
    public byte[] Data { get; }

    public int ByteLength => Data.Length;

    public JsArrayBuffer(int byteLength)
    {
        if (byteLength < 0) byteLength = 0;
        Data = new byte[byteLength];
    }

    public override object? Get(string key)
    {
        if (key == "byteLength") return (double)ByteLength;
        return base.Get(key);
    }

    public override bool Has(string key)
    {
        if (key == "byteLength") return true;
        return base.Has(key);
    }
}

/// <summary>
/// Base type for every ES2015 numeric typed array —
/// <c>Int8Array</c>, <c>Uint8Array</c>, <c>Uint8ClampedArray</c>,
/// <c>Int16Array</c>, <c>Uint16Array</c>, <c>Int32Array</c>,
/// <c>Uint32Array</c>, <c>Float32Array</c>, <c>Float64Array</c>.
/// The concrete element type lives in <see cref="Kind"/>;
/// everything else is shared.
///
/// Integer indexing is handled in the <see cref="Get(string)"/>
/// / <see cref="Set(string, object?)"/> overrides: numeric-canonical
/// string keys route to <see cref="ReadElement"/> /
/// <see cref="WriteElement"/>, which read from or write to
/// the underlying <see cref="JsArrayBuffer.Data"/> at
/// <c>ByteOffset + index * ElementSize</c> using
/// <see cref="System.BitConverter"/>. Host byte order
/// matches the spec's "platform byte order" — little-endian
/// on every modern host. <see cref="JsDataView"/> is the
/// escape hatch for explicit-endian byte access.
/// </summary>
public sealed class JsTypedArray : JsObject
{
    public TypedArrayKind Kind { get; }
    public JsArrayBuffer Buffer { get; }
    public int ByteOffset { get; }
    /// <summary>Number of elements, not bytes.</summary>
    public int Length { get; }

    public int ElementSize => ElementSizeFor(Kind);

    public int ByteLength => Length * ElementSize;

    public JsTypedArray(
        TypedArrayKind kind,
        JsArrayBuffer buffer,
        int byteOffset,
        int length)
    {
        Kind = kind;
        Buffer = buffer;
        ByteOffset = byteOffset;
        Length = length;
    }

    /// <summary>Bytes per element for a given kind.</summary>
    public static int ElementSizeFor(TypedArrayKind kind) => kind switch
    {
        TypedArrayKind.Int8 => 1,
        TypedArrayKind.Uint8 => 1,
        TypedArrayKind.Uint8Clamped => 1,
        TypedArrayKind.Int16 => 2,
        TypedArrayKind.Uint16 => 2,
        TypedArrayKind.Int32 => 4,
        TypedArrayKind.Uint32 => 4,
        TypedArrayKind.Float32 => 4,
        TypedArrayKind.Float64 => 8,
        _ => 1,
    };

    public object? ReadElement(int index)
    {
        if (index < 0 || index >= Length) return JsValue.Undefined;
        int offset = ByteOffset + index * ElementSize;
        var data = Buffer.Data;
        return Kind switch
        {
            TypedArrayKind.Int8 => (double)(sbyte)data[offset],
            TypedArrayKind.Uint8 => (double)data[offset],
            TypedArrayKind.Uint8Clamped => (double)data[offset],
            TypedArrayKind.Int16 => (double)BitConverter.ToInt16(data, offset),
            TypedArrayKind.Uint16 => (double)BitConverter.ToUInt16(data, offset),
            TypedArrayKind.Int32 => (double)BitConverter.ToInt32(data, offset),
            TypedArrayKind.Uint32 => (double)BitConverter.ToUInt32(data, offset),
            TypedArrayKind.Float32 => (double)BitConverter.ToSingle(data, offset),
            TypedArrayKind.Float64 => BitConverter.ToDouble(data, offset),
            _ => JsValue.Undefined,
        };
    }

    public void WriteElement(int index, object? value)
    {
        if (index < 0 || index >= Length) return;
        int offset = ByteOffset + index * ElementSize;
        var data = Buffer.Data;
        double d = JsValue.ToNumber(value);

        switch (Kind)
        {
            case TypedArrayKind.Int8:
                data[offset] = unchecked((byte)(sbyte)JsValue.ToInt32(d));
                return;
            case TypedArrayKind.Uint8:
                data[offset] = unchecked((byte)(uint)JsValue.ToUint32(d));
                return;
            case TypedArrayKind.Uint8Clamped:
                data[offset] = ClampToUint8(d);
                return;
            case TypedArrayKind.Int16:
                {
                    short s = unchecked((short)JsValue.ToInt32(d));
                    data[offset] = (byte)(s & 0xFF);
                    data[offset + 1] = (byte)((s >> 8) & 0xFF);
                    return;
                }
            case TypedArrayKind.Uint16:
                {
                    ushort u = unchecked((ushort)(uint)JsValue.ToUint32(d));
                    data[offset] = (byte)(u & 0xFF);
                    data[offset + 1] = (byte)((u >> 8) & 0xFF);
                    return;
                }
            case TypedArrayKind.Int32:
                {
                    int i = JsValue.ToInt32(d);
                    data[offset] = (byte)(i & 0xFF);
                    data[offset + 1] = (byte)((i >> 8) & 0xFF);
                    data[offset + 2] = (byte)((i >> 16) & 0xFF);
                    data[offset + 3] = (byte)((i >> 24) & 0xFF);
                    return;
                }
            case TypedArrayKind.Uint32:
                {
                    uint u = (uint)JsValue.ToUint32(d);
                    data[offset] = (byte)(u & 0xFF);
                    data[offset + 1] = (byte)((u >> 8) & 0xFF);
                    data[offset + 2] = (byte)((u >> 16) & 0xFF);
                    data[offset + 3] = (byte)((u >> 24) & 0xFF);
                    return;
                }
            case TypedArrayKind.Float32:
                {
                    float f = (float)d;
                    var bytes = BitConverter.GetBytes(f);
                    data[offset] = bytes[0];
                    data[offset + 1] = bytes[1];
                    data[offset + 2] = bytes[2];
                    data[offset + 3] = bytes[3];
                    return;
                }
            case TypedArrayKind.Float64:
                {
                    var bytes = BitConverter.GetBytes(d);
                    for (int i = 0; i < 8; i++) data[offset + i] = bytes[i];
                    return;
                }
        }
    }

    /// <summary>
    /// Spec-mandated clamp for <c>Uint8ClampedArray</c>:
    /// NaN → 0, negatives → 0, values ≥ 256 → 255, and
    /// anything else rounds half-to-even (IEEE 754's
    /// default). We use <see cref="Math.Round(double, MidpointRounding)"/>
    /// with <c>ToEven</c> to match.
    /// </summary>
    private static byte ClampToUint8(double d)
    {
        if (double.IsNaN(d)) return 0;
        if (d <= 0) return 0;
        if (d >= 255) return 255;
        return (byte)Math.Round(d, MidpointRounding.ToEven);
    }

    public override object? Get(string key)
    {
        if (key == "length") return (double)Length;
        if (key == "byteLength") return (double)ByteLength;
        if (key == "byteOffset") return (double)ByteOffset;
        if (key == "buffer") return Buffer;
        if (key == "BYTES_PER_ELEMENT") return (double)ElementSize;
        if (TryParseIndex(key, out int idx))
        {
            return ReadElement(idx);
        }
        return base.Get(key);
    }

    public override void Set(string key, object? value)
    {
        if (TryParseIndex(key, out int idx) && idx >= 0 && idx < Length)
        {
            WriteElement(idx, value);
            return;
        }
        base.Set(key, value);
    }

    public override bool Has(string key)
    {
        if (key == "length" || key == "byteLength" || key == "byteOffset"
            || key == "buffer" || key == "BYTES_PER_ELEMENT") return true;
        if (TryParseIndex(key, out int idx) && idx >= 0 && idx < Length) return true;
        return base.Has(key);
    }

    public override IEnumerable<string> OwnKeys()
    {
        for (int i = 0; i < Length; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var k in Properties.Keys) yield return k;
    }

    /// <summary>
    /// Accept only the canonical integer-index strings
    /// ("0", "1", ..., never "01", "1.0", or "  1"). Matches
    /// <see cref="JsArray"/>'s parsing rule.
    /// </summary>
    private static bool TryParseIndex(string s, out int idx)
    {
        idx = 0;
        if (s.Length == 0) return false;
        if (s == "0") { idx = 0; return true; }
        if (s[0] == '0') return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out idx);
    }
}

/// <summary>
/// ES2015 <c>DataView</c> — explicit-endian reader/writer
/// over an <see cref="JsArrayBuffer"/>. Unlike typed arrays,
/// <see cref="JsDataView"/> methods take a byte offset and
/// an optional <c>littleEndian</c> flag on multi-byte
/// getters / setters (default is big-endian, per spec).
///
/// The getters return <see cref="double"/> values (since all
/// JS numbers are doubles); the integer getters sign-extend
/// or zero-extend as appropriate. Out-of-range offsets
/// throw <c>RangeError</c>-shaped errors — surfaced by the
/// built-in method wrappers, not by this class directly.
/// </summary>
public sealed class JsDataView : JsObject
{
    public JsArrayBuffer Buffer { get; }
    public int ByteOffset { get; }
    public int ByteLength { get; }

    public JsDataView(JsArrayBuffer buffer, int byteOffset, int byteLength)
    {
        Buffer = buffer;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
    }

    public override object? Get(string key)
    {
        if (key == "buffer") return Buffer;
        if (key == "byteOffset") return (double)ByteOffset;
        if (key == "byteLength") return (double)ByteLength;
        return base.Get(key);
    }

    public override bool Has(string key)
    {
        if (key == "buffer" || key == "byteOffset" || key == "byteLength") return true;
        return base.Has(key);
    }

    /// <summary>
    /// Read <paramref name="count"/> bytes starting at
    /// <paramref name="relativeOffset"/> into a freshly
    /// allocated little-endian-ordered array — if
    /// <paramref name="littleEndian"/> is false, the bytes
    /// are reversed before return so
    /// <see cref="BitConverter"/> (host byte order = LE)
    /// produces the spec value.
    /// </summary>
    internal byte[] ReadBytes(int relativeOffset, int count, bool littleEndian)
    {
        int start = ByteOffset + relativeOffset;
        var result = new byte[count];
        for (int i = 0; i < count; i++) result[i] = Buffer.Data[start + i];
        if (!littleEndian)
        {
            Array.Reverse(result);
        }
        return result;
    }

    internal void WriteBytes(int relativeOffset, byte[] bytes, bool littleEndian)
    {
        int start = ByteOffset + relativeOffset;
        if (!littleEndian)
        {
            var copy = (byte[])bytes.Clone();
            Array.Reverse(copy);
            bytes = copy;
        }
        for (int i = 0; i < bytes.Length; i++) Buffer.Data[start + i] = bytes[i];
    }
}
