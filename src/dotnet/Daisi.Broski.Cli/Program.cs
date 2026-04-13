using Daisi.Broski;
using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Ipc;

namespace Daisi.Broski.Cli;

/// <summary>
/// Command-line driver for the phase-1 engine. Usage:
///
/// <code>
///   daisi-broski fetch &lt;url&gt; [options]
///
///   Options:
///     --select &lt;css&gt;   CSS selector to apply to the parsed document.
///                       When set, only the selected elements' text content
///                       is printed (one element per line).
///     --html            Print the decoded HTML body instead of a summary.
///     --ua &lt;string&gt;    Override the User-Agent header.
///     --max-redirects N Max redirects to follow (default 20).
///     --no-sandbox      Run the engine in-process instead of spawning a
///                       Daisi.Broski.Sandbox.exe child. Use only for
///                       trusted URLs — the parser, DOM, and selector
///                       engine run in the host process with no memory
///                       or resource limits. Not available as the default
///                       on non-Windows (where the sandbox is also
///                       unavailable until phase 5).
/// </code>
///
/// By default the CLI spawns a <see cref="Daisi.Broski.Sandbox"/> child
/// process for every fetch. The child runs under a Win32 Job Object with
/// a 256 MiB memory cap, kill-on-close, die-on-unhandled-exception, and
/// UI restrictions — see architecture.md §5.8. This means pointing
/// <c>daisi-broski fetch</c> at a hostile page is kernel-bounded.
///
/// Exit codes:
///   0  success
///   1  transport / fetch failure
///   2  sandbox spawn / IPC failure
///   3  usage error
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 3 : 0;
        }

        return args[0] switch
        {
            "fetch" => await FetchCommand(args[1..]),
            "run" => await RunCommand(args[1..]),
            "screenshot" => await ScreenshotCommand(args[1..]),
            _ => UsageError($"Unknown command '{args[0]}'"),
        };
    }

    // -------------------------------------------------------------------
    // run — fetch a page, parse it, attach to the JS engine, evaluate
    //       every inline <script>, and print a before/after summary.
    //       No sandboxing in this command: the goal is to exercise the
    //       engine directly so it's easy to iterate on.
    // -------------------------------------------------------------------

    private static async Task<int> RunCommand(string[] args)
    {
        string? urlArg = null;
        string? select = null;
        string? userAgent = null;
        int maxRedirects = 20;
        bool quiet = false;
        bool noSandbox = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--select":
                    if (i + 1 >= args.Length) return UsageError("--select requires a value");
                    select = args[++i];
                    break;
                case "--ua":
                    if (i + 1 >= args.Length) return UsageError("--ua requires a value");
                    userAgent = args[++i];
                    break;
                case "--max-redirects":
                    if (i + 1 >= args.Length) return UsageError("--max-redirects requires a value");
                    if (!int.TryParse(args[++i], out maxRedirects) || maxRedirects < 0)
                        return UsageError("--max-redirects must be a non-negative integer");
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--no-sandbox":
                    noSandbox = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                        return UsageError($"Unknown option '{a}'");
                    if (urlArg is not null)
                        return UsageError("Multiple positional URLs passed");
                    urlArg = a;
                    break;
            }
        }

        if (urlArg is null) return UsageError("run requires a URL");
        if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var url) ||
            (url.Scheme != "http" && url.Scheme != "https"))
        {
            return UsageError($"'{urlArg}' is not an absolute http(s) URL");
        }

        // Default to the sandboxed path — the JS engine now runs in
        // the child process, so `run` gets the same kernel-enforced
        // memory cap / UI lockdown that `fetch` already has.
        // --no-sandbox keeps the old in-process path, which is the
        // one with detailed per-script error reporting.
        bool useSandbox = !noSandbox && OperatingSystem.IsWindows();
        if (!noSandbox && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "daisi-broski: sandbox mode is Windows-only; running in-process.");
        }
        if (useSandbox && OperatingSystem.IsWindows())
        {
            return await RunInSandbox(url, select, userAgent, maxRedirects, quiet);
        }

        var options = new HttpFetcherOptions
        {
            MaxRedirects = maxRedirects,
            UserAgent = userAgent ?? new HttpFetcherOptions().UserAgent,
        };

        try
        {
            using var loader = new PageLoader(options);
            var page = await loader.LoadAsync(url);

            Console.Error.WriteLine(
                $"{(int)page.Status} {page.Status} {page.FinalUrl} " +
                $"({page.Body.Length} bytes, {page.Encoding.WebName})");

            var doc = page.Document;

            // Snapshot the pre-JS DOM stats so we can show the delta.
            int preElements = doc.DescendantElements().Count();
            int preLinks = doc.QuerySelectorAll("a[href]").Count;
            int preScripts = doc.QuerySelectorAll("script").Count;
            int preTextBytes = doc.DocumentElement?.TextContent.Length ?? 0;

            // Wire the engine to the parsed document.
            var engine = new JsEngine();
            // Pass the final (post-redirect) URL so scripts
            // that read location.href or do
            // `new URL('.', location.href)` see a real URL
            // instead of the about:blank placeholder.
            engine.AttachDocument(doc, page.FinalUrl);

            // A shared fetcher for external script loading,
            // reusing the same options (UA, redirects) as the
            // page fetch so the server sees consistent headers.
            using var scriptFetcher = new HttpFetcher(options);

            // Execute every <script> in document order — both
            // inline and external (src="..."). This matches the
            // browser's blocking-script execution model: each
            // script runs to completion before the next one starts,
            // and scripts see the DOM as it was after all
            // preceding scripts finished.
            //
            // We skip:
            //   - type="module" (ES module loading deferred)
            //   - type="application/json" / "application/ld+json"
            //   - async / defer (would need out-of-order scheduling)
            int ranCount = 0;
            int errorCount = 0;
            int inlineCount = 0;
            int externalCount = 0;
            int externalFetchErrors = 0;
            int skippedTypeCount = 0;
            var scriptErrors = new List<string>();
            // Deferred scripts: collected during the blocking pass
            // and executed after all blocking scripts finish, in
            // document order. Matches the browser's defer semantics.
            var deferredScripts = new List<(Daisi.Broski.Engine.Dom.Element script, int idx)>();
            int scriptIdx = 0;
            foreach (var script in doc.QuerySelectorAll("script"))
            {
                scriptIdx++;
                var type = script.GetAttribute("type") ?? "text/javascript";
                // Skip modules / JSON-LD / application-specific types.
                if (type is not ("" or "text/javascript" or "application/javascript"))
                {
                    skippedTypeCount++;
                    continue;
                }
                // async scripts load out of order — skip those.
                if (script.HasAttribute("async"))
                {
                    externalCount++;
                    continue;
                }
                // defer scripts run in order AFTER all blocking
                // scripts. Collect them for a second pass.
                if (script.HasAttribute("defer"))
                {
                    externalCount++;
                    deferredScripts.Add((script, scriptIdx));
                    continue;
                }

                string source;
                string label;
                bool isExternal = script.HasAttribute("src");
                if (isExternal)
                {
                    externalCount++;
                    var src = script.GetAttribute("src")!;
                    // Resolve relative URLs against the page.
                    Uri scriptUri;
                    try
                    {
                        scriptUri = new Uri(page.FinalUrl, src);
                    }
                    catch
                    {
                        externalFetchErrors++;
                        continue;
                    }
                    // Fetch the script source.
                    try
                    {
                        var result = scriptFetcher.FetchAsync(scriptUri).GetAwaiter().GetResult();
                        source = System.Text.Encoding.UTF8.GetString(result.Body);
                    }
                    catch (Exception fetchEx)
                    {
                        externalFetchErrors++;
                        if (!quiet)
                        {
                            scriptErrors.Add(
                                $"[script #{scriptIdx} src={src}] fetch failed: {fetchEx.Message}");
                        }
                        continue;
                    }
                    label = $"src={src} ({source.Length} bytes)";
                }
                else
                {
                    source = script.TextContent;
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    inlineCount++;
                    label = $"inline ({source.Length} bytes)";
                }

                try
                {
                    // Track the currently-executing script
                    // element so `document.currentScript`
                    // resolves during the run.
                    engine.ExecutingScript = script;
                    engine.RunScript(source);
                    ranCount++;
                }
                catch (Daisi.Broski.Engine.Js.JsRuntimeException jsEx)
                {
                    errorCount++;
                    string detail = UnpackJsError(jsEx);
                    scriptErrors.Add(
                        $"[script #{scriptIdx}] {detail}\n" +
                        $"    {label}: {Truncate(source, 400)}");
                }
                catch (Daisi.Broski.Engine.Js.JsParseException parseEx)
                {
                    errorCount++;
                    // Show the source around the error offset so
                    // we can see exactly what syntax tripped up.
                    string ctx = "";
                    if (parseEx.Offset >= 0 && parseEx.Offset < source.Length)
                    {
                        int from = Math.Max(0, parseEx.Offset - 40);
                        int to = Math.Min(source.Length, parseEx.Offset + 60);
                        ctx = $"\n    context: ...{source[from..to].Replace('\n', ' ')}...";
                    }
                    scriptErrors.Add(
                        $"[script #{scriptIdx}] {parseEx.GetType().Name}: {parseEx.Message} (offset {parseEx.Offset}){ctx}\n" +
                        $"    {label}");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    scriptErrors.Add(
                        $"[script #{scriptIdx}] {ex.GetType().Name}: {ex.Message}\n" +
                        $"    {label}: {Truncate(source, 400)}");
                }
                finally
                {
                    engine.ExecutingScript = null;
                }
            }

            // Second pass: deferred scripts, in the order
            // they appeared in the document. These run after
            // the document is fully parsed + all blocking
            // scripts have completed, matching browser semantics.
            foreach (var (deferScript, deferIdx) in deferredScripts)
            {
                var src = deferScript.GetAttribute("src");
                if (string.IsNullOrEmpty(src)) continue;
                Uri deferUri;
                try { deferUri = new Uri(page.FinalUrl, src); }
                catch { externalFetchErrors++; continue; }
                string deferSource;
                try
                {
                    var result = scriptFetcher.FetchAsync(deferUri).GetAwaiter().GetResult();
                    deferSource = System.Text.Encoding.UTF8.GetString(result.Body);
                }
                catch (Exception fetchEx)
                {
                    externalFetchErrors++;
                    if (!quiet)
                    {
                        scriptErrors.Add(
                            $"[script #{deferIdx} defer src={src}] fetch failed: {fetchEx.Message}");
                    }
                    continue;
                }
                try
                {
                    engine.ExecutingScript = deferScript;
                    engine.RunScript(deferSource);
                    ranCount++;
                }
                catch (Daisi.Broski.Engine.Js.JsRuntimeException jsEx)
                {
                    errorCount++;
                    string detail = UnpackJsError(jsEx);
                    scriptErrors.Add(
                        $"[script #{deferIdx} defer] {detail}\n" +
                        $"    src={src} ({deferSource.Length} bytes): {Truncate(deferSource, 400)}");
                }
                catch (Daisi.Broski.Engine.Js.JsParseException parseEx)
                {
                    errorCount++;
                    string ctx = "";
                    if (parseEx.Offset >= 0 && parseEx.Offset < deferSource.Length)
                    {
                        int from = Math.Max(0, parseEx.Offset - 40);
                        int to = Math.Min(deferSource.Length, parseEx.Offset + 60);
                        ctx = $"\n    context: ...{deferSource[from..to].Replace('\n', ' ')}...";
                    }
                    scriptErrors.Add(
                        $"[script #{deferIdx} defer] {parseEx.Message} (offset {parseEx.Offset}){ctx}\n" +
                        $"    src={src}");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    scriptErrors.Add(
                        $"[script #{deferIdx} defer] {ex.GetType().Name}: {ex.Message}\n" +
                        $"    src={src} ({deferSource.Length} bytes): {Truncate(deferSource, 400)}");
                }
                finally
                {
                    engine.ExecutingScript = null;
                }
            }

            // Post-script DOM stats + diff.
            int postElements = doc.DescendantElements().Count();
            int postLinks = doc.QuerySelectorAll("a[href]").Count;
            int postScripts = doc.QuerySelectorAll("script").Count;
            int postTextBytes = doc.DocumentElement?.TextContent.Length ?? 0;

            var title = doc.QuerySelector("title")?.TextContent ?? "(no title)";
            Console.Out.WriteLine($"title: {title}");
            int totalRunnable = inlineCount + externalCount;
            Console.Out.WriteLine(
                $"scripts:     {preScripts} total (" +
                $"{inlineCount} inline, {externalCount} external, {skippedTypeCount} other type)");
            Console.Out.WriteLine(
                $"executed:    {ranCount}/{totalRunnable}" +
                (errorCount > 0 ? $" ({errorCount} errored)" : "") +
                (externalFetchErrors > 0 ? $" ({externalFetchErrors} fetch failed)" : ""));
            Console.Out.WriteLine(
                $"elements:    {preElements} → {postElements} " +
                $"({Delta(postElements - preElements)})");
            Console.Out.WriteLine(
                $"a[href]:     {preLinks} → {postLinks} " +
                $"({Delta(postLinks - preLinks)})");
            Console.Out.WriteLine(
                $"text bytes:  {preTextBytes} → {postTextBytes} " +
                $"({Delta(postTextBytes - preTextBytes)})");

            if (engine.ConsoleOutput.Length > 0 && !quiet)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("--- console output ---");
                Console.Out.Write(engine.ConsoleOutput.ToString());
                if (engine.ConsoleOutput[engine.ConsoleOutput.Length - 1] != '\n')
                {
                    Console.Out.WriteLine();
                }
            }

            if (errorCount > 0 && !quiet)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("--- script errors ---");
                foreach (var e in scriptErrors)
                {
                    Console.Out.WriteLine(e);
                }
            }

            if (select is not null)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"--- select '{select}' after JS ---");
                foreach (var m in doc.QuerySelectorAll(select))
                {
                    Console.Out.WriteLine(NormalizeWhitespace(m.TextContent));
                }
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"fetch error: {ex.Message}");
            return 1;
        }
        catch (HttpFetcherException ex)
        {
            Console.Error.WriteLine($"fetch error: {ex.Message}");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("fetch timed out");
            return 1;
        }
    }

    private static string Delta(int d) =>
        d > 0 ? $"+{d}" : d.ToString();

    /// <summary>
    /// Collapse whitespace runs and trim the source to at
    /// most <paramref name="maxLen"/> characters so we can
    /// print a readable single-line snippet for failure
    /// diagnostics. Long scripts get ellipsised with a
    /// <c>...</c> so the caller can see the beginning.
    /// </summary>
    private static string Truncate(string s, int maxLen)
    {
        var compact = NormalizeWhitespace(s);
        return compact.Length <= maxLen
            ? compact
            : compact.Substring(0, maxLen) + "...";
    }

    /// <summary>
    /// Walk the <see cref="Daisi.Broski.Engine.Js.JsRuntimeException"/>
    /// and produce a readable one-line description. When the thrown
    /// value is a normal Error-shaped object, we pull <c>name</c> +
    /// <c>message</c> off it — otherwise we fall back to the string
    /// coercion so primitive throws still print something useful.
    /// </summary>
    private static string UnpackJsError(Daisi.Broski.Engine.Js.JsRuntimeException ex)
    {
        if (ex.JsValue is Daisi.Broski.Engine.Js.JsObject obj)
        {
            var nameVal = obj.Get("name");
            var msgVal = obj.Get("message");
            var name = nameVal is string ns ? ns : "Error";
            var msg = msgVal is string ms ? ms : "";
            return string.IsNullOrEmpty(msg) ? name : $"{name}: {msg}";
        }
        if (ex.JsValue is string s) return s;
        return ex.Message;
    }

    private static async Task<int> FetchCommand(string[] args)
    {
        string? urlArg = null;
        string? select = null;
        string? userAgent = null;
        bool html = false;
        bool noSandbox = false;
        int maxRedirects = 20;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--select":
                    if (i + 1 >= args.Length) return UsageError("--select requires a value");
                    select = args[++i];
                    break;
                case "--ua":
                    if (i + 1 >= args.Length) return UsageError("--ua requires a value");
                    userAgent = args[++i];
                    break;
                case "--max-redirects":
                    if (i + 1 >= args.Length) return UsageError("--max-redirects requires a value");
                    if (!int.TryParse(args[++i], out maxRedirects) || maxRedirects < 0)
                        return UsageError("--max-redirects must be a non-negative integer");
                    break;
                case "--html":
                    html = true;
                    break;
                case "--no-sandbox":
                    noSandbox = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                        return UsageError($"Unknown option '{a}'");
                    if (urlArg is not null)
                        return UsageError("Multiple positional URLs passed");
                    urlArg = a;
                    break;
            }
        }

        if (urlArg is null) return UsageError("fetch requires a URL");

        if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var url) ||
            (url.Scheme != "http" && url.Scheme != "https"))
        {
            return UsageError($"'{urlArg}' is not an absolute http(s) URL");
        }

        // Decide execution mode. The default is sandboxed — in-process
        // execution only fires on --no-sandbox, or when the platform
        // doesn't support the Windows sandbox yet.
        bool useSandbox = !noSandbox && OperatingSystem.IsWindows();
        if (!noSandbox && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "daisi-broski: sandbox mode is Windows-only (phase 4). " +
                "Running in-process; pass --no-sandbox to silence this warning. " +
                "Cross-platform sandboxing is tracked in docs/roadmap.md phase 5.");
        }

        // The explicit IsWindows() re-check is redundant (useSandbox already
        // implies it) but the analyzer can't see through the local variable.
        if (useSandbox && OperatingSystem.IsWindows())
        {
            return await FetchInSandbox(url, select, userAgent, maxRedirects, html);
        }
        return await FetchInProcess(url, select, userAgent, maxRedirects, html);
    }

    // -------- sandboxed path --------

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> FetchInSandbox(
        Uri url, string? select, string? userAgent, int maxRedirects, bool html)
    {
        try
        {
            await using var session = BrowserSession.Create();

            var nav = await session.NavigateAsync(
                url, userAgent, maxRedirects, includeHtml: html);

            WriteStatusLine(
                (int)nav.Status,
                System.Net.HttpStatusCode.OK.ToString() /* placeholder, see below */,
                nav.FinalUrl,
                nav.ByteCount,
                nav.Encoding);

            if (html)
            {
                Console.Out.Write(nav.Html ?? "");
                return 0;
            }

            if (select is not null)
            {
                var matches = await session.QuerySelectorAllAsync(select);
                foreach (var m in matches)
                {
                    Console.Out.WriteLine(NormalizeWhitespace(m.TextContent));
                }
                Console.Error.WriteLine($"{matches.Count} match(es)");
                return 0;
            }

            // Summary: title + element counts via selector queries.
            Console.Out.WriteLine($"title: {nav.Title ?? "(no title)"}");
            var links = await session.QuerySelectorAllAsync("a[href]");
            var images = await session.QuerySelectorAllAsync("img");
            var scripts = await session.QuerySelectorAllAsync("script");
            Console.Out.WriteLine($"links: {links.Count}");
            Console.Out.WriteLine($"images: {images.Count}");
            Console.Out.WriteLine($"scripts: {scripts.Count}");
            return 0;
        }
        catch (SandboxException ex)
        {
            Console.Error.WriteLine($"sandbox error: {ex.Message}");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            // Raised by SandboxLauncher when it can't find the child .exe.
            Console.Error.WriteLine($"sandbox error: {ex.Message}");
            return 2;
        }
    }

    // -------- sandboxed `run` --------

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunInSandbox(
        Uri url, string? select, string? userAgent, int maxRedirects, bool quiet)
    {
        try
        {
            await using var session = BrowserSession.Create();
            var resp = await session.RunAsync(
                url,
                scriptingEnabled: true,
                select: select,
                includeHtml: false,
                userAgent: userAgent,
                maxRedirects: maxRedirects);

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"{resp.Status} {resp.FinalUrl} " +
                    $"(scripts {resp.ScriptsRan}/{resp.ScriptsTotal}, " +
                    $"elements {resp.ElementCount})");
            }

            if (select is not null)
            {
                foreach (var m in resp.Matches)
                {
                    Console.Out.WriteLine(NormalizeWhitespace(m.TextContent));
                }
                if (!quiet)
                    Console.Error.WriteLine($"{resp.Matches.Count} match(es)");
                return 0;
            }

            Console.Out.WriteLine($"title: {resp.Title ?? "(no title)"}");
            Console.Out.WriteLine(
                $"scripts:     {resp.ScriptsTotal} total, " +
                $"{resp.ScriptsRan} ran, {resp.ScriptsErrored} errored");
            Console.Out.WriteLine($"elements:    {resp.ElementCount}");
            return 0;
        }
        catch (SandboxException ex)
        {
            Console.Error.WriteLine($"sandbox error: {ex.Message}");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"sandbox error: {ex.Message}");
            return 2;
        }
    }

    // -------- in-process path (legacy) --------

    private static async Task<int> FetchInProcess(
        Uri url, string? select, string? userAgent, int maxRedirects, bool html)
    {
        var options = new HttpFetcherOptions
        {
            MaxRedirects = maxRedirects,
            UserAgent = userAgent ?? new HttpFetcherOptions().UserAgent,
        };

        try
        {
            using var loader = new PageLoader(options);
            var page = await loader.LoadAsync(url);

            Console.Error.WriteLine(
                $"{(int)page.Status} {page.Status} {page.FinalUrl} " +
                $"({page.Body.Length} bytes, {page.Encoding.WebName}) [no-sandbox]");

            if (html)
            {
                Console.Out.Write(page.Html);
                return 0;
            }

            if (select is not null)
            {
                var matches = page.Document.QuerySelectorAll(select);
                foreach (var m in matches)
                {
                    Console.Out.WriteLine(NormalizeWhitespace(m.TextContent));
                }
                Console.Error.WriteLine($"{matches.Count} match(es)");
                return 0;
            }

            var doc = page.Document;
            var title = doc.QuerySelector("title")?.TextContent ?? "(no title)";
            Console.Out.WriteLine($"title: {title}");
            Console.Out.WriteLine($"links: {doc.QuerySelectorAll("a[href]").Count}");
            Console.Out.WriteLine($"images: {doc.QuerySelectorAll("img").Count}");
            Console.Out.WriteLine($"scripts: {doc.QuerySelectorAll("script").Count}");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"fetch error: {ex.Message}");
            return 1;
        }
        catch (HttpFetcherException ex)
        {
            Console.Error.WriteLine($"fetch error: {ex.Message}");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("fetch timed out");
            return 1;
        }
    }

    // -------- shared helpers --------

    private static void WriteStatusLine(int statusCode, string _, string finalUrl, int byteCount, string encoding)
    {
        Console.Error.WriteLine(
            $"{statusCode} {(System.Net.HttpStatusCode)statusCode} {finalUrl} " +
            $"({byteCount} bytes, {encoding})");
    }

    private static string NormalizeWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (var c in s)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (sb.Length > 0) pendingSpace = true;
            }
            else
            {
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void PrintUsage()
    {
        Console.Out.WriteLine("daisi-broski — native C# headless web browser");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  daisi-broski fetch <url> [options]    # phase 1 — fetch + parse only");
        Console.Out.WriteLine("  daisi-broski run   <url> [options]    # phase 3 — also run inline scripts");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Shared options:");
        Console.Out.WriteLine("  --select <css>      CSS selector; prints matching elements' text");
        Console.Out.WriteLine("  --ua <string>       Override the User-Agent header");
        Console.Out.WriteLine("  --max-redirects N   Max redirects to follow (default 20)");
        Console.Out.WriteLine();
        Console.Out.WriteLine("fetch-only options:");
        Console.Out.WriteLine("  --html              Print the decoded HTML body");
        Console.Out.WriteLine("  --no-sandbox        Run in-process instead of in Daisi.Broski.Sandbox.exe");
        Console.Out.WriteLine();
        Console.Out.WriteLine("run-only options:");
        Console.Out.WriteLine("  --quiet             Suppress console output + script error traces");
        Console.Out.WriteLine();
        Console.Out.WriteLine("By default, fetch spawns a sandboxed child process under a Win32");
        Console.Out.WriteLine("Job Object with a 256 MiB memory cap and kill-on-close semantics.");
        Console.Out.WriteLine("The run command always executes in-process (no sandbox) so it can");
        Console.Out.WriteLine("exercise the JS engine directly.");
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"daisi-broski: {message}");
        Console.Error.WriteLine("Run 'daisi-broski --help' for usage.");
        return 3;
    }

    private static IEnumerable<Daisi.Broski.Engine.Layout.LayoutBox> WalkBoxes(Daisi.Broski.Engine.Layout.LayoutBox root)
    {
        var stack = new Stack<Daisi.Broski.Engine.Layout.LayoutBox>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var box = stack.Pop();
            yield return box;
            for (int i = box.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(box.Children[i]);
            }
        }
    }

    /// <summary>
    /// `daisi-broski screenshot &lt;url&gt; --out &lt;path&gt;
    /// [--width N] [--height N]` — fetch + render the page,
    /// write the resulting PNG to disk. Backgrounds + borders
    /// only (text rendering deferred). Always uses the
    /// sandbox child.
    /// </summary>
    private static async Task<int> ScreenshotCommand(string[] args)
    {
        if (args.Length == 0) return UsageError("screenshot requires a URL");
        string urlArg = args[0];
        string? outPath = null;
        int width = 1280;
        int height = 800;
        bool wireframe = false;
        bool dumpStyles = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                case "-o":
                    if (i + 1 >= args.Length) return UsageError("--out needs a path");
                    outPath = args[++i];
                    break;
                case "--width":
                    if (i + 1 >= args.Length) return UsageError("--width needs a number");
                    if (!int.TryParse(args[++i], out width)) return UsageError("bad --width");
                    break;
                case "--height":
                    if (i + 1 >= args.Length) return UsageError("--height needs a number");
                    if (!int.TryParse(args[++i], out height)) return UsageError("bad --height");
                    break;
                case "--wireframe":
                    wireframe = true;
                    break;
                case "--dump-styles":
                    dumpStyles = true;
                    break;
                default:
                    return UsageError($"unknown screenshot flag '{args[i]}'");
            }
        }

        if (outPath is null) return UsageError("--out <path> is required");
        if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var url) ||
            (url.Scheme != "http" && url.Scheme != "https"))
        {
            return UsageError($"'{urlArg}' is not an absolute http(s) URL");
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("daisi-broski: screenshot is Windows-only until phase 5.");
            return 2;
        }

        try
        {
            if (dumpStyles)
            {
                // In-process debug: load, lay out, walk the layout
                // tree, and print each box's computed color + size.
                var page = await Daisi.Broski.Engine.Broski.LoadAsync(url,
                    new Daisi.Broski.Engine.BroskiOptions { ScriptingEnabled = true });
                var vp = new Daisi.Broski.Engine.Css.Viewport { Width = width, Height = height };
                // Print font-fetch stats before the layout dump
                // so we can see whether @font-face came through
                // without waking through the full tree output.
                int fontFiles = 0;
                foreach (var fam in page.Document.Fonts.Values) fontFiles += fam.Count;
                Console.Out.WriteLine($"fonts: {page.Document.Fonts.Count} families, "
                    + $"{fontFiles} files");
                foreach (var (family, files) in page.Document.Fonts)
                {
                    long bytes = 0;
                    foreach (var f in files) bytes += f.Bytes.Length;
                    Console.Out.WriteLine($"  {family} — {files.Count} file(s), {bytes} bytes");
                }
                var root = Daisi.Broski.Engine.Layout.LayoutTree.Build(page.Document, vp);
                int n = 0;
                foreach (var box in WalkBoxes(root))
                {
                    if (box.Element is null) continue;
                    var style = Daisi.Broski.Engine.Css.StyleResolver.Resolve(box.Element, vp);
                    var bg = style.GetPropertyValue("background-color");
                    var bs = style.GetPropertyValue("background");
                    var color = style.GetPropertyValue("color");
                    var display = style.GetPropertyValue("display");
                    Console.Out.WriteLine(
                        $"<{box.Element.TagName}>#{box.Element.Id} .{box.Element.ClassName}  " +
                        $"rect=({box.X:F0},{box.Y:F0},{box.Width:F0}x{box.Height:F0})  " +
                        $"display={display}  bg-color='{bg}'  bg='{bs}'  color='{color}'");
                    if (++n > 40) { Console.Out.WriteLine("  ... truncated ..."); break; }
                }
                return 0;
            }

            await using var session = BrowserSession.Create();
            var nav = await session.RunAsync(url, scriptingEnabled: true);
            Console.Error.WriteLine($"{nav.Status} {nav.FinalUrl} (scripts {nav.ScriptsRan}/{nav.ScriptsTotal})");
            var shot = await session.ScreenshotAsync(width: width, height: height, wireframe: wireframe);
            await File.WriteAllBytesAsync(outPath, shot.Png);
            Console.Out.WriteLine($"wrote {shot.Png.Length} bytes ({shot.Width}x{shot.Height}) → {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"daisi-broski: screenshot failed: {ex.Message}");
            return 1;
        }
    }
}
