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
        // `self` and `globalThis` are both spec aliases for
        // the global object. Next.js-based sites (and every
        // modern bundler's streaming-SSR runtime) call
        // `self.__next_f.push(...)` on every chunk, so
        // failing to install `self` blocks the entire
        // hydration pipeline. Install as aliases for the
        // window proxy so any assignment to `self.X` also
        // reaches `window.X` and vice versa.
        if (!engine.Globals.ContainsKey("self"))
        {
            engine.Globals["self"] = engine.Globals["window"];
        }
        if (!engine.Globals.ContainsKey("globalThis"))
        {
            engine.Globals["globalThis"] = engine.Globals["window"];
        }

        // `top` and `parent` are frame-hierarchy globals. In a
        // single-frame headless context they all point at window.
        if (!engine.Globals.ContainsKey("top"))
        {
            engine.Globals["top"] = engine.Globals["window"];
        }
        if (!engine.Globals.ContainsKey("parent"))
        {
            engine.Globals["parent"] = engine.Globals["window"];
        }
        if (!engine.Globals.ContainsKey("frames"))
        {
            engine.Globals["frames"] = engine.Globals["window"];
        }

        // Global event target methods — in browsers, bare
        // `addEventListener('load', fn)` routes to `window`.
        // Since our JsWindowProxy isn't a JsDomNode with the
        // event system, we install these as globals that
        // silently accept the registration. A future slice
        // can wire them to a real event dispatch on the
        // window object for events like 'load', 'error',
        // 'unhandledrejection'.
        engine.Globals["addEventListener"] = new JsFunction("addEventListener", (t, a) => JsValue.Undefined);
        engine.Globals["removeEventListener"] = new JsFunction("removeEventListener", (t, a) => JsValue.Undefined);
        engine.Globals["dispatchEvent"] = new JsFunction("dispatchEvent", (t, a) => true);

        // XPathEvaluator — htmx and other DOM libraries check
        // for its existence as a feature-detection signal.
        // Install a minimal stub that creates evaluator
        // instances with an evaluate() method returning an
        // empty result set.
        engine.Globals["XPathEvaluator"] = new JsFunction("XPathEvaluator", (t, a) =>
        {
            var evaluator = new JsObject { Prototype = engine.ObjectPrototype };
            evaluator.SetNonEnumerable("evaluate", new JsFunction("evaluate", (et, ea) =>
            {
                var result = new JsObject { Prototype = engine.ObjectPrototype };
                result.Set("resultType", 0.0);
                result.Set("snapshotLength", 0.0);
                result.SetNonEnumerable("iterateNext", new JsFunction("iterateNext", (rt, ra) => JsValue.Null));
                result.SetNonEnumerable("snapshotItem", new JsFunction("snapshotItem", (rt, ra) => JsValue.Null));
                return result;
            }));
            evaluator.SetNonEnumerable("createExpression", new JsFunction("createExpression", (et, ea) =>
            {
                var expr = new JsObject { Prototype = engine.ObjectPrototype };
                expr.SetNonEnumerable("evaluate", new JsFunction("evaluate", (rt, ra) =>
                {
                    var result = new JsObject { Prototype = engine.ObjectPrototype };
                    result.Set("resultType", 0.0);
                    result.Set("snapshotLength", 0.0);
                    result.SetNonEnumerable("iterateNext", new JsFunction("iterateNext", (rrt, rra) => JsValue.Null));
                    return result;
                }));
                return expr;
            }));
            return evaluator;
        });

        // ReadableStream — the WHATWG Streams API. PlanetScale
        // and other sites use it for streaming responses.
        // Install a minimal stub constructor so feature-
        // detection code doesn't crash. A real implementation
        // would need async iteration + the controller API.
        engine.Globals["ReadableStream"] = new JsFunction("ReadableStream", (t, a) =>
        {
            var stream = new JsObject { Prototype = engine.ObjectPrototype };
            stream.SetNonEnumerable("getReader", new JsFunction("getReader", (tt, aa) =>
            {
                var reader = new JsObject { Prototype = engine.ObjectPrototype };
                reader.SetNonEnumerable("read", new JsFunction("read", (rt, ra) =>
                {
                    // Return a resolved promise with {done: true}
                    var result = new JsObject { Prototype = engine.ObjectPrototype };
                    result.Set("done", true);
                    result.Set("value", JsValue.Undefined);
                    var p = new JsPromise(engine);
                    p.Resolve(result);
                    return p;
                }));
                reader.SetNonEnumerable("cancel", new JsFunction("cancel", (rt, ra) =>
                {
                    var p = new JsPromise(engine);
                    p.Resolve(JsValue.Undefined);
                    return p;
                }));
                reader.SetNonEnumerable("releaseLock", new JsFunction("releaseLock", (rt, ra) => JsValue.Undefined));
                return reader;
            }));
            stream.SetNonEnumerable("cancel", new JsFunction("cancel", (tt, aa) =>
            {
                var p = new JsPromise(engine);
                p.Resolve(JsValue.Undefined);
                return p;
            }));
            stream.Set("locked", false);
            // If a source was passed, try calling start() on it
            // so the controller pattern minimally works.
            if (a.Count > 0 && a[0] is JsObject source)
            {
                var startFn = source.Get("start");
                if (startFn is JsFunction sf)
                {
                    var controller = new JsObject { Prototype = engine.ObjectPrototype };
                    controller.SetNonEnumerable("enqueue", new JsFunction("enqueue", (ct, ca) => JsValue.Undefined));
                    controller.SetNonEnumerable("close", new JsFunction("close", (ct, ca) => JsValue.Undefined));
                    controller.SetNonEnumerable("error", new JsFunction("error", (ct, ca) => JsValue.Undefined));
                    try
                    {
                        engine.Vm.InvokeJsFunction(sf, source, new object?[] { controller });
                    }
                    catch { } // Don't crash on errors in the source start callback
                }
            }
            return stream;
        });

        // WritableStream stub — some sites check for this too.
        engine.Globals["WritableStream"] = new JsFunction("WritableStream", (t, a) =>
        {
            var stream = new JsObject { Prototype = engine.ObjectPrototype };
            stream.SetNonEnumerable("getWriter", new JsFunction("getWriter", (tt, aa) =>
            {
                var writer = new JsObject { Prototype = engine.ObjectPrototype };
                writer.SetNonEnumerable("write", new JsFunction("write", (wt, wa) =>
                {
                    var p = new JsPromise(engine);
                    p.Resolve(JsValue.Undefined);
                    return p;
                }));
                writer.SetNonEnumerable("close", new JsFunction("close", (wt, wa) =>
                {
                    var p = new JsPromise(engine);
                    p.Resolve(JsValue.Undefined);
                    return p;
                }));
                writer.SetNonEnumerable("releaseLock", new JsFunction("releaseLock", (wt, wa) => JsValue.Undefined));
                return writer;
            }));
            stream.Set("locked", false);
            return stream;
        });

        // TransformStream stub
        engine.Globals["TransformStream"] = new JsFunction("TransformStream", (t, a) =>
        {
            var stream = new JsObject { Prototype = engine.ObjectPrototype };
            stream.Set("readable", engine.Vm.InvokeJsFunction(
                (JsFunction)engine.Globals["ReadableStream"]!, JsValue.Undefined, System.Array.Empty<object?>()));
            stream.Set("writable", engine.Vm.InvokeJsFunction(
                (JsFunction)engine.Globals["WritableStream"]!, JsValue.Undefined, System.Array.Empty<object?>()));
            return stream;
        });

        // XMLHttpRequest installs as a real implementation
        // backed by the engine's FetchHandler — the same path
        // fetch() uses, so XHR shares the cookie jar, redirect
        // rules, and test-time stubs. See BuiltinXhr.
        BuiltinXhr.Install(engine);

        InstallStorage(engine);
        InstallNavigator(engine);
        InstallLocation(engine);
        InstallPerformance(engine);
        InstallWindowViewport(engine);
        InstallHistory(engine);
        InstallAnimationFrame(engine);
        InstallObservers(engine);

        // Image constructor — `new Image()` is used for beacon
        // pixels (Google analytics, error reporting) and image
        // preloads. The object just needs `src`, `width`,
        // `height` properties; setting `src` triggers a fetch
        // in a real browser but we leave it as a no-op in
        // headless mode.
        engine.Globals["Image"] = new JsFunction("Image", (thisVal, args) =>
        {
            var img = new JsObject { Prototype = engine.ObjectPrototype };
            img.Set("width", args.Count > 0 ? JsValue.ToNumber(args[0]) : 0.0);
            img.Set("height", args.Count > 1 ? JsValue.ToNumber(args[1]) : 0.0);
            img.Set("src", "");
            img.Set("complete", false);
            return img;
        });
    }

    /// <summary>Wrap a <see cref="Daisi.Broski.Engine.Css.ComputedStyle"/>
    /// in the script-shaped CSSStyleDeclaration surface
    /// (<c>length</c>, indexed <c>item(i)</c>,
    /// <c>getPropertyValue</c>, plus a getter per kebab /
    /// camel property name). Read-only — writes are accepted
    /// but ignored, matching what spec'd
    /// <c>getComputedStyle</c> returns.</summary>
    private static JsObject BuildComputedStyleObject(
        JsEngine engine, Daisi.Broski.Engine.Css.ComputedStyle computed)
    {
        var obj = new ComputedStyleProxy(computed)
        { Prototype = engine.ObjectPrototype };
        obj.SetNonEnumerable("getPropertyValue",
            new JsFunction("getPropertyValue", (t, a) =>
            {
                if (a.Count == 0) return "";
                return computed.GetPropertyValue(JsValue.ToJsString(a[0]));
            }));
        obj.SetNonEnumerable("getPropertyPriority",
            new JsFunction("getPropertyPriority", (t, a) => ""));
        obj.SetNonEnumerable("setProperty",
            new JsFunction("setProperty", (t, a) => JsValue.Undefined));
        obj.SetNonEnumerable("removeProperty",
            new JsFunction("removeProperty", (t, a) => ""));
        obj.SetNonEnumerable("item", new JsFunction("item", (t, a) =>
        {
            int idx = a.Count > 0 ? (int)JsValue.ToNumber(a[0]) : 0;
            int i = 0;
            foreach (var kv in computed.Entries())
            {
                if (i++ == idx) return kv.Key;
            }
            return "";
        }));
        return obj;
    }

    /// <summary>JsObject subclass that surfaces computed-style
    /// values via Get without copying the dictionary into the
    /// JsObject's own property bag. Honors both kebab-case
    /// and camelCase reads (so <c>style.color</c> and
    /// <c>style.backgroundColor</c> both work) plus the
    /// <c>length</c> shortcut.</summary>
    private sealed class ComputedStyleProxy : JsObject
    {
        private readonly Daisi.Broski.Engine.Css.ComputedStyle _computed;
        public ComputedStyleProxy(Daisi.Broski.Engine.Css.ComputedStyle computed)
        {
            _computed = computed;
        }

        public override object? Get(string key)
        {
            if (key == "length") return (double)_computed.Length;
            // Try direct (kebab-case) and camelCase → kebab
            // conversion. Properties on this set come from the
            // resolver (which lowercases) so OrdinalIgnoreCase
            // matches camelCase too.
            if (_computed.TryGet(key, out var v)) return v;
            var kebab = CamelToKebab(key);
            if (_computed.TryGet(kebab, out var v2)) return v2;
            // Resolve own properties (the installed prototype
            // methods like getPropertyValue) BEFORE the
            // CSS-property fallback so we don't shadow them
            // with the empty-string default.
            var baseValue = base.Get(key);
            if (baseValue is not JsUndefined) return baseValue;
            // For anything that looks like a CSS property name,
            // honor the spec's "always return a string, never
            // undefined" rule — unset values come back as "".
            if (LooksLikeCssProperty(key)) return "";
            return baseValue;
        }

        private static bool LooksLikeCssProperty(string key)
        {
            if (key.Length == 0) return false;
            if (key.Contains('-')) return true;
            if (!char.IsLower(key[0])) return false;
            // camelCase: lowercase first char + at least one
            // uppercase later — assume CSS shape.
            for (int i = 1; i < key.Length; i++)
            {
                if (char.IsUpper(key[i])) return true;
            }
            // Single-word lowercase: the common CSS property
            // names that real scripts read off getComputedStyle
            // without going through getPropertyValue. Conservative
            // list — better to occasionally return undefined
            // for an obscure single-word property than to
            // intercept a non-CSS field.
            return key is "color" or "display" or "width" or "height"
                or "margin" or "padding" or "position" or "top"
                or "left" or "right" or "bottom" or "overflow"
                or "opacity" or "visibility" or "cursor" or "float"
                or "clear" or "content" or "border" or "outline"
                or "background" or "font" or "transform"
                or "transition" or "animation" or "flex" or "grid"
                or "gap" or "order" or "direction" or "quotes";
        }

        public override bool Has(string key) =>
            key == "length" || _computed.TryGet(key, out _) ||
            _computed.TryGet(CamelToKebab(key), out _) || base.Has(key);

        private static string CamelToKebab(string name)
        {
            if (name.Contains('-')) return name;
            var sb = new System.Text.StringBuilder(name.Length + 4);
            foreach (var c in name)
            {
                if (char.IsUpper(c))
                {
                    if (sb.Length > 0) sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
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
            s.NotifyChanged();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeItem", new JsFunction("removeItem", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.removeItem");
            if (args.Count == 0) return JsValue.Undefined;
            if (s.Items.Remove(JsValue.ToJsString(args[0]))) s.NotifyChanged();
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("clear", new JsFunction("clear", (thisVal, args) =>
        {
            var s = RequireStorage(thisVal, "Storage.prototype.clear");
            if (s.Items.Count == 0) return JsValue.Undefined;
            s.Items.Clear();
            s.NotifyChanged();
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
        // matching browser behavior. Per phase-5a, localStorage
        // is file-backed when the engine carries a non-null
        // <see cref="IStorageBackend"/>; sessionStorage
        // is always transient (browser semantics: session storage
        // does not outlive the hosting engine).
        var localStorage = new JsStorage { Prototype = proto };
        var sessionStorage = new JsStorage { Prototype = proto };
        engine.Globals["localStorage"] = localStorage;
        engine.Globals["sessionStorage"] = sessionStorage;

        // The manager is wired up even when the engine currently
        // holds the default NullStorageBackend — the engine lets
        // the embedder swap in a real backend later via
        // <see cref="JsEngine.SetStorageBackend"/>, and the
        // manager handles that transition.
        engine.StorageManager = new JsStorageManager(
            localStorage, sessionStorage, engine);
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

        // Legacy Navigation Timing API (performance.timing).
        // Deprecated in favor of PerformanceNavigationTiming
        // but still widely used by analytics libraries
        // (SolidJS, Qwik, Express sites all read
        // performance.timing.navigationStart).
        var timing = new JsObject { Prototype = engine.ObjectPrototype };
        timing.Set("navigationStart", origin);
        timing.Set("unloadEventStart", 0.0);
        timing.Set("unloadEventEnd", 0.0);
        timing.Set("redirectStart", 0.0);
        timing.Set("redirectEnd", 0.0);
        timing.Set("fetchStart", origin);
        timing.Set("domainLookupStart", origin);
        timing.Set("domainLookupEnd", origin);
        timing.Set("connectStart", origin);
        timing.Set("connectEnd", origin);
        timing.Set("requestStart", origin);
        timing.Set("responseStart", origin);
        timing.Set("responseEnd", origin);
        timing.Set("domLoading", origin);
        timing.Set("domInteractive", origin);
        timing.Set("domContentLoadedEventStart", origin);
        timing.Set("domContentLoadedEventEnd", origin);
        timing.Set("domComplete", origin);
        timing.Set("loadEventStart", origin);
        timing.Set("loadEventEnd", origin);
        perf.SetNonEnumerable("timing", timing);

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

        // getComputedStyle — runs the phase-6b cascade
        // resolver against the element's owner document,
        // returning a read-only style object that includes
        // matched author rules, inline declarations, and
        // inherited values from ancestors. Falls back to an
        // empty stub when the argument isn't a wrapped DOM
        // element so feature-detection paths
        // (`getComputedStyle(window).whatever`) still don't
        // crash.
        engine.Globals["getComputedStyle"] = new JsFunction("getComputedStyle", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not Daisi.Broski.Engine.Js.Dom.JsDomElement el)
            {
                var stub = new JsObject { Prototype = engine.ObjectPrototype };
                stub.SetNonEnumerable("getPropertyValue",
                    new JsFunction("getPropertyValue", (t, a) => ""));
                return stub;
            }
            if (el.BackingNode is not Daisi.Broski.Engine.Dom.Element backing)
            {
                return new JsObject { Prototype = engine.ObjectPrototype };
            }
            var computed = Daisi.Broski.Engine.Css.StyleResolver.Resolve(backing);
            return BuildComputedStyleObject(engine, computed);
        });

        // matchMedia returns a MediaQueryList-shaped object.
        // We evaluate a small subset of common media queries
        // against the engine's viewport constants (innerWidth
        // / innerHeight at 1280×800, light theme, no-motion).
        engine.Globals["matchMedia"] = new JsFunction("matchMedia", (t, a) =>
        {
            var query = a.Count > 0 ? JsValue.ToJsString(a[0]) : "";
            bool matches = EvaluateMediaQuery(query);
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            result.Set("matches", matches);
            result.Set("media", query);
            result.SetNonEnumerable("addListener", new JsFunction("addListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("removeListener", new JsFunction("removeListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (tt, aa) => JsValue.Undefined));
            result.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (tt, aa) => JsValue.Undefined));
            return result;
        });
    }

    // =======================================================
    // history
    // =======================================================

    private static void InstallHistory(JsEngine engine)
    {
        // A single History instance per engine, with a
        // simple entry list so pushState / replaceState /
        // back / forward have coherent semantics even
        // though we don't actually navigate.
        var entries = new List<JsHistoryEntry>
        {
            new JsHistoryEntry(JsValue.Null, "", "about:blank"),
        };
        int index = 0;

        var history = new JsObject { Prototype = engine.ObjectPrototype };

        history.SetAccessor("length",
            new JsFunction("get length", (t, a) => (double)entries.Count),
            null);
        history.SetAccessor("state",
            new JsFunction("get state", (t, a) => entries[index].State),
            null);
        history.SetAccessor("scrollRestoration",
            new JsFunction("get scrollRestoration", (t, a) => "auto"),
            new JsFunction("set scrollRestoration", (t, a) => JsValue.Undefined));

        // Helper: update location.href when the current
        // history entry changes URL.
        void UpdateLocation()
        {
            if (engine.Globals.TryGetValue("location", out var loc) && loc is JsLocation jLoc)
            {
                jLoc.Set("href", entries[index].Url);
            }
        }

        // Helper: fire a popstate event on window with the
        // current entry's state. Per spec, popstate fires
        // only on back / forward / go — NOT on pushState
        // or replaceState.
        void FirePopstate(JsVM vm)
        {
            if (engine.Globals.TryGetValue("window", out var win) && win is JsObject winObj)
            {
                var evt = new Daisi.Broski.Engine.Js.Dom.JsDomCustomEvent(
                    "popstate", bubbles: false, cancelable: false, entries[index].State)
                {
                    Prototype = engine.EventPrototype,
                };
                // If window is a JsDomNode we can dispatch properly;
                // otherwise just look for an onpopstate handler.
                if (winObj is Daisi.Broski.Engine.Js.Dom.JsDomNode domWin)
                {
                    domWin.DispatchEvent(vm, evt);
                }
            }
        }

        history.SetNonEnumerable("pushState", new JsFunction("pushState", (thisVal, args) =>
        {
            var state = args.Count > 0 ? args[0] : JsValue.Null;
            var title = args.Count > 1 ? JsValue.ToJsString(args[1]) : "";
            var url = args.Count > 2 ? JsValue.ToJsString(args[2]) : entries[index].Url;
            // Truncate any forward history past the current
            // index, then append the new entry.
            if (index < entries.Count - 1)
            {
                entries.RemoveRange(index + 1, entries.Count - 1 - index);
            }
            entries.Add(new JsHistoryEntry(state, title, url));
            index = entries.Count - 1;
            // Per spec: pushState updates location but does
            // NOT fire popstate.
            UpdateLocation();
            return JsValue.Undefined;
        }));

        history.SetNonEnumerable("replaceState", new JsFunction("replaceState", (thisVal, args) =>
        {
            var state = args.Count > 0 ? args[0] : JsValue.Null;
            var title = args.Count > 1 ? JsValue.ToJsString(args[1]) : "";
            var url = args.Count > 2 ? JsValue.ToJsString(args[2]) : entries[index].Url;
            entries[index] = new JsHistoryEntry(state, title, url);
            UpdateLocation();
            return JsValue.Undefined;
        }));

        history.SetNonEnumerable("back", new JsFunction("back", (vm, thisVal, args) =>
        {
            if (index > 0)
            {
                index--;
                UpdateLocation();
                FirePopstate(vm);
            }
            return JsValue.Undefined;
        }));

        history.SetNonEnumerable("forward", new JsFunction("forward", (vm, thisVal, args) =>
        {
            if (index < entries.Count - 1)
            {
                index++;
                UpdateLocation();
                FirePopstate(vm);
            }
            return JsValue.Undefined;
        }));

        history.SetNonEnumerable("go", new JsFunction("go", (vm, thisVal, args) =>
        {
            int delta = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            if (delta == 0) return JsValue.Undefined;
            int target = index + delta;
            if (target < 0) target = 0;
            if (target >= entries.Count) target = entries.Count - 1;
            if (target != index)
            {
                index = target;
                UpdateLocation();
                FirePopstate(vm);
            }
            return JsValue.Undefined;
        }));

        engine.Globals["history"] = history;
    }

    // =======================================================
    // requestAnimationFrame
    // =======================================================

    private static void InstallAnimationFrame(JsEngine engine)
    {
        // rAF schedules the callback as a zero-delay timer.
        // Real browsers align with the display refresh
        // (~16ms per frame); we don't have a render loop
        // to drive that, so the best approximation is "run
        // it on the next event-loop tick". Most scripts use
        // rAF for deferred work rather than precise timing
        // so this covers the common case.
        engine.Globals["requestAnimationFrame"] = new JsFunction("requestAnimationFrame", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsFunction cb)
            {
                JsThrow.TypeError("requestAnimationFrame: callback must be a function");
                return null;
            }
            // The callback signature is `cb(domHighResTimestamp)`.
            // Pass performance.now() equivalent as the arg.
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            var forwarded = new object?[1];
            forwarded[0] = 0.0;
            // The event loop's ScheduleTimer already accepts
            // a forwarded-args array; we update it to reflect
            // "time since engine start" at fire time via a
            // wrapper function.
            var wrapper = new JsFunction("[raf]", (vm, tv, ta) =>
            {
                long delta = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                double ts = delta * 1000.0 / freq;
                return vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { ts });
            });
            int id = engine.EventLoop.ScheduleTimer(0, wrapper,
                System.Array.Empty<object?>(), isInterval: false);
            return (double)id;
        });

        engine.Globals["cancelAnimationFrame"] = new JsFunction("cancelAnimationFrame", (thisVal, args) =>
        {
            if (args.Count > 0)
            {
                int id = (int)JsValue.ToNumber(args[0]);
                engine.EventLoop.ClearTimer(id);
            }
            return JsValue.Undefined;
        });
    }

    // =======================================================
    // IntersectionObserver / ResizeObserver / MutationObserver
    // =======================================================

    private static void InstallObservers(JsEngine engine)
    {
        // All three observer classes are common enough that
        // scripts assume they exist, but sitting on top of a
        // real layout / mutation engine. We install stub
        // classes that accept the callback + options, never
        // fire, and expose the spec-shaped methods
        // (observe / unobserve / disconnect / takeRecords).
        // Scripts that set up observers get a functional
        // object they can call into without crashes; they
        // just don't receive any notifications.
        engine.Globals["IntersectionObserver"] = MakeObserverCtor(engine, "IntersectionObserver");
        engine.Globals["ResizeObserver"] = MakeObserverCtor(engine, "ResizeObserver");
        // Real implementation — replaces the stub now that the
        // engine has a per-document MutationDispatcher.
        BuiltinMutationObserver.Install(engine);
        engine.Globals["PerformanceObserver"] = MakeObserverCtor(engine, "PerformanceObserver");

        // Common DOM-API constants + namespaces real sites poke at
        // before doing any actual layout work. Without these as at
        // least bare objects, scripts crash with ReferenceError on
        // module load (seen on airbnb, cloudflare, anthropic).
        var nodeFilter = new JsObject { Prototype = engine.ObjectPrototype };
        // Standard NodeFilter constants — bitmask values from the
        // DOM Traversal spec. Real consumers use these to
        // configure TreeWalker / NodeIterator.
        nodeFilter.Set("FILTER_ACCEPT", 1.0);
        nodeFilter.Set("FILTER_REJECT", 2.0);
        nodeFilter.Set("FILTER_SKIP", 3.0);
        nodeFilter.Set("SHOW_ALL", (double)0xFFFFFFFFu);
        nodeFilter.Set("SHOW_ELEMENT", 1.0);
        nodeFilter.Set("SHOW_ATTRIBUTE", 2.0);
        nodeFilter.Set("SHOW_TEXT", 4.0);
        nodeFilter.Set("SHOW_CDATA_SECTION", 8.0);
        nodeFilter.Set("SHOW_COMMENT", 128.0);
        nodeFilter.Set("SHOW_DOCUMENT", 256.0);
        nodeFilter.Set("SHOW_DOCUMENT_TYPE", 512.0);
        nodeFilter.Set("SHOW_DOCUMENT_FRAGMENT", 1024.0);
        engine.Globals["NodeFilter"] = nodeFilter;

        // DOM interface constructors that real-world code uses
        // either as `instanceof` targets (`x instanceof HTMLElement`)
        // or as registration anchors (`Object.defineProperty(
        // HTMLElement.prototype, ...)`). We install bare callable
        // stubs whose `.prototype` is a fresh JsObject — adequate
        // for both patterns since we don't yet wire DOM element
        // wrappers to extend these prototypes (so instanceof
        // returns false, which is the safer default — sites
        // typically have a fallback path).
        InstallDomInterfaceStub(engine, "Node");
        InstallDomInterfaceStub(engine, "Element");
        InstallDomInterfaceStub(engine, "HTMLElement");
        InstallDomInterfaceStub(engine, "HTMLDivElement");
        InstallDomInterfaceStub(engine, "HTMLAnchorElement");
        InstallDomInterfaceStub(engine, "HTMLInputElement");
        InstallDomInterfaceStub(engine, "HTMLButtonElement");
        InstallDomInterfaceStub(engine, "HTMLImageElement");
        InstallDomInterfaceStub(engine, "HTMLFormElement");
        InstallDomInterfaceStub(engine, "HTMLScriptElement");
        InstallDomInterfaceStub(engine, "HTMLStyleElement");
        InstallDomInterfaceStub(engine, "HTMLLinkElement");
        InstallDomInterfaceStub(engine, "HTMLCanvasElement");
        InstallDomInterfaceStub(engine, "HTMLVideoElement");
        InstallDomInterfaceStub(engine, "HTMLAudioElement");
        InstallDomInterfaceStub(engine, "HTMLIFrameElement");
        InstallDomInterfaceStub(engine, "HTMLTemplateElement");
        InstallDomInterfaceStub(engine, "HTMLBodyElement");
        InstallDomInterfaceStub(engine, "HTMLHeadElement");
        InstallDomInterfaceStub(engine, "HTMLMetaElement");
        InstallDomInterfaceStub(engine, "ShadowRoot");
        InstallDomInterfaceStub(engine, "DocumentFragment");
        InstallDomInterfaceStub(engine, "Text");
        InstallDomInterfaceStub(engine, "Comment");
        InstallDomInterfaceStub(engine, "Document");
        InstallDomInterfaceStub(engine, "EventTarget");

        // CSS namespace — typically used as `CSS.supports(...)` or
        // `CSS.escape(...)`. We return false / pass-through stubs
        // so consumers don't crash; real CSSOM support is in a
        // future slice.
        var cssObj = new JsObject { Prototype = engine.ObjectPrototype };
        cssObj.Set("supports", new JsFunction("supports",
            (thisVal, args) => false));
        cssObj.Set("escape", new JsFunction("escape", (thisVal, args) =>
        {
            // Per CSS.escape spec: backslash-escape any character
            // outside [-_a-zA-Z0-9]. Sufficient for sites that use
            // it to build selector strings from arbitrary input.
            if (args.Count == 0) return "";
            var s = JsValue.ToJsString(args[0]);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('\\').Append(c);
                }
            }
            return sb.ToString();
        }));
        engine.Globals["CSS"] = cssObj;
    }

    /// <summary>
    /// Install a bare DOM-interface constructor stub at
    /// <paramref name="name"/> with a fresh prototype. Real
    /// instanceof checks against these will return false (no
    /// DOM element wrapper extends them yet); the value of the
    /// stub is to keep `typeof HTMLElement === 'function'`,
    /// `HTMLElement.prototype` accessible, and assignment to
    /// `HTMLElement.prototype.foo = ...` working — all common
    /// patterns in polyfills and shadow-DOM helpers.
    /// </summary>
    private static void InstallDomInterfaceStub(JsEngine engine, string name)
    {
        var ctor = new JsFunction(name, (thisVal, args) =>
        {
            // `new HTMLElement()` is illegal in real browsers (the
            // constructor is "illegal"); we mirror that by raising
            // a TypeError so callers don't get a half-built object.
            JsThrow.TypeError($"Illegal constructor: {name}");
            return null;
        });
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        proto.SetNonEnumerable("constructor", ctor);
        ctor.SetNonEnumerable("prototype", proto);
        engine.Globals[name] = ctor;
    }

    /// <summary>
    /// Evaluate a media query string against the engine's
    /// hardcoded viewport (1280×800, light theme, no motion
    /// preference, screen media). Covers the common queries
    /// real scripts actually test:
    /// <list type="bullet">
    /// <item><c>(min-width: Npx)</c> / <c>(max-width: Npx)</c></item>
    /// <item><c>(min-height: Npx)</c> / <c>(max-height: Npx)</c></item>
    /// <item><c>(prefers-color-scheme: dark|light)</c></item>
    /// <item><c>(prefers-reduced-motion: reduce|no-preference)</c></item>
    /// <item><c>screen</c> / <c>print</c> / <c>all</c></item>
    /// </list>
    /// Unknown queries default to <c>false</c>.
    /// </summary>
    private static bool EvaluateMediaQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim().ToLowerInvariant();

        // Media type bare checks.
        if (q == "all" || q == "screen") return true;
        if (q == "print") return false;

        // Compound checks BEFORE single-paren stripping —
        // otherwise `(a) and (b)` would strip the outer
        // parens and misparse.
        if (q.StartsWith("not "))
        {
            return !EvaluateMediaQuery(q.Substring(4));
        }
        if (q.Contains(") and ("))
        {
            foreach (var part in q.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!EvaluateMediaQuery(part.Trim())) return false;
            }
            return true;
        }
        if (q.Contains(") or (") || q.Contains("), ("))
        {
            var sep = q.Contains(") or (") ? " or " : ", ";
            foreach (var part in q.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (EvaluateMediaQuery(part.Trim())) return true;
            }
            return false;
        }

        // Simple feature queries — extract the content
        // between the outermost parens.
        if (q.StartsWith('(') && q.EndsWith(')'))
        {
            var inner = q.Substring(1, q.Length - 2).Trim();
            return EvaluateFeature(inner);
        }

        // Unknown query → false (safe default).
        return false;
    }

    private static bool EvaluateFeature(string feature)
    {
        // Split on ':'
        var colon = feature.IndexOf(':');
        if (colon < 0)
        {
            // Bare feature test like `(color)` → true for a
            // color-capable display.
            return feature is "color" or "hover" or "pointer";
        }
        var name = feature.Substring(0, colon).Trim();
        var value = feature.Substring(colon + 1).Trim();

        // Viewport dimensions (our constants: 1280×800).
        const int W = 1280;
        const int H = 800;

        switch (name)
        {
            case "min-width":
                return TryParsePx(value, out var minW) && W >= minW;
            case "max-width":
                return TryParsePx(value, out var maxW) && W <= maxW;
            case "min-height":
                return TryParsePx(value, out var minH) && H >= minH;
            case "max-height":
                return TryParsePx(value, out var maxH) && H <= maxH;
            case "prefers-color-scheme":
                return value == "light";
            case "prefers-reduced-motion":
                return value == "no-preference";
            case "display-mode":
                return value == "browser";
            case "orientation":
                return value == "landscape"; // 1280 > 800
            default:
                return false;
        }
    }

    private static bool TryParsePx(string s, out int px)
    {
        px = 0;
        if (s.EndsWith("px"))
        {
            return int.TryParse(s.Substring(0, s.Length - 2).Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out px);
        }
        // rem / em — approximate as 16px per unit.
        if (s.EndsWith("rem") || s.EndsWith("em"))
        {
            var numPart = s.Substring(0, s.Length - (s.EndsWith("rem") ? 3 : 2)).Trim();
            if (double.TryParse(numPart,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var val))
            {
                px = (int)(val * 16);
                return true;
            }
        }
        // Plain number — treat as px.
        return int.TryParse(s.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out px);
    }

    private static JsFunction MakeObserverCtor(JsEngine engine, string name)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        proto.SetNonEnumerable("observe", new JsFunction("observe", (t, a) => JsValue.Undefined));
        proto.SetNonEnumerable("unobserve", new JsFunction("unobserve", (t, a) => JsValue.Undefined));
        proto.SetNonEnumerable("disconnect", new JsFunction("disconnect", (t, a) => JsValue.Undefined));
        proto.SetNonEnumerable("takeRecords", new JsFunction("takeRecords", (t, a) =>
            new JsArray { Prototype = engine.ArrayPrototype }));
        var ctor = new JsFunction(name, (thisVal, args) =>
        {
            var instance = new JsObject { Prototype = proto };
            return instance;
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        return ctor;
    }
}

/// <summary>
/// One entry in the <c>history</c> stack. Stores the
/// script-supplied state payload, title, and URL tuple
/// that <c>pushState</c> / <c>replaceState</c> accept.
/// </summary>
internal readonly struct JsHistoryEntry
{
    public object? State { get; }
    public string Title { get; }
    public string Url { get; }

    public JsHistoryEntry(object? state, string title, string url)
    {
        State = state;
        Title = title;
        Url = url;
    }
}

/// <summary>
/// Instance state for <c>localStorage</c> /
/// <c>sessionStorage</c>. A flat insertion-order-preserving
/// dictionary of string → string, with a <c>length</c>
/// getter intercepted in <see cref="Get"/>.
///
/// <para>
/// Phase 5a: each successful write fires <see cref="Changed"/>
/// so an embedder-supplied storage backend can persist the
/// updated snapshot. <see cref="JsStorageManager"/> subscribes
/// to this hook to route localStorage writes through the
/// configured <see cref="IStorageBackend"/>;
/// sessionStorage subscribes to nothing and stays transient.
/// </para>
/// </summary>
public sealed class JsStorage : JsObject
{
    public Dictionary<string, string> Items { get; } = new();

    /// <summary>Fired after <c>setItem</c> / <c>removeItem</c>
    /// (when the item existed) / <c>clear</c> (when there was
    /// something to clear). Read-only mutations — <c>getItem</c>,
    /// <c>key</c>, <c>length</c> — do not fire this event.</summary>
    public event Action? Changed;

    internal void NotifyChanged() => Changed?.Invoke();

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
        if (_engine.AttachedPageUrl is not null)
        {
            return _engine.AttachedPageUrl.ToString();
        }
        return "about:blank";
    }

    /// <summary>
    /// Called by <see cref="JsEngine.AttachDocument"/> when
    /// a new document is attached. Clears the explicit
    /// override so the next read picks up the real page URL.
    /// </summary>
    internal void OnPageLoaded(Uri? pageUrl)
    {
        _href = null;
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
