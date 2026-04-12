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

            // Let scripts reach the real network via the slice 3c-6
            // fetch handler. The engine's default already points at
            // a shared HttpFetcher, so no extra setup needed.

            // Find every inline <script> and run it in document
            // order. We skip scripts with a `src` attribute for
            // this command — a real browser would fetch them, but
            // the point here is to exercise the engine against
            // the scripts that actually ship in the HTML response.
            int ranCount = 0;
            int errorCount = 0;
            int inlineCount = 0;
            int externalCount = 0;
            int skippedTypeCount = 0;
            var scriptErrors = new List<string>();
            int scriptIdx = 0;
            foreach (var script in doc.QuerySelectorAll("script"))
            {
                scriptIdx++;
                if (script.HasAttribute("src"))
                {
                    externalCount++;
                    continue;
                }
                var type = script.GetAttribute("type") ?? "text/javascript";
                // Skip modules / JSON-LD / application-specific types.
                if (type is not ("" or "text/javascript" or "application/javascript"))
                {
                    skippedTypeCount++;
                    continue;
                }
                var source = script.TextContent;
                if (string.IsNullOrWhiteSpace(source)) continue;
                inlineCount++;
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
                        $"    source ({source.Length} bytes): {Truncate(source, 400)}");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    scriptErrors.Add(
                        $"[script #{scriptIdx}] {ex.GetType().Name}: {ex.Message}\n" +
                        $"    source ({source.Length} bytes): {Truncate(source, 400)}");
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
            Console.Out.WriteLine(
                $"scripts:     {preScripts} total (" +
                $"{inlineCount} inline, {externalCount} external, {skippedTypeCount} other type)");
            Console.Out.WriteLine(
                $"inline run:  {ranCount}/{inlineCount}" +
                (errorCount > 0 ? $" ({errorCount} errored)" : ""));
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
}
