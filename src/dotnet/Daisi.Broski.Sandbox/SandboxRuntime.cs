using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
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
    /// <summary>Long-lived per-connection state — the current page
    /// plus an engine + handle table that persist across request
    /// boundaries so the host can interleave navigate → query →
    /// get_property → call_method on the same live DOM / JS
    /// object graph. Reset by each <c>navigate</c> and
    /// <c>run</c> so handles from a prior page don't leak across
    /// site boundaries.</summary>
    private sealed class State
    {
        public LoadedPage? Page;
        public JsEngine? Engine;
        public readonly HandleTable Handles = new();

        public void ResetDocument()
        {
            Handles.Clear();
            Engine = null;
            Page = null;
        }
    }

    public static async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        using var loader = new PageLoader();
        var state = new State();

        while (true)
        {
            var request = await IpcCodec.ReadAsync(input, ct).ConfigureAwait(false);
            if (request is null) return; // clean EOF

            if (request.Kind != MessageKind.Request)
            {
                // Unexpected message shape. Drop it rather than crash.
                continue;
            }

            var response = await HandleRequestAsync(request, loader, state, ct)
                .ConfigureAwait(false);

            try
            {
                await IpcCodec.WriteAsync(output, response, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Host disappeared mid-reply. Exit the loop.
                return;
            }

            if (request.Method == Methods.Close) return;
        }
    }

    private static async Task<IpcMessage> HandleRequestAsync(
        IpcMessage request, PageLoader loader, State state, CancellationToken ct)
    {
        try
        {
            switch (request.Method)
            {
                case Methods.Navigate:
                    return await HandleNavigate(request, loader, state, ct).ConfigureAwait(false);

                case Methods.QueryAll:
                    return HandleQueryAll(request, state.Page);

                case Methods.Run:
                    return await HandleRun(request, state, ct).ConfigureAwait(false);

                case Methods.Skim:
                    return await HandleSkim(request, ct).ConfigureAwait(false);

                case Methods.Evaluate:
                    return HandleEvaluate(request, state);

                case Methods.GetProperty:
                    return HandleGetProperty(request, state);

                case Methods.SetProperty:
                    return HandleSetProperty(request, state);

                case Methods.CallMethod:
                    return HandleCallMethod(request, state);

                case Methods.QueryHandles:
                    return HandleQueryHandles(request, state);

                case Methods.ReleaseHandles:
                    return HandleReleaseHandles(request, state);

                case Methods.Screenshot:
                    return HandleScreenshot(request, state);

                case Methods.Close:
                    return IpcMessage.Response(request.Id, new CloseResponse());

                default:
                    return IpcMessage.ResponseError(
                        request.Id, "method_not_found",
                        $"Unknown method '{request.Method}'");
            }
        }
        catch (Exception ex)
        {
            return IpcMessage.ResponseError(request.Id, "internal_error", ex.Message);
        }
    }

    private static async Task<IpcMessage> HandleNavigate(
        IpcMessage request, PageLoader loader, State state, CancellationToken ct)
    {
        var nav = request.ParamsAs<NavigateRequest>();
        if (nav is null)
        {
            return IpcMessage.ResponseError(request.Id, "bad_request", "navigate requires params");
        }

        if (!Uri.TryCreate(nav.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return IpcMessage.ResponseError(request.Id, "bad_url",
                $"'{nav.Url}' is not an absolute http(s) URL");
        }

        var page = await loader.LoadAsync(uri, ct).ConfigureAwait(false);

        // Cross-page navigate drops every handle from the previous
        // document — those refs are meaningless once the engine
        // attaches a new tree.
        state.ResetDocument();
        state.Page = page;

        return IpcMessage.Response(request.Id, new NavigateResponse
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

    private static async Task<IpcMessage> HandleRun(
        IpcMessage request, State state, CancellationToken ct)
    {
        var req = request.ParamsAs<RunRequest>();
        if (req is null)
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "run requires params");
        }
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return IpcMessage.ResponseError(request.Id, "bad_url",
                $"'{req.Url}' is not an absolute http(s) URL");
        }

        var fetcherOptions = new HttpFetcherOptions
        {
            MaxRedirects = req.MaxRedirects ?? 20,
            UserAgent = req.UserAgent ?? new HttpFetcherOptions().UserAgent,
        };

        // Fetch + parse through PageLoader directly so we retain
        // the engine handle for follow-on Evaluate / CallMethod
        // requests. Broski.LoadAsync would create a throwaway
        // engine we can't reach later.
        using var pageFetcher = new HttpFetcher(fetcherOptions);
        using var pageLoader = new PageLoader(pageFetcher);
        var page = await pageLoader.LoadAsync(uri, ct).ConfigureAwait(false);

        var engine = new JsEngine();
        // Persist localStorage writes to a per-origin JSON file
        // under the platform's local-application-data directory.
        // Gives sites running inside the sandboxed child the same
        // "same-origin reload sees its own state" semantics a real
        // browser offers, without the host having to thread a
        // storage path through IPC for every run.
        var storagePath = EngineBroski.DefaultStoragePath();
        engine.SetStorageBackend(new FileStorageBackend(storagePath));
        engine.IndexedDbBackend = new FileIndexedDbBackend(
            Path.Combine(storagePath, "indexeddb"));
        engine.AttachDocument(page.Document, page.FinalUrl);

        var scriptResult = new PageScriptsResult
        {
            TotalScripts = 0,
            InlineScripts = 0,
            ExternalScripts = 0,
            SkippedByType = 0,
            ExternalFetchErrors = 0,
            Ran = 0,
            Errored = 0,
            Errors = Array.Empty<PageScriptError>(),
        };
        if (req.ScriptingEnabled)
        {
            scriptResult = PageScripts.RunAllAgainst(
                engine, page.Document, page.FinalUrl, pageFetcher);
        }

        // Swap the state's page / engine in. The new handle
        // table starts empty — a previous page's refs aren't
        // meaningful in this document.
        state.ResetDocument();
        state.Page = page;
        state.Engine = engine;

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

        return IpcMessage.Response(request.Id, new RunResponse
        {
            FinalUrl = page.FinalUrl.AbsoluteUri,
            Status = (int)page.Status,
            Title = page.Document.QuerySelector("title")?.TextContent,
            ScriptsTotal = scriptResult.TotalScripts,
            ScriptsRan = scriptResult.Ran,
            ScriptsErrored = scriptResult.Errored,
            ElementCount = elements,
            Html = html,
            Matches = matches,
        });
    }

    private static async Task<IpcMessage> HandleSkim(
        IpcMessage request, CancellationToken ct)
    {
        var req = request.ParamsAs<SkimRequest>();
        if (req is null)
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "skim requires params");
        }
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return IpcMessage.ResponseError(request.Id, "bad_url",
                $"'{req.Url}' is not an absolute http(s) URL");
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

        return IpcMessage.Response(request.Id, new SkimResponse
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
    }

    // ========================================================
    // Phase-4 live handle table: Evaluate / GetProperty /
    // SetProperty / CallMethod / QueryHandles / ReleaseHandles
    // ========================================================

    private static IpcMessage HandleEvaluate(IpcMessage request, State state)
    {
        var req = request.ParamsAs<EvaluateRequest>();
        if (req is null || string.IsNullOrEmpty(req.Script))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "evaluate requires params.script");
        }
        if (state.Engine is null)
        {
            return IpcMessage.ResponseError(request.Id, "no_engine",
                "run or navigate must succeed before evaluate");
        }

        try
        {
            var result = state.Engine.Evaluate(req.Script);
            state.Engine.DrainEventLoop();
            return IpcMessage.Response(request.Id, new EvaluateResponse
            {
                Value = IpcValueCodec.Encode(result, state.Handles),
            });
        }
        catch (JsRuntimeException ex)
        {
            return IpcMessage.ResponseError(request.Id, "js_error", ex.Message);
        }
    }

    private static IpcMessage HandleGetProperty(IpcMessage request, State state)
    {
        var req = request.ParamsAs<GetPropertyRequest>();
        if (req is null || string.IsNullOrEmpty(req.Name))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "get_property requires params.handle + params.name");
        }
        if (!state.Handles.TryGet(req.Handle, out var target))
        {
            return IpcMessage.ResponseError(request.Id, "stale_handle",
                $"handle {req.Handle} is not registered");
        }

        object? value = ReadJsProperty(target, req.Name, state.Engine);
        return IpcMessage.Response(request.Id, new GetPropertyResponse
        {
            Value = IpcValueCodec.Encode(value, state.Handles),
        });
    }

    private static IpcMessage HandleSetProperty(IpcMessage request, State state)
    {
        var req = request.ParamsAs<SetPropertyRequest>();
        if (req is null || string.IsNullOrEmpty(req.Name))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "set_property requires params.handle + params.name + params.value");
        }
        if (!state.Handles.TryGet(req.Handle, out var target))
        {
            return IpcMessage.ResponseError(request.Id, "stale_handle",
                $"handle {req.Handle} is not registered");
        }
        if (target is not JsObject jo)
        {
            return IpcMessage.ResponseError(request.Id, "bad_handle",
                "set_property requires a JsObject handle");
        }

        var decoded = IpcValueCodec.Decode(req.Value, state.Handles);
        jo.Set(req.Name, decoded);
        return IpcMessage.Response(request.Id, new SetPropertyResponse { Ok = true });
    }

    private static IpcMessage HandleCallMethod(IpcMessage request, State state)
    {
        var req = request.ParamsAs<CallMethodRequest>();
        if (req is null || string.IsNullOrEmpty(req.Name))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "call_method requires params.handle + params.name");
        }
        if (state.Engine is null)
        {
            return IpcMessage.ResponseError(request.Id, "no_engine",
                "run or navigate must succeed before call_method");
        }
        if (!state.Handles.TryGet(req.Handle, out var target))
        {
            return IpcMessage.ResponseError(request.Id, "stale_handle",
                $"handle {req.Handle} is not registered");
        }
        if (target is not JsObject jo)
        {
            return IpcMessage.ResponseError(request.Id, "bad_handle",
                "call_method requires a JsObject handle");
        }

        var method = jo.Get(req.Name);
        if (method is not JsFunction fn)
        {
            return IpcMessage.ResponseError(request.Id, "not_a_function",
                $"property '{req.Name}' is not a function");
        }

        var args = new object?[req.Args.Count];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = IpcValueCodec.Decode(req.Args[i], state.Handles);
        }

        try
        {
            var result = state.Engine.Vm.InvokeJsFunction(fn, target, args);
            state.Engine.DrainEventLoop();
            return IpcMessage.Response(request.Id, new CallMethodResponse
            {
                Value = IpcValueCodec.Encode(result, state.Handles),
            });
        }
        catch (JsRuntimeException ex)
        {
            return IpcMessage.ResponseError(request.Id, "js_error", ex.Message);
        }
    }

    private static IpcMessage HandleQueryHandles(IpcMessage request, State state)
    {
        var req = request.ParamsAs<QueryHandlesRequest>();
        if (req is null || string.IsNullOrEmpty(req.Selector))
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "query_handles requires params.selector");
        }
        if (state.Page is null)
        {
            return IpcMessage.ResponseError(request.Id, "no_page",
                "navigate / run must succeed before query_handles");
        }

        var matches = state.Page.Document.QuerySelectorAll(req.Selector);
        var handles = new IpcValue[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            // Prefer the JS-bridge wrapper when an engine is
            // attached — JsDomElement is a JsObject, so host-side
            // SetProperty / CallMethod (click, setAttribute,
            // dispatchEvent, ...) work against it. Fall back to
            // the raw Element otherwise; ReadJsProperty handles
            // the narrow set of attribute-style reads that don't
            // need the bridge.
            object registered = state.Engine?.DomBridge is { } bridge
                ? bridge.Wrap(matches[i])
                : matches[i];
            long id = state.Handles.Register(registered);
            handles[i] = IpcValue.Handle(id, "Element");
        }

        return IpcMessage.Response(request.Id, new QueryHandlesResponse
        {
            Handles = handles,
        });
    }

    private static IpcMessage HandleScreenshot(IpcMessage request, State state)
    {
        if (state.Page is null)
        {
            return IpcMessage.ResponseError(request.Id, "no_page",
                "navigate / run must succeed before screenshot");
        }
        var req = request.ParamsAs<ScreenshotRequest>() ?? new ScreenshotRequest();
        var viewport = new Daisi.Broski.Engine.Css.Viewport
        {
            Width = req.Width ?? Daisi.Broski.Engine.Css.Viewport.Default.Width,
            Height = req.Height ?? Daisi.Broski.Engine.Css.Viewport.Default.Height,
        };
        var root = Daisi.Broski.Engine.Layout.LayoutTree.Build(state.Page.Document, viewport);
        var raster = Daisi.Broski.Engine.Paint.Painter.Paint(
            root, state.Page.Document, viewport);
        var png = Daisi.Broski.Engine.Paint.PngEncoder.Encode(raster);
        return IpcMessage.Response(request.Id, new ScreenshotResponse
        {
            Png = png,
            Width = viewport.Width,
            Height = viewport.Height,
        });
    }

    private static IpcMessage HandleReleaseHandles(IpcMessage request, State state)
    {
        var req = request.ParamsAs<ReleaseHandlesRequest>();
        if (req is null)
        {
            return IpcMessage.ResponseError(request.Id, "bad_request",
                "release_handles requires params.handles");
        }

        int released = 0;
        foreach (var id in req.Handles)
        {
            if (state.Handles.Release(id)) released++;
        }
        return IpcMessage.Response(request.Id, new ReleaseHandlesResponse
        {
            Released = released,
        });
    }

    /// <summary>Read a JS-accessible property off a handle's
    /// target. For JsObject we route through the normal prototype
    /// chain. For a raw DOM Element (which is what QueryHandles
    /// registers), we honor the most common .NET-side shortcuts
    /// (tagName, id, className, textContent, innerHTML,
    /// outerHTML, attributes-by-name) directly so the host
    /// doesn't need the JS bridge wired up to read basic element
    /// state.</summary>
    private static object? ReadJsProperty(object? target, string name, JsEngine? engine)
    {
        if (target is JsObject jo)
        {
            return jo.Get(name);
        }
        if (target is Element el)
        {
            return name switch
            {
                "tagName" => el.TagName,
                "id" => el.Id,
                "className" => el.ClassName,
                "textContent" => el.TextContent,
                "nodeName" => el.NodeName,
                _ => el.GetAttribute(name) ?? (object)JsValue.Undefined,
            };
        }
        return JsValue.Undefined;
    }
}
