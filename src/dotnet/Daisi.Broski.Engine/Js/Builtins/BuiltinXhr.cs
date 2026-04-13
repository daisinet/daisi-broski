using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>XMLHttpRequest</c> — the legacy "do an HTTP request" API.
/// Phase-3 shipped a stub that accepted <c>open</c> /
/// <c>send</c> as no-ops just to keep feature-detection from
/// crashing. Phase-5c upgrades it to a real implementation
/// routed through the engine's
/// <see cref="JsEngine.FetchHandler"/>, the same path
/// <c>fetch()</c> uses — so XHR and fetch share cookie state,
/// redirect rules, and any test-installed stub handler.
///
/// <para>
/// The flow matches the spec's readyState progression:
/// <c>0</c> UNSENT → <c>open()</c> → <c>1</c> OPENED →
/// <c>send()</c> → (work) → <c>2</c> HEADERS_RECEIVED →
/// <c>4</c> DONE. We collapse <c>3</c> LOADING into the same
/// turn as DONE because the underlying fetch handler returns
/// the full body in one shot — there's no incremental body
/// for an XHR consumer to observe between LOADING and DONE.
/// On every state change <c>readystatechange</c> fires; on
/// completion <c>load</c> + <c>loadend</c> fire (or
/// <c>error</c> + <c>loadend</c> on transport failure).
/// </para>
///
/// <para>
/// Async vs. sync mode: the <c>async</c> arg to <c>open</c>
/// defaults to <c>true</c>. When true, we schedule the work
/// as a microtask so the calling script can install
/// handlers before the response shows up. When false, we run
/// the request inline — the script blocks. Both paths share
/// the same fetch handler, so behavior is consistent across
/// the two scheduling modes.
/// </para>
/// </summary>
internal static class BuiltinXhr
{
    private const double Unsent = JsXhr.Unsent;
    private const double Opened = JsXhr.Opened;
    private const double HeadersReceived = JsXhr.HeadersReceived;
    private const double Loading = JsXhr.Loading;
    private const double Done = JsXhr.Done;

    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        // readyState constants on the prototype so script can
        // read them off an instance (`xhr.DONE` etc.).
        proto.Set("UNSENT", Unsent);
        proto.Set("OPENED", Opened);
        proto.Set("HEADERS_RECEIVED", HeadersReceived);
        proto.Set("LOADING", Loading);
        proto.Set("DONE", Done);

        proto.SetNonEnumerable("open", new JsFunction("open", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.open");
            if (args.Count < 2)
            {
                JsThrow.TypeError(
                    "XMLHttpRequest.open requires at least 2 arguments (method, url)");
            }
            var method = JsValue.ToJsString(args[0]).ToUpperInvariant();
            var url = JsValue.ToJsString(args[1]);
            bool isAsync = args.Count < 3 || JsValue.ToBoolean(args[2]);
            x.Open(method, url, isAsync);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("setRequestHeader", new JsFunction("setRequestHeader", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.setRequestHeader");
            if (args.Count < 2) return JsValue.Undefined;
            x.SetRequestHeader(
                JsValue.ToJsString(args[0]),
                JsValue.ToJsString(args[1]));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("send", new JsFunction("send", (vm, thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.send");
            byte[]? body = null;
            string? bodyContentType = null;
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                (body, bodyContentType) = CoerceBody(args[0]);
            }
            x.Send(vm, body, bodyContentType);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("abort", new JsFunction("abort", (vm, thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.abort");
            x.Abort(vm);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("getResponseHeader", new JsFunction("getResponseHeader", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.getResponseHeader");
            if (args.Count == 0) return JsValue.Null;
            return x.GetResponseHeader(JsValue.ToJsString(args[0]));
        }));

        proto.SetNonEnumerable("getAllResponseHeaders", new JsFunction("getAllResponseHeaders", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.getAllResponseHeaders");
            return x.GetAllResponseHeaders();
        }));

        proto.SetNonEnumerable("overrideMimeType", new JsFunction("overrideMimeType", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.overrideMimeType");
            if (args.Count > 0) x.OverrideMime = JsValue.ToJsString(args[0]);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.addEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            x.AddListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (thisVal, args) =>
        {
            var x = RequireXhr(thisVal, "XMLHttpRequest.prototype.removeEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            x.RemoveListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        var ctor = new JsFunction("XMLHttpRequest", (thisVal, args) =>
        {
            return new JsXhr(engine) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        ctor.Set("UNSENT", Unsent);
        ctor.Set("OPENED", Opened);
        ctor.Set("HEADERS_RECEIVED", HeadersReceived);
        ctor.Set("LOADING", Loading);
        ctor.Set("DONE", Done);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["XMLHttpRequest"] = ctor;
    }

    private static JsXhr RequireXhr(object? thisVal, string name)
    {
        if (thisVal is not JsXhr x)
        {
            JsThrow.TypeError($"{name} called on non-XMLHttpRequest");
        }
        return (JsXhr)thisVal!;
    }

    /// <summary>Convert a script-supplied body into bytes + a
    /// guessed content-type. Mirrors fetch's CoerceBody so the
    /// two paths produce identical wire output for the same JS
    /// inputs.</summary>
    private static (byte[] bytes, string? contentType) CoerceBody(object? body)
    {
        switch (body)
        {
            case string s:
                return (Encoding.UTF8.GetBytes(s), "text/plain;charset=UTF-8");
            case JsBlob b:
                var blobCopy = new byte[b.Data.Length];
                Array.Copy(b.Data, blobCopy, b.Data.Length);
                return (blobCopy, string.IsNullOrEmpty(b.Type) ? null : b.Type);
            case JsArrayBuffer ab:
                var abCopy = new byte[ab.Data.Length];
                Array.Copy(ab.Data, abCopy, ab.Data.Length);
                return (abCopy, null);
            case JsTypedArray ta:
                var taCopy = new byte[ta.ByteLength];
                Array.Copy(ta.Buffer.Data, ta.ByteOffset, taCopy, 0, ta.ByteLength);
                return (taCopy, null);
            case JsFormData fd:
                // Serialize multipart/form-data with a generated
                // boundary. Real browsers do the same — XHR.send
                // with FormData picks a fresh boundary each call.
                return EncodeMultipart(fd);
            default:
                return (Encoding.UTF8.GetBytes(JsValue.ToJsString(body)), "text/plain;charset=UTF-8");
        }
    }

    private static (byte[] bytes, string contentType) EncodeMultipart(JsFormData form)
    {
        var boundary = "----DaisiBroskiBoundary" + Guid.NewGuid().ToString("N");
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false)) { NewLine = "\r\n" };

        foreach (var (name, value) in form.Entries())
        {
            writer.WriteLine("--" + boundary);
            if (value is JsFile file)
            {
                writer.WriteLine($"Content-Disposition: form-data; name=\"{name}\"; filename=\"{file.Name}\"");
                writer.WriteLine($"Content-Type: {(string.IsNullOrEmpty(file.Type) ? "application/octet-stream" : file.Type)}");
                writer.WriteLine();
                writer.Flush();
                ms.Write(file.Data, 0, file.Data.Length);
                writer.WriteLine();
            }
            else if (value is JsBlob blob)
            {
                writer.WriteLine($"Content-Disposition: form-data; name=\"{name}\"; filename=\"blob\"");
                writer.WriteLine($"Content-Type: {(string.IsNullOrEmpty(blob.Type) ? "application/octet-stream" : blob.Type)}");
                writer.WriteLine();
                writer.Flush();
                ms.Write(blob.Data, 0, blob.Data.Length);
                writer.WriteLine();
            }
            else
            {
                writer.WriteLine($"Content-Disposition: form-data; name=\"{name}\"");
                writer.WriteLine();
                writer.WriteLine(value?.ToString() ?? "");
            }
        }
        writer.WriteLine("--" + boundary + "--");
        writer.Flush();
        return (ms.ToArray(), "multipart/form-data; boundary=" + boundary);
    }
}

/// <summary>
/// Instance state for an <c>XMLHttpRequest</c>. Holds the
/// spec-visible <c>readyState</c>, <c>status</c>,
/// <c>responseText</c>, and friends; routes the actual
/// request through the engine's
/// <see cref="JsEngine.FetchHandler"/>.
/// </summary>
public sealed class JsXhr : JsObject
{
    // Spec readyState constants — exposed as `XMLHttpRequest.DONE`
    // etc. on both the constructor and the prototype, and used
    // internally by the dispatch state machine.
    public const double Unsent = 0.0;
    public const double Opened = 1.0;
    public const double HeadersReceived = 2.0;
    public const double Loading = 3.0;
    public const double Done = 4.0;

    private readonly JsEngine _engine;
    private readonly Dictionary<string, List<JsFunction>> _listeners = new();
    private readonly JsHeaders _requestHeaders = new();
    private string _method = "GET";
    private string _url = "";
    private bool _async = true;
    private double _readyState = 0.0;
    private double _status = 0.0;
    private string _statusText = "";
    private string _responseUrl = "";
    private string _responseText = "";
    private byte[] _responseBytes = Array.Empty<byte>();
    private JsHeaders _responseHeaders = new();
    private string _responseType = "";
    private object? _error = JsValue.Null;
    private int _generation;
    private bool _aborted;

    /// <summary>Optional MIME type override set by
    /// <c>overrideMimeType</c>. Currently unused — kept for
    /// future Content-Type sniffing for binary responses.</summary>
    public string? OverrideMime { get; set; }

    public JsXhr(JsEngine engine) { _engine = engine; }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "readyState" => _readyState,
        "status" => _status,
        "statusText" => _statusText,
        "responseURL" => _responseUrl,
        "responseText" => _responseText,
        "responseType" => _responseType,
        "response" => GetResponse(),
        "withCredentials" => false,
        "timeout" => 0.0,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override bool Has(string key) =>
        key is "readyState" or "status" or "statusText" or "responseURL"
            or "responseText" or "responseType" or "response"
            or "withCredentials" or "timeout"
        || base.Has(key);

    public override void Set(string key, object? value)
    {
        switch (key)
        {
            case "responseType":
                _responseType = JsValue.ToJsString(value);
                return;
            case "withCredentials":
            case "timeout":
                // Accepted for compatibility but not honored —
                // our fetch handler doesn't expose a timeout
                // hook and the cookie jar is shared regardless.
                return;
        }
        base.Set(key, value);
    }

    public void Open(string method, string url, bool isAsync)
    {
        _method = method;
        _url = url;
        _async = isAsync;
        _readyState = 1.0;
        _status = 0.0;
        _statusText = "";
        _responseText = "";
        _responseBytes = Array.Empty<byte>();
        _responseHeaders = new JsHeaders();
        _error = JsValue.Null;
        _aborted = false;
        // open() does not fire readystatechange until send()
        // is also called per spec? Actually it does: state
        // transitions from UNSENT (0) to OPENED (1) trigger
        // readystatechange. But because no script can have
        // installed a handler before construction, the dispatch
        // is observably a no-op here — we still fire it for
        // listeners attached via addEventListener after the
        // open call.
        // Defer the fire to send() so the spec-required ordering
        // (which also fires loadstart + readystatechange in send)
        // doesn't double up readystatechange-1.
    }

    public void SetRequestHeader(string name, string value)
    {
        if (_readyState != 1.0)
        {
            JsThrow.Raise(MakeError("InvalidStateError",
                "setRequestHeader: must be called after open() and before send()"));
        }
        _requestHeaders.Append(name, value);
    }

    public void Send(JsVM vm, byte[]? body, string? bodyContentType)
    {
        if (_readyState != 1.0)
        {
            JsThrow.Raise(MakeError("InvalidStateError",
                "send: must be called after open()"));
        }
        if (bodyContentType is not null && _requestHeaders.GetHeader("content-type") is null)
        {
            _requestHeaders.Append("Content-Type", bodyContentType);
        }

        int gen = ++_generation;
        FireStateChange(vm); // fire readystatechange for OPENED
        Fire(vm, "loadstart");

        Action work = () =>
        {
            if (gen != _generation || _aborted) return;
            try
            {
                if (_engine.FetchHandler is null)
                {
                    DeliverError(vm, "no FetchHandler is installed on the engine");
                    return;
                }

                var req = new JsFetchRequest
                {
                    Url = _url,
                    Method = _method,
                    Headers = _requestHeaders,
                    Body = body ?? Array.Empty<byte>(),
                };
                JsFetchResponse resp;
                try
                {
                    resp = _engine.FetchHandler(req);
                }
                catch (Exception ex)
                {
                    DeliverError(vm, ex.Message);
                    return;
                }
                if (gen != _generation || _aborted) return;

                _status = resp.Status;
                _statusText = resp.StatusText ?? "";
                _responseUrl = resp.Url ?? _url;
                _responseHeaders = resp.Headers ?? new JsHeaders();
                _responseBytes = resp.Body ?? Array.Empty<byte>();
                _responseText = Encoding.UTF8.GetString(_responseBytes);

                _readyState = HeadersReceived;
                FireStateChange(vm);
                _readyState = Loading;
                FireStateChange(vm);
                _readyState = Done;
                FireStateChange(vm);
                Fire(vm, "load");
                Fire(vm, "loadend");
            }
            catch (Exception ex)
            {
                DeliverError(vm, ex.Message);
            }
        };

        if (_async)
        {
            _engine.EventLoop.QueueMicrotask(work);
        }
        else
        {
            // Synchronous mode: run the work immediately on
            // this thread. The script call to send() blocks
            // until the response is in.
            work();
        }
    }

    public void Abort(JsVM vm)
    {
        if (_readyState == Unsent || _readyState == Done)
        {
            // No-op; spec says abort on UNSENT / DONE only
            // resets state, doesn't fire events.
            _readyState = Unsent;
            return;
        }
        _aborted = true;
        _generation++;
        _readyState = Done;
        _status = 0;
        _statusText = "";
        FireStateChange(vm);
        Fire(vm, "abort");
        Fire(vm, "loadend");
    }

    public object? GetResponseHeader(string name)
    {
        var v = _responseHeaders.GetHeader(name);
        return (object?)v ?? JsValue.Null;
    }

    public string GetAllResponseHeaders()
    {
        var sb = new StringBuilder();
        foreach (var kv in _responseHeaders.Snapshot())
        {
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }
        return sb.ToString();
    }

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

    private void DeliverError(JsVM vm, string message)
    {
        _readyState = Done;
        _status = 0;
        _statusText = "";
        _error = MakeError("NetworkError", message);
        FireStateChange(vm);
        Fire(vm, "error");
        Fire(vm, "loadend");
    }

    private void FireStateChange(JsVM vm) => Fire(vm, "readystatechange");

    private void Fire(JsVM vm, string type)
    {
        var evt = new JsObject { Prototype = _engine.ObjectPrototype };
        evt.Set("type", type);
        evt.Set("target", this);
        evt.Set("currentTarget", this);
        evt.Set("bubbles", false);

        if (base.Get("on" + type) is JsFunction onCb)
        {
            vm.InvokeJsFunction(onCb, this, new object?[] { evt });
        }
        if (_listeners.TryGetValue(type, out var list))
        {
            foreach (var cb in list.ToArray())
            {
                vm.InvokeJsFunction(cb, this, new object?[] { evt });
            }
        }
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        return err;
    }

    /// <summary>Compute the <c>response</c> property based on
    /// <c>responseType</c>: empty / "text" → string, "json" →
    /// parsed JSON, "arraybuffer" → ArrayBuffer, "blob" → Blob.
    /// Other types fall through to the raw text — matches the
    /// majority of real-world XHR consumers that only set the
    /// type for the first three.</summary>
    private object? GetResponse()
    {
        switch (_responseType)
        {
            case "":
            case "text":
                return _responseText;
            case "json":
                if (_responseBytes.Length == 0) return JsValue.Null;
                try
                {
                    return BuiltinJson.ParseText(_engine, _responseText);
                }
                catch
                {
                    return JsValue.Null;
                }
            case "arraybuffer":
                var ab = new JsArrayBuffer(_responseBytes.Length);
                Array.Copy(_responseBytes, ab.Data, _responseBytes.Length);
                return ab;
            case "blob":
                if (_engine.Globals.TryGetValue("Blob", out var ctor) &&
                    ctor is JsFunction fn &&
                    fn.Get("prototype") is JsObject blobProto)
                {
                    var contentType = _responseHeaders.GetHeader("content-type") ?? "";
                    return new JsBlob(_responseBytes, contentType)
                    { Prototype = blobProto };
                }
                return JsValue.Null;
            default:
                return _responseText;
        }
    }
}
