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
    public static PageScriptsResult RunAll(LoadedPage page, HttpFetcher? scriptFetcher = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var doc = page.Document;
        var engine = new JsEngine();
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

        // Two passes: blocking first, then defer (browser order).
        var deferred = new List<Element>();
        int idx = 0;
        foreach (var script in document.QuerySelectorAll("script"))
        {
            idx++;
            totalScripts++;

            var type = script.GetAttribute("type") ?? "text/javascript";
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
        string source;
        bool isExternal = script.HasAttribute("src");
        string label;

        if (isExternal)
        {
            var src = script.GetAttribute("src")!;
            Uri uri;
            try { uri = new Uri(pageUrl, src); }
            catch
            {
                errors.Add(new PageScriptError(sourceIndex, deferred, src,
                    "URL resolution failed"));
                return (0, 0, 0, 1);
            }
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
            label = src;
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

        try
        {
            engine.ExecutingScript = script;
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
