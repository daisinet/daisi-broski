using System.Text.Json;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Net;

namespace Daisi.Broski.Engine;

/// <summary>
/// Shared script-execution pipeline used by every consumer that
/// wants the post-JS DOM (CLI <c>run</c> command, the Surfer's
/// <c>SkimSession</c>, <see cref="Broski.LoadAsync"/> when
/// scripting is enabled). Lifts the loop out of the CLI so the
/// "load page → run scripts → query DOM" flow has a single
/// implementation that everyone shares.
///
/// <para>
/// Execution order matches the browser's blocking-script model:
/// every non-async, non-defer <c>&lt;script&gt;</c> runs in source
/// order to completion before the next one starts; defer scripts
/// run in source order after the blocking pass; async scripts are
/// skipped (out-of-order scheduling isn't modeled). Per-script
/// failures are captured into <see cref="PageScriptsResult.Errors"/>
/// and don't abort subsequent scripts.
/// </para>
/// </summary>
public static class PageScripts
{
    /// <summary>Execute every blocking + deferred script in
    /// <paramref name="page"/>'s document against a fresh
    /// <see cref="JsEngine"/>. Returns counters + per-script error
    /// messages so callers (CLI in particular) can report on what
    /// happened.</summary>
    /// <param name="page">The parsed page to drive.</param>
    /// <param name="scriptFetcher">Optional fetcher for external
    /// scripts. When null, a fresh <see cref="HttpFetcher"/> with
    /// default options is used per call. Pass an existing fetcher
    /// to share its cookie jar with the page fetch.</param>
    /// <param name="storagePath">Optional root directory for
    /// persisted <c>localStorage</c>. When <c>null</c> (default),
    /// <c>localStorage</c> is transient. Pass
    /// <see cref="Broski.DefaultStoragePath"/> for a sensible
    /// per-user location.</param>
    public static PageScriptsResult RunAll(
        LoadedPage page,
        HttpFetcher? scriptFetcher = null,
        string? storagePath = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var doc = page.Document;
        var engine = new JsEngine();
        // Install the storage backend *before* AttachDocument so
        // the initial origin-load hits the file-backed backend
        // instead of the transient default.
        if (!string.IsNullOrEmpty(storagePath))
        {
            engine.SetStorageBackend(new FileStorageBackend(storagePath));
            engine.IndexedDbBackend = new FileIndexedDbBackend(
                Path.Combine(storagePath, "indexeddb"));
        }
        engine.AttachDocument(doc, page.FinalUrl);

        bool ownsFetcher = scriptFetcher is null;
        var fetcher = scriptFetcher ?? new HttpFetcher(new HttpFetcherOptions());
        try
        {
            return RunAllAgainst(engine, doc, page.FinalUrl, fetcher);
        }
        finally
        {
            if (ownsFetcher) fetcher.Dispose();
        }
    }

    /// <summary>Lower-level entry point: run scripts against a
    /// caller-supplied engine + document. Use this when you need to
    /// pre-install custom host objects on the engine before scripts
    /// see them (CLI does this to attach <c>document.currentScript</c>
    /// state).</summary>
    public static PageScriptsResult RunAllAgainst(
        JsEngine engine,
        Document document,
        Uri pageUrl,
        HttpFetcher scriptFetcher)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageUrl);
        ArgumentNullException.ThrowIfNull(scriptFetcher);

        int ranCount = 0;
        int errorCount = 0;
        int inlineCount = 0;
        int externalCount = 0;
        int externalFetchErrors = 0;
        int skippedTypeCount = 0;
        int totalScripts = 0;
        var errors = new List<PageScriptError>();

        // Pre-scan for <script type="importmap"> and build
        // the specifier → resolved URL table so any later
        // `import(specifier)` can translate via the map
        // before fetching. Blazor / Vite / Rollup builds emit
        // these, and without the table the fingerprinted
        // bundle names never resolve.
        var importMap = BuildImportMap(document, pageUrl);
        InstallModuleResolver(engine, scriptFetcher, pageUrl, document, importMap);

        // Two passes: blocking first, then defer (browser order).
        var deferred = new List<Element>();
        int idx = 0;
        foreach (var script in document.QuerySelectorAll("script"))
        {
            idx++;
            totalScripts++;

            var type = script.GetAttribute("type") ?? "text/javascript";

            // Importmap scripts: data-only. Already consumed
            // during the pre-scan above — skip execution.
            if (type == "importmap")
            {
                continue;
            }

            // Module scripts: route through JsEngine.ImportModule
            // so ES module import/export syntax works end-to-end.
            // Modules are deferred by spec: they all execute
            // after the document is parsed, never blocking it.
            // For our synchronous runner, that ordering is
            // approximate — we run them after the blocking
            // pass but before explicit defer scripts, which is
            // fine in practice because modules don't race with
            // inline classic scripts.
            if (type == "module")
            {
                externalCount++;
                deferred.Add(script);
                continue;
            }

            if (type is not ("" or "text/javascript" or "application/javascript"))
            {
                skippedTypeCount++;
                continue;
            }
            if (script.HasAttribute("async"))
            {
                externalCount++;
                continue;
            }
            if (script.HasAttribute("defer"))
            {
                externalCount++;
                deferred.Add(script);
                continue;
            }

            var (rc, ec, ifc, ifx) = RunOne(engine, scriptFetcher, pageUrl, script, idx, deferred: false, errors);
            ranCount += rc;
            errorCount += ec;
            if (script.HasAttribute("src")) externalCount++; else inlineCount++;
            externalFetchErrors += ifx;
        }

        foreach (var script in deferred)
        {
            idx = 0; // index already accounted for in the blocking pass
            var (rc, ec, _, ifx) = RunOne(engine, scriptFetcher, pageUrl, script, deferIndex(script), deferred: true, errors);
            ranCount += rc;
            errorCount += ec;
            externalFetchErrors += ifx;
        }

        // Fire the browser's ready-state events. Frameworks
        // like Blazor, htmx, and Alpine gate their startup
        // on DOMContentLoaded — without this synthesis they
        // register handlers and then the bootstrap never
        // triggers. We dispatch on the document and on
        // window (via the global fallback) then drain the
        // event loop so any handlers that queued
        // microtasks (dynamic imports, promise chains) can
        // make progress before the caller inspects the DOM.
        //
        // After DOMContentLoaded, we also call Blazor.start()
        // if Blazor's runtime is present — Blazor Web's
        // auto-start hook registers late enough that the
        // synthetic DOMContentLoaded above can miss it; an
        // explicit call is idempotent and unblocks the
        // circuit bootstrap path that imports
        // blazor.server.js / blazor.webassembly.js.
        try
        {
            engine.RunScript(@"
                (function() {
                    try {
                        var e = new Event('DOMContentLoaded', { bubbles: true, cancelable: false });
                        document.dispatchEvent(e);
                    } catch (err) { /* best-effort */ }
                    try {
                        var le = new Event('load');
                        if (typeof window !== 'undefined' && typeof window.dispatchEvent === 'function') {
                            window.dispatchEvent(le);
                        }
                    } catch (err) { /* best-effort */ }
                    try {
                        if (typeof Blazor !== 'undefined' && typeof Blazor.start === 'function') {
                            Blazor.start();
                        }
                    } catch (err) { /* best-effort */ }
                })();
            ");
            engine.DrainEventLoop();
        }
        catch
        {
            // Don't let a listener's error block the page
            // script report — it's telemetry, not a contract.
        }

        return new PageScriptsResult
        {
            TotalScripts = totalScripts,
            InlineScripts = inlineCount,
            ExternalScripts = externalCount,
            SkippedByType = skippedTypeCount,
            ExternalFetchErrors = externalFetchErrors,
            Ran = ranCount,
            Errored = errorCount,
            Errors = errors,
        };

        // The defer pass needs to refer back to the script's
        // original document position for error reporting; cheap to
        // recompute since defers are rare.
        int deferIndex(Element script)
        {
            int n = 0;
            foreach (var s in document.QuerySelectorAll("script"))
            {
                n++;
                if (ReferenceEquals(s, script)) return n;
            }
            return -1;
        }
    }

    private static (int Ran, int Errored, int InlineFlag, int FetchError) RunOne(
        JsEngine engine,
        HttpFetcher fetcher,
        Uri pageUrl,
        Element script,
        int sourceIndex,
        bool deferred,
        List<PageScriptError> errors)
    {
        string source = "";
        bool isExternal = script.HasAttribute("src");
        bool isModuleScript = (script.GetAttribute("type") ?? "") == "module";
        string label;

        if (isExternal)
        {
            var src = script.GetAttribute("src")!;
            label = src;
            // Module scripts route through engine.ImportModule
            // which fetches via the module resolver — don't
            // pre-fetch here, otherwise the importmap-rewritten
            // URL would differ from the one we fetched, and
            // we'd hit the non-fingerprinted URL needlessly
            // (and might get the wrong bytes back when the
            // server serves different content at that path).
            if (isModuleScript)
            {
                // Resolve once just to validate the src can be
                // turned into a URL — ImportModule will do the
                // actual resolution including importmap
                // lookup.
                var resolved = Dom.UrlResolver.Resolve(script.OwnerDocument, pageUrl, src);
                if (resolved is null)
                {
                    errors.Add(new PageScriptError(sourceIndex, deferred, src,
                        "URL resolution failed"));
                    return (0, 0, 0, 1);
                }
            }
            else
            {
                // Classic external script — fetch synchronously.
                // <base href> is honored via UrlResolver, fixes
                // the Blazor-breaker where relative script srcs
                // against a page on a sub-path pivoted on the
                // wrong directory.
                var resolved = Dom.UrlResolver.Resolve(script.OwnerDocument, pageUrl, src);
                if (resolved is null)
                {
                    errors.Add(new PageScriptError(sourceIndex, deferred, src,
                        "URL resolution failed"));
                    return (0, 0, 0, 1);
                }
                Uri uri = resolved;
                try
                {
                    var result = fetcher.FetchAsync(uri).GetAwaiter().GetResult();
                    source = System.Text.Encoding.UTF8.GetString(result.Body);
                }
                catch (Exception fetchEx)
                {
                    errors.Add(new PageScriptError(sourceIndex, deferred, src,
                        "fetch failed: " + fetchEx.Message));
                    return (0, 0, 0, 1);
                }
            }
        }
        else
        {
            source = script.TextContent;
            if (string.IsNullOrWhiteSpace(source))
            {
                return (0, 0, 0, 0);
            }
            label = "<inline>";
        }

        // type="module" goes through the module pipeline —
        // parse/evaluate with ImportDeclaration handling,
        // run in an isolated scope, stash exports in the
        // engine's module cache keyed by URL so dynamic
        // import() sees it.
        bool isModule = (script.GetAttribute("type") ?? "") == "module";
        try
        {
            engine.ExecutingScript = script;
            if (isModule && isExternal)
            {
                // Route through the engine's ImportModule
                // pipeline. ImportMap + fetch resolution is
                // wired in InstallModuleResolver. Pass null
                // as the referrer so the resolver goes
                // through EffectiveBase(<base href> + pageUrl)
                // — top-level module srcs in the HTML have
                // document-level base semantics, not page-
                // URL-relative.
                var src = script.GetAttribute("src")!;
                engine.ImportModule(src, referrerUrl: null);
                return (1, 0, 0, 0);
            }
            // Inline modules are rare enough that running
            // them as classic scripts covers the common case
            // (bundlers rarely inline modules; when they do
            // it's for nodemon-style bootstraps without
            // import/export). A future slice can add inline-
            // module support by synthesising a fake URL +
            // registering in the resolver cache.
            engine.RunScript(source);
            return (1, 0, 0, 0);
        }
        catch (Exception ex)
        {
            errors.Add(new PageScriptError(sourceIndex, deferred, label, ex.Message));
            return (0, 1, 0, 0);
        }
        finally
        {
            engine.ExecutingScript = null;
        }
    }

    /// <summary>Scan the document for
    /// <c>&lt;script type="importmap"&gt;</c> scripts, parse
    /// their JSON contents, and build a specifier → resolved
    /// URL table. Both exact matches (<c>"./foo.js"</c>) and
    /// prefix matches (<c>"./foo/"</c> → entire subtree) are
    /// kept in the same dictionary — the lookup path picks
    /// the longest matching prefix at resolve time.</summary>
    private static Dictionary<string, string> BuildImportMap(
        Document document, Uri pageUrl)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in document.QuerySelectorAll("script"))
        {
            var type = script.GetAttribute("type");
            if (type != "importmap") continue;
            var text = script.TextContent;
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                using var parsed = JsonDocument.Parse(text);
                if (!parsed.RootElement.TryGetProperty("imports", out var imports))
                    continue;
                if (imports.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in imports.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var val = prop.Value.GetString();
                    if (string.IsNullOrEmpty(val)) continue;
                    // Resolve the target URL so subsequent
                    // lookups don't have to. Honors
                    // <base href> via UrlResolver.
                    var resolved = UrlResolver.Resolve(document, pageUrl, val);
                    if (resolved is null) continue;
                    map[prop.Name] = resolved.AbsoluteUri;
                }
            }
            catch (JsonException)
            {
                // Malformed importmap: spec says it's a warning
                // and the map is empty. Silently skip.
            }
        }
        return map;
    }

    /// <summary>Wire <c>engine.ModuleResolver</c> to turn a
    /// specifier into a (url, source) pair: look the
    /// specifier up in the import map, fall back to URL
    /// resolution against the referrer / page URL, fetch
    /// synchronously through the caller's
    /// <see cref="HttpFetcher"/>. This is how dynamic
    /// <c>import()</c> and <c>type="module"</c> src resolution
    /// both get their bytes — one shared callback, so the
    /// importmap is honored uniformly.</summary>
    private static void InstallModuleResolver(
        JsEngine engine,
        HttpFetcher scriptFetcher,
        Uri pageUrl,
        Document document,
        Dictionary<string, string> importMap)
    {
        engine.ModuleResolver = (specifier, referrerUrl) =>
        {
            if (string.IsNullOrEmpty(specifier)) return null;

            // 1) Import-map lookup. Try exact match first,
            //    then longest prefix match per the HTML spec.
            string rewritten = specifier;
            if (importMap.TryGetValue(specifier, out var exact))
            {
                rewritten = exact;
            }
            else
            {
                string? bestKey = null;
                foreach (var key in importMap.Keys)
                {
                    if (!key.EndsWith('/')) continue;
                    if (!specifier.StartsWith(key, StringComparison.Ordinal)) continue;
                    if (bestKey is null || key.Length > bestKey.Length) bestKey = key;
                }
                if (bestKey is not null)
                {
                    rewritten = importMap[bestKey]
                        + specifier.Substring(bestKey.Length);
                }
            }

            // 2) Resolve to absolute URL. Referrer-based
            //    resolution for relative specifiers keeps
            //    chained imports relative to the importing
            //    module's own URL; top-level imports use the
            //    document's effective base URL (page URL
            //    combined with <base href>), matching how
            //    classic script src lookup works. Without the
            //    base-href honor, Blazor's module srcs pivot
            //    on the current request path and 404.
            Uri baseUri;
            if (!string.IsNullOrEmpty(referrerUrl)
                && Uri.TryCreate(referrerUrl, UriKind.Absolute, out var ru))
            {
                baseUri = ru;
            }
            else
            {
                baseUri = UrlResolver.EffectiveBase(document, pageUrl);
            }
            if (!Uri.TryCreate(baseUri, rewritten, out var moduleUri))
            {
                return null;
            }
            if (moduleUri.Scheme is not ("http" or "https")) return null;

            // 3) Fetch synchronously. The module evaluator
            //    runs bottom-up so blocking here is fine —
            //    modules get evaluated sequentially.
            try
            {
                var result = scriptFetcher.FetchAsync(moduleUri).GetAwaiter().GetResult();
                if ((int)result.Status < 200 || (int)result.Status >= 300)
                {
                    return null;
                }
                var source = System.Text.Encoding.UTF8.GetString(result.Body);
                return new ResolvedModule(moduleUri.AbsoluteUri, source);
            }
            catch
            {
                return null;
            }
        };
    }
}

/// <summary>Result of <see cref="PageScripts.RunAll"/> — counters
/// + per-script errors. Callers report or log the values; the
/// engine does not surface them anywhere automatically.</summary>
public sealed class PageScriptsResult
{
    public required int TotalScripts { get; init; }
    public required int InlineScripts { get; init; }
    public required int ExternalScripts { get; init; }
    public required int SkippedByType { get; init; }
    public required int ExternalFetchErrors { get; init; }
    public required int Ran { get; init; }
    public required int Errored { get; init; }
    public required IReadOnlyList<PageScriptError> Errors { get; init; }
}

/// <summary>One error captured during <see cref="PageScripts.RunAll"/>.
/// <paramref name="Source"/> is either the external URL or the
/// literal <c>"&lt;inline&gt;"</c> for inline scripts.</summary>
public readonly record struct PageScriptError(
    int Index,
    bool Deferred,
    string Source,
    string Message);
