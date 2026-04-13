using Daisi.Broski.Engine.Net;
// Alias to disambiguate the `Skimmer` static class from the
// containing `Daisi.Broski.Skimmer` namespace.
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// CLI driver for the Skimmer — fetch a URL, extract the main article
/// content, format it as JSON or Markdown, and write to stdout (or a
/// file via <c>--out</c>).
///
/// <code>
///   daisi-broski-skim &lt;url&gt; [options]
///
///   Options:
///     --format json|md|both   Output format. Default: json.
///     --out FILE              Write to FILE instead of stdout. With
///                             --format both, treated as a prefix
///                             (FILE.json + FILE.md).
///     --ua STRING             User-Agent override.
///     --max-redirects N       Max redirects to follow (default 20).
///     --quiet                 Suppress the fetch summary on stderr.
/// </code>
///
/// Exit codes:
///   0  success
///   1  fetch failure
///   2  extraction yielded no content
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

        string? urlArg = null;
        string format = "json";
        string? outPath = null;
        string? userAgent = null;
        int maxRedirects = 20;
        bool quiet = false;
        bool scriptingEnabled = true;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--format":
                    if (i + 1 >= args.Length) return UsageError("--format requires a value");
                    format = args[++i].ToLowerInvariant();
                    if (format is not ("json" or "md" or "markdown" or "both"))
                        return UsageError($"--format must be json|md|both, got '{format}'");
                    if (format == "markdown") format = "md";
                    break;
                case "--out":
                case "-o":
                    if (i + 1 >= args.Length) return UsageError("--out requires a value");
                    outPath = args[++i];
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
                case "--no-scripts":
                    scriptingEnabled = false;
                    break;
                case "--scripts":
                    scriptingEnabled = true;
                    break;
                default:
                    if (a.StartsWith('-')) return UsageError($"Unknown option '{a}'");
                    if (urlArg is not null) return UsageError("Multiple positional URLs passed");
                    urlArg = a;
                    break;
            }
        }

        if (urlArg is null) return UsageError("missing URL");
        if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var url) ||
            (url.Scheme != "http" && url.Scheme != "https"))
        {
            return UsageError($"'{urlArg}' is not an absolute http(s) URL");
        }

        var fetcher = new HttpFetcherOptions
        {
            MaxRedirects = maxRedirects,
            UserAgent = userAgent ?? new HttpFetcherOptions().UserAgent,
        };

        try
        {
            var article = await SkimmerApi.SkimAsync(url, new SkimmerOptions
            {
                ScriptingEnabled = scriptingEnabled,
                Fetcher = fetcher,
            });

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"{article.Url} ({article.WordCount} words" +
                    (scriptingEnabled ? ", scripts on" : ", scripts off") + ")");
            }

            if (article.ContentRoot is null && string.IsNullOrEmpty(article.PlainText))
            {
                Console.Error.WriteLine("skimmer: no article content found");
                return 2;
            }

            return EmitOutputs(article, format, outPath, quiet);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fetch error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Render the article in the requested format(s) and
    /// write to <paramref name="outPath"/> (or stdout when null).
    /// With <c>--format both</c>, <paramref name="outPath"/> is treated
    /// as a prefix and the two outputs land in <c>{prefix}.json</c> and
    /// <c>{prefix}.md</c>.</summary>
    private static int EmitOutputs(ArticleContent article, string format, string? outPath, bool quiet)
    {
        if (format == "both")
        {
            string json = JsonFormatter.Format(article);
            string md = MarkdownFormatter.Format(article);
            if (outPath is null)
            {
                // Stdout: concatenate with a separator so a script that
                // pipes both can split. Uncommon path — most callers
                // either pick one format or pass a file prefix.
                Console.Out.WriteLine("=== JSON ===");
                Console.Out.WriteLine(json);
                Console.Out.WriteLine();
                Console.Out.WriteLine("=== MARKDOWN ===");
                Console.Out.WriteLine(md);
            }
            else
            {
                File.WriteAllText(outPath + ".json", json);
                File.WriteAllText(outPath + ".md", md);
                if (!quiet)
                {
                    Console.Error.WriteLine($"wrote {outPath}.json + {outPath}.md");
                }
            }
            return 0;
        }

        string text = format switch
        {
            "md" => MarkdownFormatter.Format(article),
            _ => JsonFormatter.Format(article),
        };

        if (outPath is null)
        {
            Console.Out.WriteLine(text);
        }
        else
        {
            File.WriteAllText(outPath, text);
            if (!quiet) Console.Error.WriteLine($"wrote {outPath}");
        }
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"daisi-broski-skim: {message}");
        Console.Error.WriteLine("Run 'daisi-broski-skim --help' for usage.");
        return 3;
    }

    private static void PrintUsage()
    {
        Console.Out.WriteLine(
            """
            daisi-broski-skim - extract article content as JSON or Markdown

            Usage:
              daisi-broski-skim <url> [options]

            Options:
              --format json|md|both   Output format (default: json)
              --out FILE              Write to FILE instead of stdout. With
                                      --format both, FILE is a prefix
                                      (writes FILE.json + FILE.md).
              --ua STRING             Override the User-Agent header.
              --max-redirects N       Max redirects to follow (default 20).
              --scripts               Run page scripts before extracting (default).
              --no-scripts            Skip script execution. Faster + deterministic;
                                      use when the SSR shell already has the content.
              --quiet                 Suppress fetch summary on stderr.

            Examples:
              daisi-broski-skim https://news.ycombinator.com --format md
              daisi-broski-skim https://example.com --out article --format both
            """);
    }
}
