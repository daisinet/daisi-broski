using Daisi.Broski;
using Daisi.Broski.Engine;
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
            _ => UsageError($"Unknown command '{args[0]}'"),
        };
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
        Console.Out.WriteLine("  daisi-broski fetch <url> [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --select <css>      CSS selector; prints matching elements' text");
        Console.Out.WriteLine("  --html              Print the decoded HTML body");
        Console.Out.WriteLine("  --ua <string>       Override the User-Agent header");
        Console.Out.WriteLine("  --max-redirects N   Max redirects to follow (default 20)");
        Console.Out.WriteLine("  --no-sandbox        Run in-process instead of in a Daisi.Broski.Sandbox.exe child");
        Console.Out.WriteLine();
        Console.Out.WriteLine("By default, fetch spawns a sandboxed child process under a Win32");
        Console.Out.WriteLine("Job Object with a 256 MiB memory cap and kill-on-close semantics.");
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"daisi-broski: {message}");
        Console.Error.WriteLine("Run 'daisi-broski --help' for usage.");
        return 3;
    }
}
