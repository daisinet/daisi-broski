using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Net;

namespace Daisi.Broski.Cli;

/// <summary>
/// Diagnostic: load a URL through the full Broski pipeline
/// and dump the PageScriptsResult plus a snapshot of the
/// post-run JS globals relevant to SPA frameworks (Blazor,
/// React, Vue, etc.). Unlike the <c>run</c> command (which
/// has its own inline loop), this goes through the shared
/// <see cref="PageScripts"/> code path so importmap
/// parsing, base-href resolution, ES-module dispatch, and
/// the synthetic DOMContentLoaded / load events are all
/// exercised — useful when debugging a site whose
/// bootstrap depends on those paths.
/// </summary>
internal static class ProbePageScripts
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0)
        {
            System.Console.Error.WriteLine("probe-scripts <url>");
            return 2;
        }
        var url = new System.Uri(args[0]);

        using var fetcher = new HttpFetcher(new HttpFetcherOptions());
        using var loader = new PageLoader(fetcher);
        var page = await loader.LoadAsync(url).ConfigureAwait(false);

        System.Console.Out.WriteLine(
            $"{(int)page.Status} {page.Status} {page.FinalUrl}");

        int moduleCount = 0, importMapCount = 0, classicCount = 0;
        foreach (var s in page.Document.QuerySelectorAll("script"))
        {
            var t = s.GetAttribute("type") ?? "";
            if (t == "module") moduleCount++;
            else if (t == "importmap") importMapCount++;
            else classicCount++;
        }
        System.Console.Out.WriteLine(
            $"scripts in DOM: {classicCount} classic, " +
            $"{moduleCount} module, {importMapCount} importmap");

        var engine = new Daisi.Broski.Engine.Js.JsEngine();
        engine.AttachDocument(page.Document, page.FinalUrl);
        // Install interceptors BEFORE page scripts so we
        // capture every fetch / import / WebSocket that
        // Blazor's auto-start path makes.
        engine.RunScript(@"
            window.__fetchLog = [];
            window.__importLog = [];
            window.__wsLog = [];
            window.__consoleErrors = [];
            window.__unhandledRejections = [];
            if (window.fetch) {
                var _of = window.fetch;
                window.fetch = function(u, opts) {
                    window.__fetchLog.push(String(u).substring(0, 120));
                    return _of.apply(this, arguments);
                };
            }
            if (window.$importModule) {
                var _oi = window.$importModule;
                window.$importModule = function(u) {
                    window.__importLog.push(String(u).substring(0, 120));
                    return _oi.apply(this, arguments);
                };
            }
            if (typeof WebSocket === 'function') {
                var _ow = window.WebSocket;
                window.WebSocket = function(u, p) {
                    window.__wsLog.push(String(u).substring(0, 120));
                    return new _ow(u, p);
                };
                window.WebSocket.prototype = _ow.prototype;
            }
            var _ce = console.error.bind(console);
            console.error = function() {
                try {
                    var parts = [];
                    for (var i = 0; i < arguments.length; i++) {
                        var a = arguments[i];
                        if (a instanceof Error) parts.push(a.message);
                        else parts.push(String(a).substring(0, 200));
                    }
                    window.__consoleErrors.push(parts.join(' '));
                } catch(_) {}
                _ce.apply(console, arguments);
            };
            window.addEventListener('unhandledrejection', function(e) {
                var r = e && e.reason;
                window.__unhandledRejections.push(r && r.message ? r.message : String(r));
            });
        ");
        var result = PageScripts.RunAllAgainst(engine, page.Document, page.FinalUrl, fetcher);
        // Drain deep — framework bootstrap chains are often
        // 10+ awaits deep (importmap resolution → fetch →
        // parse → evaluate → register → ...). One DrainEventLoop
        // ticks one microtask; pump many so the full chain
        // settles before we snapshot state.
        for (int i = 0; i < 30; i++) engine.DrainEventLoop();

        System.Console.Out.WriteLine(
            $"PageScripts: ran={result.Ran} errored={result.Errored} " +
            $"skipped-by-type={result.SkippedByType} " +
            $"fetch-errors={result.ExternalFetchErrors}");

        // Framework-detection surface — the globals we'd
        // expect present if a common SPA stack finished
        // bootstrapping.
        System.Console.Out.WriteLine();
        System.Console.Out.WriteLine("framework globals:");
        foreach (var key in new[] {
            "Blazor", "DotNet", "React", "Vue", "Angular",
            "htmx", "Alpine", "bootstrap", "customElements" })
        {
            var val = engine.Globals.TryGetValue(key, out var v) ? v : null;
            var shape = val is null
                ? "—"
                : val is Daisi.Broski.Engine.Js.JsFunction ? "function"
                : val is Daisi.Broski.Engine.Js.JsObject ? "object"
                : val.GetType().Name;
            System.Console.Out.WriteLine($"  {key}: {shape}");
        }

        // Simulate Blazor's own descriptor walker (copied
        // from minified blazor.web.js) to find out why
        // server-mode auto-start thinks there are no
        // descriptors.
        var blazorSim = engine.RunScript(@"
            (function() {
                var Mt = /^\s*Blazor:[^{]*(?<descriptor>.*)$/;
                class IT {
                    constructor(e) { this.childNodes = e; this.currentIndex = -1; this.length = e.length; }
                    next() {
                        this.currentIndex++;
                        if (this.currentIndex < this.length) {
                            this.currentElement = this.childNodes[this.currentIndex];
                            return true;
                        }
                        this.currentElement = void 0;
                        return false;
                    }
                }
                function Lt(e, t) {
                    var n = e.currentElement;
                    if (!n) return null;
                    if (n.nodeType !== 8) return null;
                    if (!n.textContent) return null;
                    var s = Mt.exec(n.textContent);
                    var a = s && s.groups && s.groups.descriptor;
                    if (!a) return null;
                    try {
                        var parsed = JSON.parse(a);
                        if (parsed.type !== t) return null;
                        return { ok: true, type: parsed.type, pos: n.textContent.substring(0, 30) };
                    } catch (err) {
                        return { err: err.message };
                    }
                }
                function Pt(e, t) {
                    var n = [];
                    var o = new IT(e.childNodes);
                    while (o.next() && o.currentElement) {
                        var r = Lt(o, t);
                        if (r) n.push(r);
                        else if (o.currentElement.hasChildNodes && o.currentElement.hasChildNodes()) {
                            var sub = Pt(o.currentElement, t);
                            for (var i = 0; i < sub.length; i++) n.push(sub[i]);
                        }
                    }
                    return n;
                }
                // Trace version — log each step of the walk.
                var trace = [];
                function PtTrace(e, t, depth) {
                    if (depth > 5) return [];
                    trace.push('enter '+depth+' tag='+(e.nodeName||e.tagName||'?')+' cn='+ (e.childNodes ? e.childNodes.length : '?'));
                    var n = [];
                    var o = new IT(e.childNodes);
                    while (o.next() && o.currentElement) {
                        var cur = o.currentElement;
                        trace.push('  d'+depth+' idx='+o.currentIndex+' nodeType='+cur.nodeType+' name='+(cur.nodeName||cur.tagName||'?')+ (cur.nodeType===8?' text='+ (cur.textContent||'').substring(0,30):''));
                        var r = Lt(o, t);
                        if (r) { trace.push('    MATCH: ' + JSON.stringify(r)); n.push(r); }
                        else if (cur.hasChildNodes && cur.hasChildNodes()) {
                            var sub = PtTrace(cur, t, depth+1);
                            for (var i = 0; i < sub.length; i++) n.push(sub[i]);
                        }
                    }
                    return n;
                }
                var server = PtTrace(document, 'server', 0);
                return JSON.stringify({ server_count: server.length, trace: trace.slice(0, 40) }, null, 2);
            })()
        ");
        System.Console.Out.WriteLine();
        System.Console.Out.WriteLine($"Blazor walker sim: {blazorSim}");

        // Inspect document.childNodes + its recursion
        var walkShape = engine.RunScript(@"
            (function() {
                var out = {};
                out.document_childNodes_len = document.childNodes ? document.childNodes.length : 'null';
                out.document_childNodes_first = document.childNodes && document.childNodes[0] ? document.childNodes[0].nodeName : null;
                var html = document.documentElement;
                out.html_hasChildNodes = html ? html.hasChildNodes() : 'no html';
                out.html_cn_len = html ? html.childNodes.length : 0;
                if (html) {
                    for (var i = 0; i < html.childNodes.length; i++) {
                        var child = html.childNodes[i];
                        if (child.nodeName && child.nodeName.toLowerCase() === 'head') {
                            out.head_cn_len = child.childNodes.length;
                            for (var j = 0; j < child.childNodes.length; j++) {
                                var hc = child.childNodes[j];
                                if (hc.nodeType === 8) {
                                    out.head_first_comment = (hc.textContent || '').substring(0, 50);
                                    break;
                                }
                            }
                            break;
                        }
                    }
                }
                return JSON.stringify(out, null, 2);
            })()
        ");
        System.Console.Out.WriteLine($"Walk shape check: {walkShape}");

        // Network activity captured by pre-run
        // interceptors.
        var fetchLog = engine.RunScript(@"JSON.stringify(window.__fetchLog)");
        var importLog = engine.RunScript(@"JSON.stringify(window.__importLog)");
        var wsLog = engine.RunScript(@"JSON.stringify(window.__wsLog)");
        System.Console.Out.WriteLine();
        System.Console.Out.WriteLine("== post-bootstrap network ==");
        System.Console.Out.WriteLine($"  fetches: {fetchLog}");
        System.Console.Out.WriteLine($"  dynamic imports: {importLog}");
        System.Console.Out.WriteLine($"  WebSocket opens: {wsLog}");
        var consoleErr = engine.RunScript(@"JSON.stringify(window.__consoleErrors)");
        var unhRej = engine.RunScript(@"JSON.stringify(window.__unhandledRejections)");
        System.Console.Out.WriteLine($"  console.errors: {consoleErr}");
        System.Console.Out.WriteLine($"  unhandled rejections: {unhRej}");

        // Deep Blazor introspection — reach into internals
        // to see what state the auto-start reached.
        var deepState = engine.RunScript(@"
            (function() {
                var out = {};
                try {
                    // Find ri (the pending-root-components tracker) by
                    // reaching into Blazor._internal — it's set on
                    // attachWebRendererInterop or similar. Try a
                    // function probe instead: directly walk for
                    // component descriptors via rootComponents.add
                    // invocations we can inspect.
                    out.addFn = typeof Blazor.rootComponents.add;
                    out.signalRCandidates = Object.keys(window).filter(function(k){return k.indexOf('signalR')>=0||k.indexOf('Ho')>=0;}).slice(0,5);
                    // Try to force a refresh via internal.
                    out.hasInternal = typeof Blazor._internal;
                    out.internalKeys = Object.keys(Blazor._internal || {}).slice(0,30);
                    out.runtimeKeys = Object.keys(Blazor.runtime || {}).slice(0,20);
                } catch (e) { out.err = e.message; }
                return JSON.stringify(out, null, 2);
            })()
        ");
        System.Console.Out.WriteLine();
        System.Console.Out.WriteLine("== deep Blazor state ==");
        System.Console.Out.WriteLine(deepState);

        if (result.Errors.Count > 0)
        {
            System.Console.Out.WriteLine();
            System.Console.Out.WriteLine("errors:");
            foreach (var e in result.Errors)
            {
                System.Console.Out.WriteLine(
                    $"  [#{e.Index}{(e.Deferred ? " defer" : "")}] {e.Message}");
                System.Console.Out.WriteLine($"    {e.Source}");
            }
        }

        return 0;
    }
}
