using System.Text;
using Daisi.Broski.Engine.Net;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>fetch</c> + <c>Headers</c> + <c>Request</c> + <c>Response</c>
/// — the WHATWG Fetch API subset a typical script needs for
/// API calls. Built on top of slice 3c-3's <c>URL</c>,
/// <c>TextEncoder</c>, and <c>atob</c>/<c>btoa</c>.
///
/// <para>
/// The network layer is pluggable via
/// <see cref="JsEngine.FetchHandler"/> — tests install a
/// stub handler that returns canned responses without
/// touching the network, and production code lets the
/// default handler delegate to <see cref="HttpFetcher"/>
/// so real HTTPS requests run through the same phase-1
/// network stack the CLI and sandbox host already use.
/// </para>
///
/// <para>
/// Deferrals (documented in the slice commit):
/// - The HTTP call runs synchronously within
///   <c>fetch(...)</c>, so the VM blocks on I/O.
///   The returned promise still drops through the
///   microtask queue, so <c>.then</c> handlers fire in
///   spec order — this matters for tests that await
///   multiple fetches in sequence. Real async scheduling
///   is a follow-up.
/// - No streaming body. <c>response.body</c> is not a
///   <c>ReadableStream</c>; <c>text()</c> / <c>json()</c>
///   / <c>arrayBuffer()</c> simply return the already-read
///   byte buffer converted the appropriate way.
/// - No <c>AbortController</c> — slice returns quickly
///   enough that cancellation hasn't been worth the
///   complexity yet.
/// - No <c>credentials</c> / <c>mode</c> / <c>cache</c> /
///   <c>referrerPolicy</c> options; the default handler
///   always sends cookies from the attached jar and
///   honors the engine's redirect cap.
/// </para>
/// </summary>
internal static class BuiltinFetch
{
    public static void Install(JsEngine engine)
    {
        var headersProto = InstallHeaders(engine);
        var responseProto = InstallResponse(engine, headersProto);
        var requestProto = InstallRequest(engine, headersProto);
        InstallFetchGlobal(engine, headersProto, responseProto);
    }

    // =======================================================
    // Headers
    // =======================================================

    private static JsObject InstallHeaders(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.get");
            if (args.Count == 0) return JsValue.Null;
            var name = JsValue.ToJsString(args[0]);
            return h.GetHeader(name) is string s ? s : (object)JsValue.Null;
        }));
        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.has");
            if (args.Count == 0) return false;
            return h.HasHeader(JsValue.ToJsString(args[0]));
        }));
        proto.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.set");
            if (args.Count < 2) return JsValue.Undefined;
            h.Set(JsValue.ToJsString(args[0]), JsValue.ToJsString(args[1]));
            return JsValue.Undefined;
        }));
        proto.SetNonEnumerable("append", new JsFunction("append", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.append");
            if (args.Count < 2) return JsValue.Undefined;
            h.Append(JsValue.ToJsString(args[0]), JsValue.ToJsString(args[1]));
            return JsValue.Undefined;
        }));
        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.delete");
            if (args.Count == 0) return JsValue.Undefined;
            h.DeleteHeader(JsValue.ToJsString(args[0]));
            return JsValue.Undefined;
        }));
        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction cb) return JsValue.Undefined;
            foreach (var kv in h.Snapshot())
            {
                vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { kv.Value, kv.Key, h });
            }
            return JsValue.Undefined;
        }));
        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.entries");
            return CreateHeadersIterator(engine, h, HeadersIterKind.Entries);
        }));
        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.keys");
            return CreateHeadersIterator(engine, h, HeadersIterKind.Keys);
        }));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype.values");
            return CreateHeadersIterator(engine, h, HeadersIterKind.Values);
        }));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (thisVal, args) =>
        {
            var h = RequireHeaders(thisVal, "Headers.prototype[Symbol.iterator]");
            return CreateHeadersIterator(engine, h, HeadersIterKind.Entries);
        }));

        var ctor = new JsFunction("Headers", (thisVal, args) =>
        {
            var h = new JsHeaders { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                PopulateHeaders(h, args[0]);
            }
            return h;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Headers"] = ctor;
        return proto;
    }

    private static JsHeaders RequireHeaders(object? thisVal, string name)
    {
        if (thisVal is not JsHeaders h)
        {
            JsThrow.TypeError($"{name} called on non-Headers");
        }
        return (JsHeaders)thisVal!;
    }

    private static void PopulateHeaders(JsHeaders target, object? init)
    {
        if (init is JsArray arr)
        {
            // [[name, value], [name, value], ...]
            foreach (var entry in arr.Elements)
            {
                if (entry is JsArray pair && pair.Elements.Count >= 2)
                {
                    target.Append(
                        JsValue.ToJsString(pair.Elements[0]),
                        JsValue.ToJsString(pair.Elements[1]));
                }
            }
            return;
        }
        if (init is JsHeaders other)
        {
            foreach (var kv in other.Snapshot())
            {
                target.Append(kv.Key, kv.Value);
            }
            return;
        }
        if (init is JsObject obj)
        {
            foreach (var key in obj.OwnKeys())
            {
                target.Append(key, JsValue.ToJsString(obj.Get(key)));
            }
        }
    }

    private enum HeadersIterKind { Keys, Values, Entries }

    private static JsObject CreateHeadersIterator(JsEngine engine, JsHeaders h, HeadersIterKind kind)
    {
        var snapshot = h.Snapshot();
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (index >= snapshot.Count)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            var cur = snapshot[index++];
            object? value = kind switch
            {
                HeadersIterKind.Keys => cur.Key,
                HeadersIterKind.Values => cur.Value,
                _ => HeaderPairAsArray(engine, cur),
            };
            result.Set("value", value);
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static JsArray HeaderPairAsArray(JsEngine engine, KeyValuePair<string, string> pair)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        arr.Elements.Add(pair.Key);
        arr.Elements.Add(pair.Value);
        return arr;
    }

    // =======================================================
    // Response
    // =======================================================

    private static JsObject InstallResponse(JsEngine engine, JsObject headersProto)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("text", new JsFunction("text", (thisVal, args) =>
        {
            var r = RequireResponse(thisVal, "Response.prototype.text");
            return CreateResolvedPromise(engine, Encoding.UTF8.GetString(r.Body));
        }));

        proto.SetNonEnumerable("json", new JsFunction("json", (vm, thisVal, args) =>
        {
            var r = RequireResponse(thisVal, "Response.prototype.json");
            try
            {
                var parsed = BuiltinJson.ParseText(engine, Encoding.UTF8.GetString(r.Body));
                return CreateResolvedPromise(engine, parsed);
            }
            catch (Exception ex)
            {
                var err = new JsObject { Prototype = engine.TypeErrorPrototype };
                err.Set("name", "TypeError");
                err.Set("message", $"Response.json: {ex.Message}");
                var rejected = new JsPromise(engine);
                rejected.Reject(err);
                return rejected;
            }
        }));

        proto.SetNonEnumerable("arrayBuffer", new JsFunction("arrayBuffer", (thisVal, args) =>
        {
            var r = RequireResponse(thisVal, "Response.prototype.arrayBuffer");
            var buf = new JsArrayBuffer(r.Body.Length);
            Array.Copy(r.Body, buf.Data, r.Body.Length);
            return CreateResolvedPromise(engine, buf);
        }));

        // A Response constructor is exposed so script code can
        // synthesize fake responses (common in testing shims).
        var ctor = new JsFunction("Response", (thisVal, args) =>
        {
            byte[] body = Array.Empty<byte>();
            if (args.Count > 0)
            {
                body = CoerceBody(args[0]);
            }
            var init = args.Count > 1 ? args[1] as JsObject : null;
            int status = 200;
            string statusText = "OK";
            var headers = new JsHeaders { Prototype = headersProto };
            if (init is not null)
            {
                if (init.Get("status") is double s) status = (int)s;
                if (init.Get("statusText") is string st) statusText = st;
                if (init.Get("headers") is { } h && h is not JsUndefined && h is not JsNull)
                {
                    PopulateHeaders(headers, h);
                }
            }
            return new JsFetchResponse
            {
                Prototype = proto,
                Status = status,
                StatusText = statusText,
                Url = "",
                Headers = headers,
                Body = body,
            };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Response"] = ctor;
        return proto;
    }

    private static JsFetchResponse RequireResponse(object? thisVal, string name)
    {
        if (thisVal is not JsFetchResponse r)
        {
            JsThrow.TypeError($"{name} called on non-Response");
        }
        return (JsFetchResponse)thisVal!;
    }

    private static byte[] CoerceBody(object? body)
    {
        if (body is null || body is JsUndefined || body is JsNull)
        {
            return Array.Empty<byte>();
        }
        if (body is string s) return Encoding.UTF8.GetBytes(s);
        if (body is JsArrayBuffer ab)
        {
            var copy = new byte[ab.Data.Length];
            Array.Copy(ab.Data, copy, ab.Data.Length);
            return copy;
        }
        if (body is JsTypedArray ta)
        {
            var copy = new byte[ta.ByteLength];
            Array.Copy(ta.Buffer.Data, ta.ByteOffset, copy, 0, ta.ByteLength);
            return copy;
        }
        // Everything else: coerce via ToJsString. Matches the
        // "stringifier" spec behavior for unknown bodies.
        return Encoding.UTF8.GetBytes(JsValue.ToJsString(body));
    }

    // =======================================================
    // Request
    // =======================================================

    private static JsObject InstallRequest(JsEngine engine, JsObject headersProto)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        // A Request is mostly a bag of properties; no
        // prototype methods in this slice. Future slices can
        // add clone() and the body consumers.

        var ctor = new JsFunction("Request", (thisVal, args) =>
        {
            if (args.Count == 0)
            {
                JsThrow.TypeError("Request constructor requires a URL or Request argument");
            }
            var r = new JsFetchRequest { Prototype = proto };
            r.Url = JsValue.ToJsString(args[0]);
            r.Method = "GET";
            r.Headers = new JsHeaders { Prototype = headersProto };
            if (args.Count > 1 && args[1] is JsObject init)
            {
                ApplyRequestInit(r, init, headersProto);
            }
            return r;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["Request"] = ctor;
        return proto;
    }

    private static void ApplyRequestInit(JsFetchRequest r, JsObject init, JsObject headersProto)
    {
        var method = init.Get("method");
        if (method is string m) r.Method = m.ToUpperInvariant();
        var headers = init.Get("headers");
        if (headers is not null && headers is not JsUndefined && headers is not JsNull)
        {
            if (headers is JsHeaders preset)
            {
                r.Headers = preset;
            }
            else
            {
                var h = new JsHeaders { Prototype = headersProto };
                PopulateHeaders(h, headers);
                r.Headers = h;
            }
        }
        var body = init.Get("body");
        if (body is not null && body is not JsUndefined && body is not JsNull)
        {
            r.Body = CoerceBody(body);
        }
    }

    // =======================================================
    // fetch global
    // =======================================================

    private static void InstallFetchGlobal(JsEngine engine, JsObject headersProto, JsObject responseProto)
    {
        engine.Globals["fetch"] = new JsFunction("fetch", (thisVal, args) =>
        {
            var promise = new JsPromise(engine);
            try
            {
                // Build a request descriptor from the
                // positional argument plus the optional init
                // dictionary. Accept either a URL string or
                // a Request instance as the first argument.
                JsFetchRequest req;
                if (args.Count == 0)
                {
                    JsThrow.TypeError("fetch: at least one argument required");
                    return null;
                }
                if (args[0] is JsFetchRequest existing)
                {
                    req = CloneRequest(existing, headersProto);
                }
                else
                {
                    req = new JsFetchRequest
                    {
                        Prototype = engine.Globals["Request"] is JsFunction rctor &&
                                    rctor.Get("prototype") is JsObject rp ? rp : null,
                        Url = JsValue.ToJsString(args[0]),
                        Method = "GET",
                        Headers = new JsHeaders { Prototype = headersProto },
                    };
                }
                if (args.Count > 1 && args[1] is JsObject init)
                {
                    ApplyRequestInit(req, init, headersProto);
                }

                // Dispatch through the engine's handler. The
                // default handler hits HttpFetcher; tests can
                // install a stub. A null handler rejects the
                // promise with a TypeError so the test writer
                // sees a clear message instead of an NRE.
                if (engine.FetchHandler is null)
                {
                    var err = new JsObject { Prototype = engine.TypeErrorPrototype };
                    err.Set("name", "TypeError");
                    err.Set("message", "fetch: no FetchHandler is installed on the engine");
                    promise.Reject(err);
                    return promise;
                }

                JsFetchResponse response;
                try
                {
                    response = engine.FetchHandler(req);
                }
                catch (Exception ex)
                {
                    var err = new JsObject { Prototype = engine.TypeErrorPrototype };
                    err.Set("name", "TypeError");
                    err.Set("message", $"fetch: {ex.Message}");
                    promise.Reject(err);
                    return promise;
                }

                response.Prototype = responseProto;
                promise.Resolve(response);
                return promise;
            }
            catch (JsThrowSignal)
            {
                throw;
            }
        });
    }

    private static JsFetchRequest CloneRequest(JsFetchRequest src, JsObject headersProto)
    {
        var headers = new JsHeaders { Prototype = headersProto };
        if (src.Headers is JsHeaders srcH)
        {
            foreach (var kv in srcH.Snapshot())
            {
                headers.Append(kv.Key, kv.Value);
            }
        }
        return new JsFetchRequest
        {
            Prototype = src.Prototype,
            Url = src.Url,
            Method = src.Method,
            Headers = headers,
            Body = src.Body,
        };
    }

    // =======================================================
    // Helpers
    // =======================================================

    private static JsPromise CreateResolvedPromise(JsEngine engine, object? value)
    {
        var p = new JsPromise(engine);
        p.Resolve(value);
        return p;
    }
}

/// <summary>
/// Delegate that runs a fetch request and returns the
/// response synchronously. Plugged into
/// <see cref="JsEngine.FetchHandler"/> so hosts can swap
/// between a real <see cref="HttpFetcher"/>-backed
/// implementation and a test stub.
/// </summary>
public delegate JsFetchResponse FetchHandler(JsFetchRequest request);

/// <summary>
/// Instance state for a JS <c>Request</c>. A plain bag of
/// properties exposed as a <see cref="JsObject"/> via
/// read-through getters.
/// </summary>
public sealed class JsFetchRequest : JsObject
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";
    public JsHeaders Headers { get; set; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "url" => Url,
        "method" => Method,
        "headers" => Headers,
        _ => base.Get(key),
    };
}

/// <summary>
/// Instance state for a JS <c>Response</c>. Carries the
/// response bytes in <see cref="Body"/>; the prototype
/// methods <c>text</c> / <c>json</c> / <c>arrayBuffer</c>
/// return fresh promises resolved with the appropriate
/// conversion.
/// </summary>
public sealed class JsFetchResponse : JsObject
{
    public int Status { get; set; } = 200;
    public string StatusText { get; set; } = "OK";
    public string Url { get; set; } = "";
    public JsHeaders Headers { get; set; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public bool Ok => Status >= 200 && Status < 300;

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "status" => (double)Status,
        "statusText" => StatusText,
        "ok" => Ok,
        "url" => Url,
        "headers" => Headers,
        "redirected" => false,
        "type" => "basic",
        _ => base.Get(key),
    };
}

/// <summary>
/// Instance state for a JS <c>Headers</c> collection.
/// Stores entries as an ordered list to preserve insertion
/// order (observable via <c>forEach</c> / iteration) plus a
/// case-insensitive lookup for the get / has / set hot
/// paths. Multi-valued headers are supported via
/// <see cref="Append"/>, which keeps every value; getters
/// return a comma-joined string per spec.
/// </summary>
public sealed class JsHeaders : JsObject
{
    // Ordered list of (normalized-name, value) pairs.
    // "Normalized" means lowercased because HTTP header
    // names are case-insensitive.
    private readonly List<KeyValuePair<string, string>> _entries = new();

    public int Count => _entries.Count;

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "size") return (double)_entries.Count;
        return base.Get(key);
    }

    /// <summary>
    /// Return every value registered under
    /// <paramref name="name"/> joined by <c>", "</c> per
    /// WHATWG Fetch spec. Returns null when the header is
    /// absent. Named <c>GetHeader</c> rather than <c>Get</c>
    /// to avoid collision with <see cref="JsObject.Get"/>
    /// — the script-visible <c>headers.get('name')</c>
    /// method delegates to this one via its own native
    /// impl above.
    /// </summary>
    public string? GetHeader(string name)
    {
        var key = name.ToLowerInvariant();
        StringBuilder? sb = null;
        foreach (var kv in _entries)
        {
            if (kv.Key == key)
            {
                if (sb is null) sb = new StringBuilder(kv.Value);
                else sb.Append(", ").Append(kv.Value);
            }
        }
        return sb?.ToString();
    }

    public bool HasHeader(string name)
    {
        var key = name.ToLowerInvariant();
        foreach (var kv in _entries)
        {
            if (kv.Key == key) return true;
        }
        return false;
    }

    /// <summary>
    /// Spec <c>set</c>: replaces every existing value for
    /// <paramref name="name"/> with a single fresh entry.
    /// If the name wasn't present, the new entry is
    /// appended to the end.
    /// </summary>
    public void Set(string name, string value)
    {
        var key = name.ToLowerInvariant();
        int firstIdx = -1;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Key == key)
            {
                firstIdx = i;
                _entries.RemoveAt(i);
            }
        }
        var entry = new KeyValuePair<string, string>(key, value);
        if (firstIdx < 0) _entries.Add(entry);
        else _entries.Insert(firstIdx, entry);
    }

    /// <summary>
    /// Spec <c>append</c>: adds a new entry even when a
    /// previous one with the same name exists. Used for
    /// multi-valued headers like <c>Set-Cookie</c>.
    /// </summary>
    public void Append(string name, string value)
    {
        _entries.Add(new KeyValuePair<string, string>(name.ToLowerInvariant(), value));
    }

    public void DeleteHeader(string name)
    {
        var key = name.ToLowerInvariant();
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Key == key) _entries.RemoveAt(i);
        }
    }

    /// <summary>
    /// Snapshot of the entries coalesced the way
    /// <c>forEach</c> and the iterator helpers see them:
    /// one entry per distinct header name with values
    /// comma-joined, sorted alphabetically. Matches WHATWG
    /// Fetch spec's "combined header list" algorithm.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        var grouped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in _entries)
        {
            if (!grouped.TryGetValue(kv.Key, out var list))
            {
                list = new List<string>();
                grouped[kv.Key] = list;
            }
            list.Add(kv.Value);
        }
        var result = new List<KeyValuePair<string, string>>(grouped.Count);
        foreach (var g in grouped)
        {
            result.Add(new KeyValuePair<string, string>(g.Key, string.Join(", ", g.Value)));
        }
        return result;
    }
}
