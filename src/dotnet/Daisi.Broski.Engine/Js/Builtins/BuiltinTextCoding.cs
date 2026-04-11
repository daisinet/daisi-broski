using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// WHATWG text encoding built-ins — <c>TextEncoder</c> and
/// <c>TextDecoder</c>. Minimum viable surface: UTF-8 only, no
/// stream state, no <c>fatal</c> / <c>ignoreBOM</c> options.
///
/// <para>
/// Non-UTF-8 labels (<c>"iso-8859-1"</c>, <c>"shift_jis"</c>, ...)
/// throw a script-visible <c>RangeError</c> — we haven't shipped
/// the legacy single-byte / multi-byte decoder tables yet, and
/// UTF-8 covers >99% of real-world decoding.
/// </para>
/// </summary>
internal static class BuiltinTextCoding
{
    public static void Install(JsEngine engine)
    {
        InstallTextEncoder(engine);
        InstallTextDecoder(engine);
    }

    private static void InstallTextEncoder(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        proto.SetNonEnumerable("encoding", "utf-8");

        proto.SetNonEnumerable("encode", new JsFunction("encode", (thisVal, args) =>
        {
            var input = args.Count == 0 || args[0] is JsUndefined
                ? ""
                : JsValue.ToJsString(args[0]);
            var bytes = Encoding.UTF8.GetBytes(input);
            return WrapAsUint8Array(engine, bytes);
        }));

        var ctor = new JsFunction("TextEncoder", (thisVal, args) =>
        {
            var instance = new JsObject { Prototype = proto };
            return instance;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["TextEncoder"] = ctor;
    }

    private static void InstallTextDecoder(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("decode", new JsFunction("decode", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is JsUndefined) return "";
            var source = args[0];
            byte[] bytes = ExtractBytes(source);
            return Encoding.UTF8.GetString(bytes);
        }));

        var ctor = new JsFunction("TextDecoder", (thisVal, args) =>
        {
            var label = args.Count > 0 && args[0] is not JsUndefined
                ? JsValue.ToJsString(args[0]).ToLowerInvariant()
                : "utf-8";
            if (label != "utf-8" && label != "utf8" && label != "unicode-1-1-utf-8")
            {
                JsThrow.RangeError($"TextDecoder: unsupported encoding '{label}' (only utf-8 is implemented)");
            }
            var instance = new JsObject { Prototype = proto };
            instance.SetNonEnumerable("encoding", "utf-8");
            instance.SetNonEnumerable("fatal", false);
            instance.SetNonEnumerable("ignoreBOM", false);
            return instance;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["TextDecoder"] = ctor;
    }

    /// <summary>
    /// Wrap a <see cref="byte"/>[] as a JS <c>Uint8Array</c>
    /// by constructing a fresh <see cref="JsArrayBuffer"/> +
    /// <see cref="JsTypedArray"/> and setting the prototype
    /// to the engine's <c>Uint8Array.prototype</c> — which
    /// lives under the global constructor's <c>prototype</c>
    /// slot, read at call time so the bridge doesn't have to
    /// thread a direct reference through.
    /// </summary>
    internal static JsTypedArray WrapAsUint8Array(JsEngine engine, byte[] bytes)
    {
        var buf = new JsArrayBuffer(bytes.Length);
        Array.Copy(bytes, buf.Data, bytes.Length);
        var arr = new JsTypedArray(TypedArrayKind.Uint8, buf, 0, bytes.Length);
        // Look up Uint8Array.prototype off the live global so
        // text-encoded data inherits the same methods the
        // user's own Uint8Array instances get.
        if (engine.Globals.TryGetValue("Uint8Array", out var ctor) &&
            ctor is JsFunction u8 &&
            u8.Get("prototype") is JsObject proto)
        {
            arr.Prototype = proto;
        }
        return arr;
    }

    /// <summary>
    /// Coerce a decoder input into raw bytes. Accepts
    /// <see cref="JsTypedArray"/> (any element kind — reads
    /// the underlying bytes in the visible range),
    /// <see cref="JsArrayBuffer"/> (whole buffer), or
    /// <see cref="JsDataView"/> (subrange). Anything else
    /// throws <c>TypeError</c>.
    /// </summary>
    private static byte[] ExtractBytes(object? source)
    {
        if (source is JsTypedArray ta)
        {
            var result = new byte[ta.ByteLength];
            Array.Copy(ta.Buffer.Data, ta.ByteOffset, result, 0, ta.ByteLength);
            return result;
        }
        if (source is JsArrayBuffer ab)
        {
            var result = new byte[ab.Data.Length];
            Array.Copy(ab.Data, result, ab.Data.Length);
            return result;
        }
        if (source is JsDataView dv)
        {
            var result = new byte[dv.ByteLength];
            Array.Copy(dv.Buffer.Data, dv.ByteOffset, result, 0, dv.ByteLength);
            return result;
        }
        JsThrow.TypeError("TextDecoder.decode: argument must be a BufferSource");
        return null!;
    }
}
