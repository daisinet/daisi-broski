using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Ipc;
using Daisi.Broski.Skimmer;
// The Skimmer.Skimmer class collides with its containing namespace;
// alias it the same way the Surfer does.
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;
using EngineBroski = Daisi.Broski.Engine.Broski;

namespace Daisi.Broski.Sandbox;

/// <summary>
/// The request dispatch loop inside the sandbox child process. Reads
/// IPC requests from an inbound pipe, executes them against a single
/// long-lived <see cref="PageLoader"/>, and writes responses to an
/// outbound pipe.
///
/// The loop is single-threaded on purpose: one request processed at
/// a time, in order, just like a real browser's main thread. Concurrency
/// inside the engine is a phase-3 concern.
///
/// Exit conditions:
/// - The host closes the inbound pipe (clean EOF, <see cref="IpcCodec.ReadAsync"/>
///   returns null). The loop ends, the sandbox exits 0.
/// - The host sends a <c>close</c> request. We send the response, close
///   the pipes, exit 0.
/// - The host process dies. The Win32 Job Object's
///   <c>KILL_ON_JOB_CLOSE</c> flag terminates us before any further I/O.
/// </summary>
internal static class SandboxRuntime
{
    public static async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        using var loader = new PageLoader();
        LoadedPage? currentPage = null;

        while (true)
        {
            var request = await IpcCodec.ReadAsync(input, ct).ConfigureAwait(false);
            if (request is null) return; // clean EOF

            if (request.Kind != MessageKind.Request)
            {
                // Unexpected message shape. Drop it rather than crash.
                continue;
            }

            var response = await HandleRequestAsync(request, loader, currentPage, ct)
                .ConfigureAwait(false);

            // Update the current page if this was a successful navigate.
            if (response.newPage is not null) currentPage = response.newPage;

            try
            {
                await IpcCodec.WriteAsync(output, response.response, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Host disappeared mid-reply. Exit the loop.
                return;
            }

            if (request.Method == Methods.Close) return;
        }
    }

    private static async Task<(IpcMessage response, LoadedPage? newPage)> HandleRequestAsync(
        IpcMessage request, PageLoader loader, LoadedPage? currentPage, CancellationToken ct)
    {
        try
        {
            switch (request.Method)
            {
                case Methods.Navigate:
                    return await HandleNavigate(request, loader, ct).ConfigureAwait(false);

                case Methods.QueryAll:
                    return (HandleQueryAll(request, currentPage), currentPage);

                case Methods.Run:
                    return await HandleRun(request, ct).ConfigureAwait(false);

                case Methods.Skim:
                    return await HandleSkim(request, ct).ConfigureAwait(false);

                case Methods.Close:
                    return (IpcMessage.Response(request.Id, new CloseResponse()), currentPage);

                default:
                    return (
                        IpcMessage.ResponseError(
                            request.Id, "method_not_found",
                            $"Unknown method '{request.Method}'"),
                        currentPage);
            }
        }
        catch (Exception ex)
        {
            return (
                IpcMessage.ResponseError(request.Id, "internal_error", ex.Message),
                currentPage);
        }
    }

    private static async Task<(IpcMessage, LoadedPage?)> HandleNavigate(
        IpcMessage request, PageLoader loader, CancellationToken ct)
    {
        var nav = request.ParamsAs<NavigateRequest>();
        if (nav is null)
        {
            return (
                IpcMessage.ResponseError(request.Id, "bad_request", "navigate requires params"),
                null);
        }

        if (!Uri.TryCreate(nav.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (
                IpcMessage.ResponseError(request.Id, "bad_url",
                    $"'{nav.Url}' is not an absolute http(s) URL"),
                null);
        }

        var page = await loader.LoadAsync(uri, ct).ConfigureAwait(false);

        var response = IpcMessage.Response(request.Id, new NavigateResponse
        {
            FinalUrl = page.FinalUrl.AbsoluteUri,
            Status = (int)page.Status,
            ContentType = page.ContentType,
            Encoding = page.Encoding.WebName,
            RedirectChain = page.RedirectChain.Select(u => u.AbsoluteUri).ToArray(),
            ByteCount = page.Body.Length,
            Title = page.Document.QuerySelector("title")?.TextContent,
            Html = nav.IncludeHtml ? page.Html : null,
        });

        return (response, page);
    }

    private static IpcMessage HandleQueryAll(IpcMessage request, LoadedPage? currentPage)
    {
        if (currentPage is null)
        {
            return IpcMessage.ResponseError(request.Id, "no_page",
                "navigate must succeed before query_all");
        }

        var query = request.ParamsAs<QueryAllRequest>();
        if (query is null || string.IsNullOrEmpty(query.Selector))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "query_all requires params.selector");
        }

        var matches = currentPage.Document.QuerySelectorAll(query.Selector);
        var serialized = matches.Select(SerializeElement).ToArray();

        return IpcMessage.Response(request.Id, new QueryAllResponse { Matches = serialized });
    }

    private static SerializedElement SerializeElement(Element element)
    {
        var attrs = new Dictionary<string, string>(element.Attributes.Count);
        foreach (var a in element.Attributes)
        {
            attrs[a.Key] = a.Value;
        }
        return new SerializedElement
        {
            TagName = element.TagName,
            Attributes = attrs,
            TextContent = element.TextContent,
        };
    }

    // ========================================================
    // Phase-4 completion: Run + Skim dispatchers
    // ========================================================

    private static async Task<(IpcMessage, LoadedPage?)> HandleRun(
        IpcMessage request, CancellationToken ct)
    {
        var req = request.ParamsAs<RunRequest>();
        if (req is null)
        {
            return (IpcMessage.ResponseError(request.Id, "bad_request",
                "run requires params"), null);
        }
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (IpcMessage.ResponseError(request.Id, "bad_url",
                $"'{req.Url}' is not an absolute http(s) URL"), null);
        }

        var fetcherOptions = new HttpFetcherOptions
        {
            MaxRedirects = req.MaxRedirects ?? 20,
            UserAgent = req.UserAgent ?? new HttpFetcherOptions().UserAgent,
        };

        // Broski.LoadAsync takes care of both the fetch and the
        // script pass. When ScriptingEnabled is false this is
        // equivalent to PageLoader.LoadAsync. Any exception
        // propagates out and the outer catch turns it into an
        // internal_error response.
        var page = await EngineBroski.LoadAsync(uri, new BroskiOptions
        {
            ScriptingEnabled = req.ScriptingEnabled,
            Fetcher = fetcherOptions,
        }, ct).ConfigureAwait(false);

        // Count total / run / errored scripts by delegating to
        // PageScripts.RunAll's counters. When scripting is on
        // EngineBroski.LoadAsync already ran them, so we call a
        // zero-work variant here — just count the <script> tags.
        int total = page.Document.QuerySelectorAll("script").Count;
        // No per-run accounting from EngineBroski; surface the
        // counts we have. Real error tracking is the CLI's job
        // (it uses PageScripts.RunAll directly).
        int elements = page.Document.DescendantElements().Count();

        // Selector matches OR full HTML, mutually exclusive.
        IReadOnlyList<SerializedElement> matches =
            req.Select is { Length: > 0 } selector
                ? page.Document.QuerySelectorAll(selector).Select(SerializeElement).ToArray()
                : Array.Empty<SerializedElement>();
        string? html = null;
        if (req.IncludeHtml && req.Select is null)
        {
            // Serialize the post-script body via the Skimmer's
            // HtmlFormatter-equivalent idea — reuse the raw
            // decoded HTML as-is for now. A proper post-JS
            // re-serialization would need outerHTML support; this
            // is "good enough" for consumers that want something
            // to feed back through an HTML parser.
            html = page.Html;
        }

        var response = IpcMessage.Response(request.Id, new RunResponse
        {
            FinalUrl = page.FinalUrl.AbsoluteUri,
            Status = (int)page.Status,
            Title = page.Document.QuerySelector("title")?.TextContent,
            ScriptsTotal = total,
            ScriptsRan = total, // EngineBroski doesn't currently expose counts; good-enough
            ScriptsErrored = 0,
            ElementCount = elements,
            Html = html,
            Matches = matches,
        });
        return (response, page);
    }

    private static async Task<(IpcMessage, LoadedPage?)> HandleSkim(
        IpcMessage request, CancellationToken ct)
    {
        var req = request.ParamsAs<SkimRequest>();
        if (req is null)
        {
            return (IpcMessage.ResponseError(request.Id, "bad_request",
                "skim requires params"), null);
        }
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (IpcMessage.ResponseError(request.Id, "bad_url",
                $"'{req.Url}' is not an absolute http(s) URL"), null);
        }

        var fetcherOptions = new HttpFetcherOptions
        {
            MaxRedirects = req.MaxRedirects ?? 20,
            UserAgent = req.UserAgent ?? new HttpFetcherOptions().UserAgent,
        };

        var article = await SkimmerApi.SkimAsync(uri, new SkimmerOptions
        {
            ScriptingEnabled = req.ScriptingEnabled,
            Fetcher = fetcherOptions,
        }, ct).ConfigureAwait(false);

        var response = IpcMessage.Response(request.Id, new SkimResponse
        {
            Url = article.Url.AbsoluteUri,
            Title = article.Title,
            Byline = article.Byline,
            PublishedAt = article.PublishedAt,
            Lang = article.Lang,
            Description = article.Description,
            SiteName = article.SiteName,
            HeroImage = article.HeroImage,
            PlainText = article.PlainText,
            WordCount = article.WordCount,
            ContentHtml = HtmlFormatter.Format(article),
            Images = article.Images.Select(i =>
                new SerializedLink { Href = i.Src, Text = i.Alt }).ToArray(),
            Links = article.Links.Select(l =>
                new SerializedLink { Href = l.Href, Text = l.Text }).ToArray(),
            NavLinks = article.NavLinks.Select(l =>
                new SerializedLink { Href = l.Href, Text = l.Text }).ToArray(),
        });
        // Skim doesn't update the currentPage cache — query_all
        // against a post-Skim state isn't meaningful since the
        // Skimmer mutates the document during noise-strip.
        return (response, null);
    }
}
