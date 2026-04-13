using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Blob</c> + <c>File</c> — the Web platform's immutable binary
/// containers. Pages use <c>Blob</c> for
/// <c>URL.createObjectURL</c>, <c>fetch(response).blob()</c>,
/// upload bodies, and <c>FileReader</c> input; <c>File</c> (a
/// subclass carrying <c>name</c> + <c>lastModified</c>) is what
/// <c>&lt;input type=file&gt;</c> hands to drag-and-drop
/// consumers and <c>FormData</c> uploads.
///
/// <para>
/// The implementation keeps the backing bytes as a plain
/// <c>byte[]</c> — sufficient for every real-world call path
/// we need to support without a streaming layer. A future
/// slice can route <c>blob.stream()</c> through a
/// <c>ReadableStream</c> once the engine's stream surface
/// grows past the current stub.
/// </para>
/// </summary>
internal static class BuiltinBlob
{
    public static void Install(JsEngine engine)
    {
        var blobProto = InstallBlobPrototype(engine);
        InstallBlobConstructor(engine, blobProto);

        var fileProto = new JsObject { Prototype = blobProto };
        InstallFileConstructor(engine, fileProto, blobProto);

        // Bolt Response.prototype.blob onto the existing
        // Response prototype from BuiltinFetch. Runs here rather
        // than inside BuiltinFetch so the Blob prototype is
        // ready to serve as the returned blob's [[Prototype]].
        if (engine.Globals.TryGetValue("Response", out var respCtor) &&
            respCtor is JsFunction respFn &&
            respFn.Get("prototype") is JsObject respProto)
        {
            respProto.SetNonEnumerable("blob", new JsFunction("blob", (thisVal, args) =>
            {
                if (thisVal is not JsFetchResponse r)
                {
                    JsThrow.TypeError("Response.prototype.blob called on non-Response");
                    return null; // unreachable
                }
                // Prefer the Content-Type header if present so the
                // blob's `type` round-trips through the HTTP layer.
                var contentType = r.Headers.Count == 0 ? "" :
                    JsValue.ToJsString(r.Headers.Get("content-type") ?? "");
                var blob = new JsBlob(r.Body, contentType) { Prototype = blobProto };
                return CreateResolvedPromise(engine, blob);
            }));
        }
    }

    internal static JsObject InstallBlobPrototype(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("text", new JsFunction("text", (thisVal, args) =>
        {
            var b = RequireBlob(thisVal, "Blob.prototype.text");
            return CreateResolvedPromise(engine, Encoding.UTF8.GetString(b.Data));
        }));

        proto.SetNonEnumerable("arrayBuffer", new JsFunction("arrayBuffer", (thisVal, args) =>
        {
            var b = RequireBlob(thisVal, "Blob.prototype.arrayBuffer");
            var buf = new JsArrayBuffer(b.Data.Length);
            Array.Copy(b.Data, buf.Data, b.Data.Length);
            return CreateResolvedPromise(engine, buf);
        }));

        proto.SetNonEnumerable("bytes", new JsFunction("bytes", (thisVal, args) =>
        {
            var b = RequireBlob(thisVal, "Blob.prototype.bytes");
            var buf = new JsArrayBuffer(b.Data.Length);
            Array.Copy(b.Data, buf.Data, b.Data.Length);
            var view = new JsTypedArray(TypedArrayKind.Uint8, buf, 0, b.Data.Length);
            // Pick up the Uint8Array prototype if it was already
            // installed (Builtins.Install order guarantees it).
            if (engine.Globals.TryGetValue("Uint8Array", out var ctor) &&
                ctor is JsFunction fn &&
                fn.Get("prototype") is JsObject uintProto)
            {
                view.Prototype = uintProto;
            }
            return CreateResolvedPromise(engine, view);
        }));

        proto.SetNonEnumerable("slice", new JsFunction("slice", (thisVal, args) =>
        {
            var b = RequireBlob(thisVal, "Blob.prototype.slice");
            int start = args.Count > 0 && args[0] is not JsUndefined
                ? (int)JsValue.ToNumber(args[0]) : 0;
            int end = args.Count > 1 && args[1] is not JsUndefined
                ? (int)JsValue.ToNumber(args[1]) : b.Data.Length;
            string type = args.Count > 2 && args[2] is not JsUndefined
                ? JsValue.ToJsString(args[2]) : "";

            // WHATWG Blob.slice: negative indices from end,
            // clamp to [0, size], start > end → empty.
            int size = b.Data.Length;
            if (start < 0) start = Math.Max(0, size + start);
            else start = Math.Min(start, size);
            if (end < 0) end = Math.Max(0, size + end);
            else end = Math.Min(end, size);
            int len = Math.Max(0, end - start);
            var sliced = new byte[len];
            Array.Copy(b.Data, start, sliced, 0, len);
            return new JsBlob(sliced, type) { Prototype = proto };
        }));

        return proto;
    }

    private static void InstallBlobConstructor(JsEngine engine, JsObject blobProto)
    {
        var ctor = new JsFunction("Blob", (thisVal, args) =>
        {
            var bytes = CollectParts(args.Count > 0 ? args[0] : null);
            var type = ReadType(args.Count > 1 ? args[1] : null);
            return new JsBlob(bytes, type) { Prototype = blobProto };
        });
        ctor.SetNonEnumerable("prototype", blobProto);
        blobProto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Blob"] = ctor;
    }

    private static void InstallFileConstructor(
        JsEngine engine, JsObject fileProto, JsObject blobProto)
    {
        var ctor = new JsFunction("File", (thisVal, args) =>
        {
            if (args.Count < 2)
            {
                JsThrow.TypeError(
                    "File constructor requires at least 2 arguments (fileBits, fileName)");
            }
            var bytes = CollectParts(args[0]);
            var name = JsValue.ToJsString(args[1]);
            double lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string type = "";
            if (args.Count > 2 && args[2] is JsObject opts)
            {
                if (opts.Get("type") is string t) type = t;
                if (opts.Get("lastModified") is double lm) lastModified = lm;
            }
            return new JsFile(bytes, name, type, lastModified) { Prototype = fileProto };
        });
        ctor.SetNonEnumerable("prototype", fileProto);
        fileProto.SetNonEnumerable("constructor", ctor);
        engine.Globals["File"] = ctor;
    }

    /// <summary>Read the <c>type</c> option off a Blob/File options
    /// bag. Accepts <c>{ type: "text/plain" }</c>; any other shape
    /// gives an empty string (spec-shaped default).</summary>
    private static string ReadType(object? opts)
    {
        if (opts is JsObject obj && obj.Get("type") is string t) return t;
        return "";
    }

    /// <summary>Normalize a Blob/File constructor's <c>blobParts</c>
    /// array into a flat byte[] by concatenating each part.
    /// Strings UTF-8-encode; ArrayBuffers / TypedArrays / Blobs
    /// copy their bytes. Anything else stringifies via
    /// <c>JsValue.ToJsString</c> — matches browser-observable
    /// behavior.</summary>
    internal static byte[] CollectParts(object? parts)
    {
        if (parts is null || parts is JsUndefined || parts is JsNull)
        {
            return Array.Empty<byte>();
        }
        if (parts is not JsArray arr)
        {
            // Spec: "If blobParts is not an iterable, throw."
            // Accept a lone string/bytes as a one-part shortcut
            // since real-world callers pass e.g. `new Blob(["..."])`
            // but debugging mistakes pass `new Blob("...")`; keep
            // the forgiving path for now.
            return EncodePart(parts);
        }

        using var ms = new MemoryStream();
        foreach (var part in arr.Elements)
        {
            var chunk = EncodePart(part);
            ms.Write(chunk, 0, chunk.Length);
        }
        return ms.ToArray();
    }

    private static byte[] EncodePart(object? part)
    {
        switch (part)
        {
            case null:
            case JsNull:
            case JsUndefined:
                return Array.Empty<byte>();
            case string s:
                return Encoding.UTF8.GetBytes(s);
            case JsBlob b:
                // Clone so the source Blob remains immutable.
                var copy = new byte[b.Data.Length];
                Array.Copy(b.Data, copy, b.Data.Length);
                return copy;
            case JsArrayBuffer ab:
                var abCopy = new byte[ab.Data.Length];
                Array.Copy(ab.Data, abCopy, ab.Data.Length);
                return abCopy;
            case JsTypedArray ta:
                var taCopy = new byte[ta.ByteLength];
                Array.Copy(ta.Buffer.Data, ta.ByteOffset, taCopy, 0, ta.ByteLength);
                return taCopy;
            default:
                return Encoding.UTF8.GetBytes(JsValue.ToJsString(part));
        }
    }

    private static JsBlob RequireBlob(object? thisVal, string name)
    {
        if (thisVal is not JsBlob b)
        {
            JsThrow.TypeError($"{name} called on non-Blob");
        }
        return (JsBlob)thisVal!;
    }

    /// <summary>Wrap <paramref name="value"/> in an already-
    /// resolved promise. Mirrors the helper in BuiltinFetch
    /// so Blob methods stay consistent with Response methods.</summary>
    internal static JsPromise CreateResolvedPromise(JsEngine engine, object? value)
    {
        var p = new JsPromise(engine);
        p.Resolve(value);
        return p;
    }
}

/// <summary>
/// Instance state for a JS-visible Blob. Immutable byte container
/// plus a MIME type string; methods on the Blob prototype operate
/// against <see cref="Data"/>.
/// </summary>
public class JsBlob : JsObject
{
    /// <summary>Raw bytes. Treated as immutable by the Blob
    /// prototype methods; <c>slice</c> copies into a fresh
    /// buffer.</summary>
    public byte[] Data { get; }

    /// <summary>MIME type string (e.g. <c>"text/plain"</c>,
    /// <c>"image/png"</c>). Empty string when not specified.</summary>
    public string Type { get; }

    public JsBlob(byte[] data, string type)
    {
        Data = data ?? Array.Empty<byte>();
        Type = type ?? "";
    }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "size" => (double)Data.Length,
        "type" => Type,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override bool Has(string key) =>
        key == "size" || key == "type" || base.Has(key);
}

/// <summary>
/// A <see cref="JsBlob"/> with a filename and a last-modified
/// timestamp. What an <c>&lt;input type=file&gt;</c> produces
/// in a real browser; matching constructor shape lets scripts
/// round-trip these through <c>FormData</c> / <c>fetch</c>.
/// </summary>
public sealed class JsFile : JsBlob
{
    public string Name { get; }
    public double LastModified { get; }

    public JsFile(byte[] data, string name, string type, double lastModified)
        : base(data, type)
    {
        Name = name ?? "";
        LastModified = lastModified;
    }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "name" => Name,
        "lastModified" => LastModified,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override bool Has(string key) =>
        key == "name" || key == "lastModified" || base.Has(key);
}
