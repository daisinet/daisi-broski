using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Ipc;

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
}
