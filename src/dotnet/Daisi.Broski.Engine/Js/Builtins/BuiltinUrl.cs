using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>URL</c> and <c>URLSearchParams</c> built-ins — a minimal
/// subset of the WHATWG URL spec, enough for <c>fetch</c>-style
/// code that parses and manipulates request URLs.
///
/// <para>
/// Delegates the heavy lifting to <see cref="System.Uri"/>, which
/// handles scheme / authority / path parsing for HTTP/HTTPS URLs
/// — our primary use case. The script-visible API is
/// intentionally the common subset both browsers and Node ship:
/// <c>href</c>, <c>protocol</c>, <c>host</c>, <c>hostname</c>,
/// <c>port</c>, <c>pathname</c>, <c>search</c>, <c>hash</c>,
/// <c>origin</c>, <c>toString()</c>, plus a <c>searchParams</c>
/// getter.
/// </para>
///
/// <para>
/// Deferrals: relative URL resolution via the second constructor
/// argument is supported for HTTP/HTTPS and file URIs, but spec
/// edge cases around UTF-8 encoding of path segments, IDN
/// hostnames, and all of <c>URL.parse</c> / <c>URL.canParse</c>
/// / <c>URL.createObjectURL</c> are not implemented.
/// </para>
/// </summary>
internal static class BuiltinUrl
{
    public static void Install(JsEngine engine)
    {
        InstallURL(engine);
        InstallURLSearchParams(engine);
    }

    // =======================================================
    // URL
    // =======================================================

    private static void InstallURL(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        // Each accessor is installed as a real getter so `url.host` works
        // without methods. JsObject exposes this via SetAccessor.
        InstallUrlGetter(proto, "href", u => u.Href);
        InstallUrlGetter(proto, "origin", u => u.Origin);
        InstallUrlGetter(proto, "protocol", u => u.Protocol);
        InstallUrlGetter(proto, "host", u => u.Host);
        InstallUrlGetter(proto, "hostname", u => u.Hostname);
        InstallUrlGetter(proto, "port", u => u.Port);
        InstallUrlGetter(proto, "pathname", u => u.Pathname);
        InstallUrlGetter(proto, "search", u => u.Search);
        InstallUrlGetter(proto, "hash", u => u.Hash);

        // searchParams is lazily constructed on first read and
        // cached on the instance so repeated reads return the
        // same object — mutations to it flow back into the
        // URL's search string on every set.
        proto.SetAccessor("searchParams",
            new JsFunction("get searchParams", (thisVal, args) =>
            {
                var u = RequireUrl(thisVal, "URL.prototype.searchParams");
                return u.GetOrCreateSearchParams(engine);
            }),
            null);

        proto.SetNonEnumerable("toString", new JsFunction("toString", (thisVal, args) =>
        {
            var u = RequireUrl(thisVal, "URL.prototype.toString");
            return u.Href;
        }));

        proto.SetNonEnumerable("toJSON", new JsFunction("toJSON", (thisVal, args) =>
        {
            var u = RequireUrl(thisVal, "URL.prototype.toJSON");
            return u.Href;
        }));

        var ctor = new JsFunction("URL", (thisVal, args) =>
        {
            if (args.Count == 0)
            {
                JsThrow.TypeError("URL constructor requires at least one argument");
            }
            var input = JsValue.ToJsString(args[0]);
            string? baseStr = null;
            if (args.Count > 1 && args[1] is not JsUndefined && args[1] is not JsNull)
            {
                baseStr = JsValue.ToJsString(args[1]);
            }
            var u = new JsUrl { Prototype = proto };
            u.Parse(input, baseStr);
            return u;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["URL"] = ctor;
    }

    private static void InstallUrlGetter(JsObject proto, string name, Func<JsUrl, string> read)
    {
        proto.SetAccessor(name,
            new JsFunction($"get {name}", (thisVal, args) =>
            {
                var u = RequireUrl(thisVal, $"URL.prototype.{name}");
                return read(u);
            }),
            new JsFunction($"set {name}", (thisVal, args) =>
            {
                var u = RequireUrl(thisVal, $"URL.prototype.{name}");
                var value = args.Count > 0 ? JsValue.ToJsString(args[0]) : "";
                u.WriteProperty(name, value);
                return JsValue.Undefined;
            }));
    }

    private static JsUrl RequireUrl(object? thisVal, string name)
    {
        if (thisVal is not JsUrl u)
        {
            JsThrow.TypeError($"{name} called on non-URL");
        }
        return (JsUrl)thisVal!;
    }

    // =======================================================
    // URLSearchParams
    // =======================================================

    private static void InstallURLSearchParams(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.get");
            if (args.Count == 0) return JsValue.Null;
            var name = JsValue.ToJsString(args[0]);
            foreach (var pair in p.Pairs)
            {
                if (pair.Key == name) return pair.Value;
            }
            return JsValue.Null;
        }));

        proto.SetNonEnumerable("getAll", new JsFunction("getAll", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.getAll");
            var arr = new JsArray { Prototype = engine.ArrayPrototype };
            if (args.Count == 0) return arr;
            var name = JsValue.ToJsString(args[0]);
            foreach (var pair in p.Pairs)
            {
                if (pair.Key == name) arr.Elements.Add(pair.Value);
            }
            return arr;
        }));

        proto.SetNonEnumerable("has", new JsFunction("has", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.has");
            if (args.Count == 0) return false;
            var name = JsValue.ToJsString(args[0]);
            foreach (var pair in p.Pairs)
            {
                if (pair.Key == name) return true;
            }
            return false;
        }));

        proto.SetNonEnumerable("set", new JsFunction("set", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.set");
            if (args.Count < 2) return JsValue.Undefined;
            var name = JsValue.ToJsString(args[0]);
            var value = JsValue.ToJsString(args[1]);
            // Remove all existing, append one fresh entry at the
            // position of the first match (spec behavior).
            int firstIdx = -1;
            for (int i = p.Pairs.Count - 1; i >= 0; i--)
            {
                if (p.Pairs[i].Key == name)
                {
                    firstIdx = i;
                    p.Pairs.RemoveAt(i);
                }
            }
            if (firstIdx < 0) p.Pairs.Add(new KeyValuePair<string, string>(name, value));
            else p.Pairs.Insert(firstIdx, new KeyValuePair<string, string>(name, value));
            p.NotifyMutation();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("append", new JsFunction("append", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.append");
            if (args.Count < 2) return JsValue.Undefined;
            var name = JsValue.ToJsString(args[0]);
            var value = JsValue.ToJsString(args[1]);
            p.Pairs.Add(new KeyValuePair<string, string>(name, value));
            p.NotifyMutation();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.delete");
            if (args.Count == 0) return JsValue.Undefined;
            var name = JsValue.ToJsString(args[0]);
            for (int i = p.Pairs.Count - 1; i >= 0; i--)
            {
                if (p.Pairs[i].Key == name) p.Pairs.RemoveAt(i);
            }
            p.NotifyMutation();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("toString", new JsFunction("toString", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.toString");
            return p.Serialize();
        }));

        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction cb)
            {
                JsThrow.TypeError("URLSearchParams.prototype.forEach callback is not a function");
            }
            var fn = (JsFunction)args[0]!;
            var snapshot = new List<KeyValuePair<string, string>>(p.Pairs);
            foreach (var pair in snapshot)
            {
                vm.InvokeJsFunction(fn, JsValue.Undefined, new object?[] { pair.Value, pair.Key, p });
            }
            return JsValue.Undefined;
        }));

        // Iterator helpers: entries() + [Symbol.iterator].
        proto.SetNonEnumerable("entries", new JsFunction("entries", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.entries");
            return CreateParamsIterator(engine, p, ParamsIterKind.Entries);
        }));
        proto.SetNonEnumerable("keys", new JsFunction("keys", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.keys");
            return CreateParamsIterator(engine, p, ParamsIterKind.Keys);
        }));
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype.values");
            return CreateParamsIterator(engine, p, ParamsIterKind.Values);
        }));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (thisVal, args) =>
        {
            var p = RequireParams(thisVal, "URLSearchParams.prototype[Symbol.iterator]");
            return CreateParamsIterator(engine, p, ParamsIterKind.Entries);
        }));

        var ctor = new JsFunction("URLSearchParams", (thisVal, args) =>
        {
            var p = new JsUrlSearchParams { Prototype = proto };
            if (args.Count > 0 && args[0] is not JsUndefined && args[0] is not JsNull)
            {
                PopulateParams(p, args[0]);
            }
            return p;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["URLSearchParams"] = ctor;

        engine.UrlSearchParamsPrototype = proto;
    }

    private static JsUrlSearchParams RequireParams(object? thisVal, string name)
    {
        if (thisVal is not JsUrlSearchParams p)
        {
            JsThrow.TypeError($"{name} called on non-URLSearchParams");
        }
        return (JsUrlSearchParams)thisVal!;
    }

    private static void PopulateParams(JsUrlSearchParams target, object? source)
    {
        if (source is string s)
        {
            target.ParseQueryString(s);
            return;
        }
        if (source is JsArray arr)
        {
            foreach (var entry in arr.Elements)
            {
                if (entry is JsArray pair && pair.Elements.Count >= 2)
                {
                    target.Pairs.Add(new KeyValuePair<string, string>(
                        JsValue.ToJsString(pair.Elements[0]),
                        JsValue.ToJsString(pair.Elements[1])));
                }
            }
            return;
        }
        if (source is JsObject obj && source is not JsFunction)
        {
            foreach (var k in obj.OwnKeys())
            {
                target.Pairs.Add(new KeyValuePair<string, string>(
                    k, JsValue.ToJsString(obj.Get(k))));
            }
            return;
        }
        // Anything else — coerce to string and parse as query.
        target.ParseQueryString(JsValue.ToJsString(source));
    }

    private enum ParamsIterKind { Keys, Values, Entries }

    private static JsObject CreateParamsIterator(JsEngine engine, JsUrlSearchParams p, ParamsIterKind kind)
    {
        var snapshot = new List<KeyValuePair<string, string>>(p.Pairs);
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
                ParamsIterKind.Keys => cur.Key,
                ParamsIterKind.Values => cur.Value,
                _ => PairAsArray(engine, cur),
            };
            result.Set("value", value);
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static JsArray PairAsArray(JsEngine engine, KeyValuePair<string, string> pair)
    {
        var arr = new JsArray { Prototype = engine.ArrayPrototype };
        arr.Elements.Add(pair.Key);
        arr.Elements.Add(pair.Value);
        return arr;
    }
}

/// <summary>
/// Instance state for <c>URL</c>. Stores the parsed components
/// individually so read access is O(1), plus the original
/// <see cref="Href"/> so <c>toString</c> round-trips cleanly.
/// Mutations via <see cref="WriteProperty"/> rebuild
/// <see cref="Href"/> from components.
/// </summary>
public sealed class JsUrl : JsObject
{
    public string Href { get; private set; } = "";
    public string Protocol { get; private set; } = "";
    public string Host { get; private set; } = "";
    public string Hostname { get; private set; } = "";
    public string Port { get; private set; } = "";
    public string Pathname { get; private set; } = "/";
    public string Search { get; private set; } = "";
    public string Hash { get; private set; } = "";

    /// <summary>
    /// ES spec: origin is <c>{scheme}://{host}[:port]</c> for
    /// http/https/ws/wss/ftp URLs, <c>"null"</c> otherwise.
    /// </summary>
    public string Origin
    {
        get
        {
            if (Protocol is "http:" or "https:" or "ws:" or "wss:" or "ftp:")
            {
                return $"{Protocol}//{Host}";
            }
            return "null";
        }
    }

    private JsUrlSearchParams? _cachedSearchParams;

    public JsUrlSearchParams GetOrCreateSearchParams(JsEngine engine)
    {
        if (_cachedSearchParams is null)
        {
            _cachedSearchParams = new JsUrlSearchParams
            {
                Prototype = engine.UrlSearchParamsPrototype,
                Owner = this,
            };
            var query = Search.StartsWith('?') ? Search.Substring(1) : Search;
            _cachedSearchParams.ParseQueryString(query);
        }
        return _cachedSearchParams;
    }

    /// <summary>
    /// Parse <paramref name="input"/> (optionally resolving
    /// against <paramref name="baseUrl"/>) using
    /// <see cref="System.Uri"/>. On failure, throws a
    /// script-visible <c>TypeError</c>.
    /// </summary>
    public void Parse(string input, string? baseUrl)
    {
        Uri? uri;
        try
        {
            if (baseUrl is not null)
            {
                var b = new Uri(baseUrl, UriKind.Absolute);
                uri = new Uri(b, input);
            }
            else
            {
                uri = new Uri(input, UriKind.Absolute);
            }
        }
        catch (UriFormatException)
        {
            JsThrow.TypeError($"Invalid URL: '{input}'");
            return;
        }
        catch (ArgumentException)
        {
            JsThrow.TypeError($"Invalid URL: '{input}'");
            return;
        }

        Protocol = uri.Scheme + ":";
        Hostname = uri.Host;
        // .NET returns -1 when the URL uses the scheme's
        // default port; the spec wants the empty string.
        Port = uri.IsDefaultPort || uri.Port < 0 ? "" : uri.Port.ToString();
        Host = Port.Length == 0 ? Hostname : $"{Hostname}:{Port}";
        Pathname = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        Search = uri.Query; // already includes the leading '?' if present
        Hash = uri.Fragment; // already includes the leading '#' if present
        Href = Rebuild();
    }

    /// <summary>
    /// Handle a write to one of the URL's mutable properties.
    /// Recomputes <see cref="Href"/> after each change.
    /// </summary>
    public void WriteProperty(string name, string value)
    {
        switch (name)
        {
            case "href":
                Parse(value, null);
                return;
            case "protocol":
                Protocol = value.EndsWith(':') ? value : value + ":";
                break;
            case "host":
                Host = value;
                var colon = value.IndexOf(':');
                if (colon >= 0)
                {
                    Hostname = value.Substring(0, colon);
                    Port = value.Substring(colon + 1);
                }
                else
                {
                    Hostname = value;
                    Port = "";
                }
                break;
            case "hostname":
                Hostname = value;
                Host = Port.Length == 0 ? Hostname : $"{Hostname}:{Port}";
                break;
            case "port":
                Port = value;
                Host = Port.Length == 0 ? Hostname : $"{Hostname}:{Port}";
                break;
            case "pathname":
                Pathname = value.StartsWith('/') ? value : "/" + value;
                break;
            case "search":
                Search = value.Length == 0
                    ? ""
                    : (value.StartsWith('?') ? value : "?" + value);
                // Invalidate the cached searchParams view so the
                // next read re-parses.
                _cachedSearchParams = null;
                break;
            case "hash":
                Hash = value.Length == 0
                    ? ""
                    : (value.StartsWith('#') ? value : "#" + value);
                break;
        }
        Href = Rebuild();
    }

    /// <summary>
    /// Called by <see cref="JsUrlSearchParams"/> when a mutation
    /// lands on a view that's bound to this URL. Regenerates
    /// <see cref="Search"/> and <see cref="Href"/> so they stay
    /// in sync with the params object.
    /// </summary>
    internal void OnSearchParamsMutated(JsUrlSearchParams view)
    {
        var serialized = view.Serialize();
        Search = serialized.Length == 0 ? "" : "?" + serialized;
        Href = Rebuild();
    }

    private string Rebuild()
    {
        var sb = new StringBuilder();
        sb.Append(Protocol);
        sb.Append("//");
        sb.Append(Host);
        sb.Append(Pathname);
        sb.Append(Search);
        sb.Append(Hash);
        return sb.ToString();
    }
}

/// <summary>
/// Instance state for <c>URLSearchParams</c>. An ordered
/// list of <c>(name, value)</c> pairs. When <see cref="Owner"/>
/// is non-null, this view is bound to a <see cref="JsUrl"/>
/// and every mutation propagates back to the owner's
/// <c>search</c> string — matching the live-binding spec
/// behavior where <c>url.searchParams.set('x', '1')</c>
/// immediately updates <c>url.href</c>.
/// </summary>
public sealed class JsUrlSearchParams : JsObject
{
    public List<KeyValuePair<string, string>> Pairs { get; } = new();

    /// <summary>
    /// The <see cref="JsUrl"/> this params view mutates when
    /// bound, or null when the params were created stand-alone.
    /// </summary>
    internal JsUrl? Owner { get; set; }

    public override object? Get(string key)
    {
        if (key == "size") return (double)Pairs.Count;
        return base.Get(key);
    }

    public override bool Has(string key)
    {
        if (key == "size") return true;
        return base.Has(key);
    }

    /// <summary>
    /// Parse an <c>application/x-www-form-urlencoded</c>
    /// body (minus the leading <c>?</c>) into the pairs list.
    /// </summary>
    public void ParseQueryString(string query)
    {
        Pairs.Clear();
        if (string.IsNullOrEmpty(query)) return;
        // Strip leading '?' if the caller forgot to.
        if (query[0] == '?') query = query.Substring(1);
        foreach (var segment in query.Split('&'))
        {
            if (segment.Length == 0) continue;
            var eq = segment.IndexOf('=');
            string name, value;
            if (eq < 0)
            {
                name = Decode(segment);
                value = "";
            }
            else
            {
                name = Decode(segment.Substring(0, eq));
                value = Decode(segment.Substring(eq + 1));
            }
            Pairs.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    /// <summary>
    /// Serialize the pairs to an
    /// <c>application/x-www-form-urlencoded</c> string (no
    /// leading <c>?</c>). <c>+</c> is used for the space
    /// character — matching form submission / URLSearchParams
    /// spec; <c>%20</c> would be accepted by any parser but
    /// the script-visible output is <c>+</c>.
    /// </summary>
    public string Serialize()
    {
        if (Pairs.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < Pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(Encode(Pairs[i].Key));
            sb.Append('=');
            sb.Append(Encode(Pairs[i].Value));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Notify the owning URL (if any) that the params have
    /// changed, so its href / search stay in sync.
    /// </summary>
    internal void NotifyMutation()
    {
        Owner?.OnSearchParamsMutated(this);
    }

    // application/x-www-form-urlencoded subset: unreserved
    // set plus * - . _ is left alone, everything else is
    // percent-encoded in UTF-8.
    private static string Encode(string s)
    {
        var sb = new StringBuilder();
        var bytes = Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            // space → '+'
            if (b == 0x20) { sb.Append('+'); continue; }
            if (IsUnreserved((char)b)) { sb.Append((char)b); continue; }
            sb.Append('%');
            sb.Append(HexUpper(b >> 4));
            sb.Append(HexUpper(b & 0xF));
        }
        return sb.ToString();
    }

    private static bool IsUnreserved(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '*' || c == '-' ||
        c == '.' || c == '_';

    private static char HexUpper(int nibble) =>
        (char)(nibble < 10 ? ('0' + nibble) : ('A' + nibble - 10));

    private static string Decode(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Fast path: if no % and no + then return as-is.
        if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s;
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '+') { bytes.Add(0x20); continue; }
            if (c == '%' && i + 2 < s.Length)
            {
                int hi = FromHex(s[i + 1]);
                int lo = FromHex(s[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    bytes.Add((byte)((hi << 4) | lo));
                    i += 2;
                    continue;
                }
            }
            // Character outside ASCII: encode as UTF-8 for
            // faithful round-tripping.
            foreach (var b in Encoding.UTF8.GetBytes(new[] { c }))
            {
                bytes.Add(b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int FromHex(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        return -1;
    }
}
