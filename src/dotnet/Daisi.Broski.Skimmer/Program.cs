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
        bool noSandbox = false;

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
                case "--no-sandbox":
                    noSandbox = true;
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

        bool useSandbox = !noSandbox && OperatingSystem.IsWindows();

        try
        {
            if (useSandbox && OperatingSystem.IsWindows())
            {
                return await SkimInSandbox(
                    url, scriptingEnabled, userAgent, maxRedirects,
                    format, outPath, quiet);
            }

            var fetcher = new HttpFetcherOptions
            {
                MaxRedirects = maxRedirects,
                UserAgent = userAgent ?? new HttpFetcherOptions().UserAgent,
            };

            var article = await SkimmerApi.SkimAsync(url, new SkimmerOptions
            {
                ScriptingEnabled = scriptingEnabled,
                Fetcher = fetcher,
            });

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"{article.Url} ({article.WordCount} words" +
                    (scriptingEnabled ? ", scripts on" : ", scripts off") +
                    ", in-process)");
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

    /// <summary>Sandboxed skim path — the entire
    /// fetch+parse+JS+extract pipeline runs in
    /// <c>Daisi.Broski.Sandbox.exe</c> under a Win32 Job Object.
    /// The host only serializes input + deserializes output.
    /// Windows-only; the outer dispatcher checks
    /// <see cref="OperatingSystem.IsWindows"/> before calling.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> SkimInSandbox(
        Uri url, bool scriptingEnabled, string? userAgent, int maxRedirects,
        string format, string? outPath, bool quiet)
    {
        try
        {
            await using var session = Daisi.Broski.BrowserSession.Create();
            var resp = await session.SkimAsync(
                url, scriptingEnabled, userAgent, maxRedirects);

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"{resp.Url} ({resp.WordCount} words" +
                    (scriptingEnabled ? ", scripts on" : ", scripts off") +
                    ", sandboxed)");
            }

            if (string.IsNullOrEmpty(resp.PlainText))
            {
                Console.Error.WriteLine("skimmer: no article content found");
                return 2;
            }

            // Format the sandboxed response. Since ContentRoot lives
            // in the child process, we can't re-run the formatters
            // against an Element; we have the pre-rendered HTML from
            // the sandbox. JSON / MD formatters reconstruct from the
            // serialized metadata.
            var text = format switch
            {
                "md" => EmitMarkdownFromSandboxed(resp),
                "html" => resp.ContentHtml ?? "",
                "both" => null, // handled inline below
                _ => EmitJsonFromSandboxed(resp),
            };

            if (format == "both")
            {
                var json = EmitJsonFromSandboxed(resp);
                var md = EmitMarkdownFromSandboxed(resp);
                if (outPath is null)
                {
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
                    if (!quiet) Console.Error.WriteLine($"wrote {outPath}.json + {outPath}.md");
                }
                return 0;
            }

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
        catch (Daisi.Broski.SandboxException ex)
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

    /// <summary>Re-serialize a <c>SkimResponse</c> into the same JSON
    /// shape the in-process <see cref="JsonFormatter"/> emits. Uses
    /// the raw text fields directly so the output is byte-identical
    /// to the in-process path.</summary>
    private static string EmitJsonFromSandboxed(Daisi.Broski.Ipc.SkimResponse resp)
    {
        // Easier to rebuild via the real formatter — construct a
        // synthetic ArticleContent (ContentRoot null; the JSON
        // formatter only references metadata + the two link arrays).
        var fake = SynthesizeArticleFromSandbox(resp);
        return JsonFormatter.Format(fake);
    }

    private static string EmitMarkdownFromSandboxed(Daisi.Broski.Ipc.SkimResponse resp)
    {
        // MarkdownFormatter walks ContentRoot, which we don't have
        // in the sandboxed path. Emit the nav table + metadata
        // block manually and inline the pre-rendered plain text.
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(resp.Title))
            sb.Append("# ").Append(resp.Title!.Trim()).Append("\n\n");
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(resp.Byline)) meta.Add($"By **{resp.Byline}**");
        if (!string.IsNullOrWhiteSpace(resp.PublishedAt)) meta.Add(resp.PublishedAt!);
        if (!string.IsNullOrWhiteSpace(resp.SiteName)) meta.Add($"on _{resp.SiteName}_");
        meta.Add($"[source]({resp.Url})");
        sb.Append(string.Join(" • ", meta)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(resp.HeroImage))
            sb.Append("![](").Append(resp.HeroImage).Append(")\n\n");
        if (!string.IsNullOrWhiteSpace(resp.Description))
            sb.Append("> ").Append(resp.Description).Append("\n\n");
        if (resp.NavLinks.Count > 0)
        {
            sb.Append("## Navigation\n\n| Link | URL |\n| --- | --- |\n");
            foreach (var link in resp.NavLinks)
            {
                sb.Append("| [").Append(link.Text).Append("](").Append(link.Href)
                  .Append(") | <").Append(link.Href).Append("> |\n");
            }
            sb.Append('\n');
        }
        sb.Append(resp.PlainText);
        return sb.ToString();
    }

    /// <summary>Build a minimal <c>ArticleContent</c> with the
    /// sandboxed metadata so the existing <see cref="JsonFormatter"/>
    /// can emit it unchanged. <c>ContentRoot</c> stays null — the
    /// formatter doesn't need it for the JSON shape.</summary>
    private static ArticleContent SynthesizeArticleFromSandbox(
        Daisi.Broski.Ipc.SkimResponse resp)
    {
        var images = new ExtractedImage[resp.Images.Count];
        for (int i = 0; i < images.Length; i++)
            images[i] = new ExtractedImage(resp.Images[i].Href, resp.Images[i].Text);
        var links = new ExtractedLink[resp.Links.Count];
        for (int i = 0; i < links.Length; i++)
            links[i] = new ExtractedLink(resp.Links[i].Href, resp.Links[i].Text);
        var nav = new ExtractedLink[resp.NavLinks.Count];
        for (int i = 0; i < nav.Length; i++)
            nav[i] = new ExtractedLink(resp.NavLinks[i].Href, resp.NavLinks[i].Text);
        return new ArticleContent
        {
            Url = new Uri(resp.Url),
            Title = resp.Title,
            Byline = resp.Byline,
            PublishedAt = resp.PublishedAt,
            Lang = resp.Lang,
            Description = resp.Description,
            SiteName = resp.SiteName,
            HeroImage = resp.HeroImage,
            PlainText = resp.PlainText,
            WordCount = resp.WordCount,
            Images = images,
            Links = links,
            NavLinks = nav,
        };
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
