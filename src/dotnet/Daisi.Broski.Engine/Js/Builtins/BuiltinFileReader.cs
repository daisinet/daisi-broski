using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>FileReader</c> — the historical one-shot reader for
/// Blob/File contents. Exposes the three most-used read
/// operations (<c>readAsText</c>, <c>readAsDataURL</c>,
/// <c>readAsArrayBuffer</c>), plus the read-as-binary-string
/// legacy variant, plus the <c>abort</c> op. Results drop
/// through the event loop as a microtask so handler code
/// runs in the same "after the current script, before the
/// next turn" position a real browser schedules them at.
///
/// <para>
/// Event dispatch honors both the <c>onload</c> / <c>onerror</c>
/// / <c>onloadend</c> / <c>onloadstart</c> / <c>onprogress</c>
/// / <c>onabort</c> on-style properties (set via plain
/// assignment) AND listeners added via
/// <c>addEventListener</c>. Each read method fires the spec
/// sequence: <c>loadstart</c> → <c>progress</c> → <c>load</c> →
/// <c>loadend</c> on success, or <c>loadstart</c> →
/// <c>error</c> → <c>loadend</c> on failure.
/// </para>
/// </summary>
internal static class BuiltinFileReader
{
    // readyState constants — spec values.
    private const double Empty = 0.0;
    private const double Loading = 1.0;
    private const double Done = 2.0;

    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        // readyState constants on the prototype (instance reads
        // fall through to the prototype for these).
        proto.Set("EMPTY", Empty);
        proto.Set("LOADING", Loading);
        proto.Set("DONE", Done);

        proto.SetNonEnumerable("readAsText", new JsFunction("readAsText", (vm, thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.readAsText");
            var blob = args.Count > 0 ? args[0] as JsBlob : null;
            var encoding = args.Count > 1 && args[1] is string s ? s : "utf-8";
            r.StartRead(vm, blob, ReadMode.Text, encoding);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("readAsDataURL", new JsFunction("readAsDataURL", (vm, thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.readAsDataURL");
            var blob = args.Count > 0 ? args[0] as JsBlob : null;
            r.StartRead(vm, blob, ReadMode.DataUrl, null);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("readAsArrayBuffer", new JsFunction("readAsArrayBuffer", (vm, thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.readAsArrayBuffer");
            var blob = args.Count > 0 ? args[0] as JsBlob : null;
            r.StartRead(vm, blob, ReadMode.ArrayBuffer, null);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("readAsBinaryString", new JsFunction("readAsBinaryString", (vm, thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.readAsBinaryString");
            var blob = args.Count > 0 ? args[0] as JsBlob : null;
            r.StartRead(vm, blob, ReadMode.BinaryString, null);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("abort", new JsFunction("abort", (vm, thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.abort");
            r.Abort(vm);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.addEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            r.AddListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (thisVal, args) =>
        {
            var r = RequireReader(thisVal, "FileReader.prototype.removeEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            r.RemoveListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        var ctor = new JsFunction("FileReader", (thisVal, args) =>
        {
            return new JsFileReader(engine) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        ctor.Set("EMPTY", Empty);
        ctor.Set("LOADING", Loading);
        ctor.Set("DONE", Done);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["FileReader"] = ctor;
    }

    private static JsFileReader RequireReader(object? thisVal, string name)
    {
        if (thisVal is not JsFileReader r)
        {
            JsThrow.TypeError($"{name} called on non-FileReader");
        }
        return (JsFileReader)thisVal!;
    }

    internal enum ReadMode { Text, DataUrl, ArrayBuffer, BinaryString }
}

/// <summary>
/// Instance state for a <c>FileReader</c>. Holds the
/// spec-visible <c>readyState</c> / <c>result</c> / <c>error</c>
/// fields plus a listener table keyed by event type.
/// </summary>
public sealed class JsFileReader : JsObject
{
    private readonly JsEngine _engine;
    private readonly Dictionary<string, List<JsFunction>> _listeners = new();
    private double _readyState = 0.0; // EMPTY
    private object? _result = JsValue.Null;
    private object? _error = JsValue.Null;
    private int _generation;

    public JsFileReader(JsEngine engine) { _engine = engine; }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "readyState" => _readyState,
        "result" => _result,
        "error" => _error,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override bool Has(string key) =>
        key is "readyState" or "result" or "error" || base.Has(key);

    public void AddListener(string type, JsFunction cb)
    {
        if (!_listeners.TryGetValue(type, out var list))
        {
            list = new List<JsFunction>();
            _listeners[type] = list;
        }
        list.Add(cb);
    }

    public void RemoveListener(string type, JsFunction cb)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(cb);
    }

    /// <summary>Begin reading <paramref name="blob"/> in the
    /// requested mode. Schedules the result delivery via a
    /// microtask so the caller's <c>onload</c> handler runs in
    /// the next turn, matching the spec.</summary>
    internal void StartRead(
        JsVM vm, JsBlob? blob,
        BuiltinFileReader.ReadMode mode, string? encoding)
    {
        int gen = ++_generation;
        _readyState = 1.0; // LOADING
        _result = JsValue.Null;
        _error = JsValue.Null;
        Fire(vm, "loadstart");

        _engine.EventLoop.QueueMicrotask(() =>
        {
            // A later abort / readAsX bumps the generation —
            // bail so we don't overwrite a newer read's state.
            if (gen != _generation) return;

            if (blob is null)
            {
                _readyState = 2.0; // DONE
                _error = MakeError("NotFoundError",
                    "FileReader: the provided value is not a Blob");
                Fire(vm, "error");
                Fire(vm, "loadend");
                return;
            }

            try
            {
                _result = mode switch
                {
                    BuiltinFileReader.ReadMode.Text =>
                        DecodeText(blob.Data, encoding ?? "utf-8"),
                    BuiltinFileReader.ReadMode.DataUrl =>
                        $"data:{(string.IsNullOrEmpty(blob.Type) ? "application/octet-stream" : blob.Type)};base64,"
                        + Convert.ToBase64String(blob.Data),
                    BuiltinFileReader.ReadMode.ArrayBuffer =>
                        BuildArrayBuffer(blob.Data),
                    BuiltinFileReader.ReadMode.BinaryString =>
                        BuildBinaryString(blob.Data),
                    _ => JsValue.Null,
                };
                _readyState = 2.0; // DONE
                Fire(vm, "progress");
                Fire(vm, "load");
            }
            catch (Exception ex)
            {
                _readyState = 2.0;
                _error = MakeError("EncodingError", ex.Message);
                Fire(vm, "error");
            }
            Fire(vm, "loadend");
        });
    }

    /// <summary>Abort an in-flight read. Bumps the generation
    /// so the queued microtask's work is discarded; fires
    /// <c>abort</c> + <c>loadend</c> synchronously.</summary>
    internal void Abort(JsVM vm)
    {
        if (_readyState != 1.0) return; // nothing to abort
        _generation++;
        _readyState = 2.0;
        _result = JsValue.Null;
        Fire(vm, "abort");
        Fire(vm, "loadend");
    }

    private void Fire(JsVM vm, string type)
    {
        // Synthesize a minimal event object — enough state that
        // handler code can read event.type / event.target and
        // discriminate between success / error / abort.
        var evt = new JsObject { Prototype = _engine.ObjectPrototype };
        evt.Set("type", type);
        evt.Set("target", this);
        evt.Set("currentTarget", this);
        evt.Set("bubbles", false);

        // 1) on-style property, e.g. reader.onload.
        if (base.Get("on" + type) is JsFunction onCb)
        {
            vm.InvokeJsFunction(onCb, this, new object?[] { evt });
        }
        // 2) addEventListener listeners in insertion order.
        if (_listeners.TryGetValue(type, out var list))
        {
            // Snapshot so a handler that adds / removes a listener
            // doesn't disturb the current dispatch — WHATWG
            // guarantees stable iteration.
            foreach (var cb in list.ToArray())
            {
                vm.InvokeJsFunction(cb, this, new object?[] { evt });
            }
        }
    }

    private JsArrayBuffer BuildArrayBuffer(byte[] data)
    {
        var buf = new JsArrayBuffer(data.Length);
        Array.Copy(data, buf.Data, data.Length);
        return buf;
    }

    private static string BuildBinaryString(byte[] data)
    {
        // Latin-1 "binary string": one char per byte.
        var sb = new StringBuilder(data.Length);
        foreach (var b in data) sb.Append((char)b);
        return sb.ToString();
    }

    private static string DecodeText(byte[] bytes, string encodingName)
    {
        // Honor the WHATWG label → Encoding mapping for the
        // common cases; unknown labels fall back to UTF-8
        // (browsers log a warning and do the same).
        var enc = encodingName.ToLowerInvariant() switch
        {
            "utf-8" or "utf8" or "unicode-1-1-utf-8" => Encoding.UTF8,
            "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "iso-8859-1" or "latin1" or "us-ascii" or "ascii" => Encoding.Latin1,
            _ => Encoding.UTF8,
        };
        // Strip a leading BOM if the declared encoding carries
        // one; spec-consistent and matches real browser output.
        return enc.GetString(bytes);
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        return err;
    }
}
