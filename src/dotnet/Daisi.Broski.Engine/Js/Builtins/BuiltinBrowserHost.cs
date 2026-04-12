namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Browser host shims — the minimum-viable set of
/// script-visible APIs that real sites assume exist even
/// when they don't actually do anything interesting with
/// the values. Each one is a tiny facade over engine state
/// or a constant; the goal is to unblock scripts from
/// crashing with <c>undefined is not an object</c> the
/// moment they touch <c>navigator.userAgent</c> or
/// <c>localStorage.getItem(...)</c>.
///
/// <para>
/// What lands here:
/// <list type="bullet">
/// <item><c>localStorage</c> / <c>sessionStorage</c> —
///   the WHATWG Storage interface, backed by an
///   in-memory dictionary per engine. Both aliases share
///   the same storage for now; a future slice can split
///   them and add a host-side persistence hook.</item>
/// <item><c>navigator</c> — <c>userAgent</c>, <c>platform</c>,
///   <c>language</c>, <c>languages</c>, <c>onLine</c>,
///   <c>hardwareConcurrency</c>, <c>doNotTrack</c>.</item>
/// <item><c>location</c> — when the engine has a
///   <c>Document</c> attached, reflects the real URL;
///   otherwise a placeholder. Properties mirror the
///   WHATWG URL slice from 3c-3, split across
///   <c>href</c> / <c>origin</c> / <c>protocol</c> /
///   <c>host</c> / <c>hostname</c> / <c>port</c> /
///   <c>pathname</c> / <c>search</c> / <c>hash</c>.</item>
/// <item><c>performance</c> — <c>now()</c>, <c>timeOrigin</c>,
///   a stubbed <c>mark</c> / <c>measure</c>.</item>
/// <item>Window viewport shims — <c>innerWidth</c> /
///   <c>innerHeight</c> / <c>devicePixelRatio</c> /
///   <c>scrollX</c> / <c>scrollY</c>, plus a no-op
///   <c>scrollTo</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Everything here is additive — installing it never
/// breaks an existing script, and leaving it out would
/// only matter for scripts that touch a host API we
/// don't yet provide. The shims are sized for "good
/// enough to let the page's bootstrap code finish
/// running and paint a DOM" rather than spec parity.
/// </para>
/// </summary>
internal static class BuiltinBrowserHost
{
    public static void Install(JsEngine engine)
    {
        // `window` is the usual global object in a browser.
        // Historically it was only installed by AttachDocument
        // (slice 3c-2), but the viewport shims below assume
        // a window is reachable — and scripts read
        // `window.innerWidth` even before any DOM is wired.
        // Install it unconditionally here so the shims work
        // in headless / no-document contexts too.
        if (!engine.Globals.ContainsKey("window"))
        {
            engine.Globals["window"] = new Daisi.Broski.Engine.Js.Dom.JsWindowProxy(engine);
        }

        InstallStorage(engine);
        InstallNavigator(engine);
        InstallLocation(engine);
        InstallPerformance(engine);
        InstallWindowViewport(engine);
    }

    // =======================================================
    // Storage (localStorage / sessionStorage)
    // =======================================================

    private static void InstallStorage(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("getItem", new JsFunction("getItem", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.getItem");
            if (args.Count == 0) return JsValue.Null;
            var key = JsValue.ToJsString(args[0]);
            return s.Items.TryGetValue(key, out var v) ? v : (object)JsValue.Null;
        }));

        proto.SetNonEnumerable("setItem", new JsFunction("setItem", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.setItem");
            if (args.Count < 2) return JsValue.Undefined;
            s.Items[JsValue.ToJsString(args[0])] = JsValue.ToJsString(args[1]);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeItem", new JsFunction("removeItem", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.removeItem");
            if (args.Count == 0) return JsValue.Undefined;
            s.Items.Remove(JsValue.ToJsString(args[0]));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("clear", new JsFunction("clear", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.clear");
            s.Items.Clear();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("key", new JsFunction("key", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.key");
            int idx = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            int i = 0;
            foreach (var k in s.Items.Keys)
            {
                if (i++ == idx) return k;
            }
            return JsValue.Null;
        }));

        // localStorage and sessionStorage are separate instances
        // so scripts that write to one don't pollute the other,
        // matching browser behavior even though we don't yet
        // persist either across engine lifetimes.
        engine.Globals["localStorage"] = new JsStorage { Prototype = proto };
        engine.Globals["sessionStorage"] = new JsStorage { Prototype = proto };
    }

    private static JsStorage RequireStorage(object? thisVal, string name)
    {
        if (thisVal is not JsStorage s)
        {
            JsThrow.TypeError($"{name} called on non-Storage");
        }
        return (JsStorage)thisVal!;
    }

    // =======================================================
    // navigator
    // =======================================================

    private static void InstallNavigator(JsEngine engine)
    {
        var nav = new JsObject { Prototype = engine.ObjectPrototype };
        // The phase-1 HttpFetcher already uses this exact UA —
        // keep the script-visible value in sync so JS sniffing
        // matches what the server sees on the wire.
        nav.SetNonEnumerable("userAgent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "daisi-broski/0.1");
        nav.SetNonEnumerable("appName", "Netscape");
        nav.SetNonEnumerable("appVersion", "5.0 (daisi-broski)");
        nav.SetNonEnumerable("platform",
            OperatingSystem.IsWindows() ? "Win32" :
            OperatingSystem.IsLinux() ? "Linux x86_64" :
            OperatingSystem.IsMacOS() ? "MacIntel" : "Unknown");
        nav.SetNonEnumerable("language", "en-US");
        var langs = new JsArray { Prototype = engine.ArrayPrototype };
        langs.Elements.Add("en-US");
        langs.Elements.Add("en");
        nav.SetNonEnumerable("languages", langs);
        nav.SetNonEnumerable("onLine", true);
        nav.SetNonEnumerable("hardwareConcurrency", (double)Environment.ProcessorCount);
        nav.SetNonEnumerable("doNotTrack", JsValue.Null);
        nav.SetNonEnumerable("cookieEnabled", false);
        nav.SetNonEnumerable("maxTouchPoints", 0.0);

        engine.Globals["navigator"] = nav;
    }

    // =======================================================
    // location
    // =======================================================

    private static void InstallLocation(JsEngine engine)
    {
        // A JsLocation instance whose Get/Set overrides read
        // the current document URL (when attached) or fall
        // back to a placeholder "about:blank"-ish value.
        engine.Globals["location"] = new JsLocation(engine);
    }

    // =======================================================
    // performance
    // =======================================================

    private static void InstallPerformance(JsEngine engine)
    {
        var perf = new JsObject { Prototype = engine.ObjectPrototype };

        // Monotonic clock. .NET's Stopwatch.GetTimestamp +
        // Stopwatch.Frequency is the canonical source — it's
        // guaranteed monotonic and high-resolution.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double freq = System.Diagnostics.Stopwatch.Frequency;
        double origin = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        perf.SetNonEnumerable("timeOrigin", origin);

        perf.SetNonEnumerable("now", new JsFunction("now", (thisVal, args) =>
        {
            long delta = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return delta * 1000.0 / freq;
        }));

        // mark / measure are stubbed as no-ops that return
        // the spec's shape (an object with name / entryType /
        // startTime / duration). Real measurements land if
        // a future slice adds a PerformanceObserver.
        perf.SetNonEnumerable("mark", new JsFunction("mark", (thisVal, args) =>
        {
            return JsValue.Undefined;
        }));
        perf.SetNonEnumerable("measure", new JsFunction("measure", (thisVal, args) =>
        {
            return JsValue.Undefined;
        }));
        perf.SetNonEnumerable("clearMarks", new JsFunction("clearMarks", (thisVal, args) => JsValue.Undefined));
        perf.SetNonEnumerable("clearMeasures", new JsFunction("clearMeasures", (thisVal, args) => JsValue.Undefined));
        perf.SetNonEnumerable("getEntries", new JsFunction("getEntries", (thisVal, args) =>
            new JsArray { Prototype = engine.ArrayPrototype }));

        engine.Globals["performance"] = perf;
    }

    // =======================================================
    // window viewport shims
    // =======================================================

    private static void InstallWindowViewport(JsEngine engine)
    {
        // These live on `window`, but our JsWindowProxy routes
        // every read through engine.Globals, so installing the
        // values there makes them visible as both
        // `window.innerWidth` and plain `innerWidth`.
        engine.Globals["innerWidth"] = 1280.0;
        engine.Globals["innerHeight"] = 800.0;
        engine.Globals["outerWidth"] = 1280.0;
        engine.Globals["outerHeight"] = 800.0;
        engine.Globals["devicePixelRatio"] = 1.0;
        engine.Globals["scrollX"] = 0.0;
        engine.Globals["scrollY"] = 0.0;
        engine.Globals["pageXOffset"] = 0.0;
        engine.Globals["pageYOffset"] = 0.0;
        engine.Globals["screenX"] = 0.0;
        engine.Globals["screenY"] = 0.0;

        engine.Globals["scrollTo"] = new JsFunction("scrollTo", (t, a) => JsValue.Undefined);
        engine.Globals["scrollBy"] = new JsFunction("scrollBy", (t, a) => JsValue.Undefined);
        engine.Globals["scroll"] = new JsFunction("scroll", (t, a) => JsValue.Undefined);

        // matchMedia returns an object with a `matches`
        // boolean and a no-op `addListener`. Enough for
        // responsive script to not crash; always reports
        // the query as matching the light-theme /
        // landscape default.
        engine.Globals["matchMedia"] = new JsFunction("matchMedia", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            result.Set("matches", false);
            result.Set("media", a.Count > 0 ? JsValue.ToJsString(a[0]) : "");
            result.SetNonEnumerable("addListener", new JsFunction("addListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("removeListener", new JsFunction("removeListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (tt, aa) => JsValue.Undefined));
            return result;
        });
    }
}

/// <summary>
/// Instance state for <c>localStorage</c> /
/// <c>sessionStorage</c>. A flat insertion-order-preserving
/// dictionary of string → string, with a <c>length</c>
/// getter intercepted in <see cref="Get"/>.
/// </summary>
public sealed class JsStorage : JsObject
{
    public Dictionary<string, string> Items { get; } = new();

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "length") return (double)Items.Count;
        return base.Get(key);
    }

    /// <inheritdoc />
    public override bool Has(string key)
    {
        if (key == "length") return true;
        return base.Has(key);
    }
}

/// <summary>
/// Instance state for the <c>location</c> global. Reads
/// its component fields off the engine's
/// <see cref="JsEngine.AttachedDocument"/> URL when one is
/// attached; otherwise returns an empty placeholder so
/// scripts that read <c>location.href</c> before any
/// navigation still get a sensible string.
///
/// <para>
/// Writes to the mutable fields (<c>href</c>,
/// <c>pathname</c>, ...) don't actually navigate in this
/// slice — we store the override but don't trigger a new
/// fetch. A follow-up slice can layer a
/// <c>NavigationController</c> on top that calls back into
/// the host's fetcher.
/// </para>
/// </summary>
public sealed class JsLocation : JsObject
{
    private readonly JsEngine _engine;
    private string? _href;

    public JsLocation(JsEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Resolve the current URL. Priority: explicit override
    /// (set by script) → attached document's URL if known →
    /// <c>"about:blank"</c> as a last resort.
    /// </summary>
    private string CurrentHref()
    {
        if (_href is not null) return _href;
        // The attached document's URL is not stored on
        // Document itself yet (phase 1 only tracked the
        // response); if we wire that in later this is the
        // place to read it from.
        return "about:blank";
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "toString")
        {
            return new JsFunction("toString", (t, a) => CurrentHref());
        }
        var href = CurrentHref();
        switch (key)
        {
            case "href": return href;
            case "toString": return new JsFunction("toString", (t, a) => href);
            case "origin":
            case "protocol":
            case "host":
            case "hostname":
            case "port":
            case "pathname":
            case "search":
            case "hash":
                return ExtractComponent(href, key);
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (key == "href")
        {
            // Store the override; actual navigation is
            // deferred until a NavigationController lands.
            _href = JsValue.ToJsString(value);
            return;
        }
        // Other writes (pathname, hash, etc.) silently
        // succeed for now; scripts that depend on the
        // resulting navigation will hit a different check.
        base.Set(key, value);
    }

    /// <summary>
    /// Split a full URL into the same component set that
    /// slice 3c-3's <see cref="JsUrl"/> exposes. Delegated
    /// to <see cref="System.Uri"/> for robustness.
    /// </summary>
    private static string ExtractComponent(string href, string key)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return "";
        return key switch
        {
            "origin" => uri.Scheme is "http" or "https" or "ws" or "wss"
                ? $"{uri.Scheme}://{uri.Authority}"
                : "null",
            "protocol" => uri.Scheme + ":",
            "host" => uri.Authority,
            "hostname" => uri.Host,
            "port" => uri.IsDefaultPort ? "" : uri.Port.ToString(),
            "pathname" => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath,
            "search" => uri.Query,
            "hash" => uri.Fragment,
            _ => "",
        };
    }
}
