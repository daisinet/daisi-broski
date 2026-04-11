using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Net;

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
/// </code>
///
/// Exit codes:
///   0  success
///   1  transport / fetch failure
///   2  parse / selector failure (unlikely — we don't fail-closed on bad HTML)
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

        var options = new HttpFetcherOptions
        {
            MaxRedirects = maxRedirects,
            UserAgent = userAgent ?? new HttpFetcherOptions().UserAgent,
        };

        try
        {
            using var loader = new PageLoader(options);
            var page = await loader.LoadAsync(url);

            // Status line to stderr so --select output to stdout is greppable.
            Console.Error.WriteLine(
                $"{(int)page.Status} {page.Status} {page.FinalUrl} ({page.Body.Length} bytes, {page.Encoding.WebName})");

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
                    // Collapse inner whitespace runs so output stays one-line-per-match.
                    Console.Out.WriteLine(NormalizeWhitespace(m.TextContent));
                }
                Console.Error.WriteLine($"{matches.Count} match(es)");
                return 0;
            }

            // No --select, no --html: print a short summary.
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
        Console.Out.WriteLine("daisi-broski — native C# headless web browser (phase 1)");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  daisi-broski fetch <url> [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --select <css>      CSS selector; prints matching elements' text");
        Console.Out.WriteLine("  --html              Print the decoded HTML body");
        Console.Out.WriteLine("  --ua <string>       Override the User-Agent header");
        Console.Out.WriteLine("  --max-redirects N   Max redirects to follow (default 20)");
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"daisi-broski: {message}");
        Console.Error.WriteLine("Run 'daisi-broski --help' for usage.");
        return 3;
    }
}
