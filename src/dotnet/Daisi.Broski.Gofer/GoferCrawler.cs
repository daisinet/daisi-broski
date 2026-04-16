using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Net;
using Daisi.Broski.Skimmer;

namespace Daisi.Broski.Gofer;

/// <summary>
/// Hyper-parallel web crawler. Given a set of seed URLs and a
/// <see cref="GoferOptions"/>, fans work out across N workers
/// reading from a <see cref="Channel{T}"/> frontier, shares a
/// single <see cref="HttpClient"/> for connection pooling, honours
/// a per-host delay for politeness, and emits results through
/// both an <see cref="IGoferOutput"/> sink and the
/// <see cref="PageScraped"/> event / <see cref="OnPageScrapedAsync"/>
/// callback.
///
/// <para>Typical usage:</para>
/// <code>
/// var opts = new GoferOptions { MaxDepth = 2, MaxPages = 500 };
/// opts.Headers["Authorization"] = "Bearer ...";
/// opts.Selectors.Add("article.post-body");
/// opts.Output = new Outputs.FileOutput("crawl.jsonl");
///
/// var gofer = new GoferCrawler(opts);
/// gofer.PageScraped += (_, e) => Console.WriteLine($"{e.Result.Url} {e.Result.DurationMs}ms");
/// await gofer.RunAsync([new Uri("https://example.com")]);
/// </code>
/// </summary>
public sealed class GoferCrawler : IAsyncDisposable
{
    private readonly GoferOptions _opts;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Fires once per page AFTER the result has been
    /// written to <see cref="GoferOptions.Output"/>. Handlers run
    /// synchronously on the worker that finished the page — keep
    /// them short, or use <see cref="OnPageScrapedAsync"/> for
    /// async work.</summary>
    public event EventHandler<GoferPageEventArgs>? PageScraped;

    /// <summary>Async counterpart to <see cref="PageScraped"/>.
    /// Awaited on the worker thread before the next page is
    /// dequeued, so long-running handlers throttle the crawl —
    /// spawn background work if you don't want that back-pressure.</summary>
    public Func<GoferResult, CancellationToken, Task>? OnPageScrapedAsync { get; set; }

    public GoferOptions Options => _opts;

    public GoferCrawler(GoferOptions options)
        : this(options, httpClient: null) { }

    /// <summary>Construct with a caller-supplied <see cref="HttpClient"/>.
    /// Lets hosts that already own a pooled client (with their own
    /// handler, certificates, proxy, etc.) reuse it. The crawler
    /// won't dispose a client it doesn't own.</summary>
    public GoferCrawler(GoferOptions options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "DegreeOfParallelism must be > 0");
        if (options.MaxPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPages must be > 0");
        if (options.MaxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be >= 0");

        _opts = options;
        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                MaxConnectionsPerServer = Math.Max(4, options.DegreeOfParallelism),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = options.RequestTimeout,
            };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }

        foreach (var header in options.Headers)
        {
            // TryAddWithoutValidation so callers can set User-Agent
            // or Accept without the default header-sanity lash.
            _http.DefaultRequestHeaders.Remove(header.Key);
            _http.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    public Task RunAsync(Uri seed, CancellationToken ct = default)
        => RunAsync([seed], ct);

    public async Task RunAsync(IEnumerable<Uri> seeds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        var seedList = seeds.Where(u => u.Scheme is "http" or "https").ToList();
        if (seedList.Count == 0) return;

        var allowedHosts = _opts.StayOnHost
            ? new HashSet<string>(seedList.Select(s => s.Host), StringComparer.OrdinalIgnoreCase)
            : null;

        var frontier = Channel.CreateUnbounded<CrawlItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        // Dedup: TryAdd returns false on repeat URL (keyed by the
        // normalized absolute URL — fragments stripped).
        var visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Per-host politeness semaphore + last-fetch timestamp.
        // The semaphore caps concurrency-per-host at 1 so the delay
        // accounting is race-free; unlimited concurrency across
        // different hosts still holds.
        var hostGates = new ConcurrentDictionary<string, HostGate>(StringComparer.OrdinalIgnoreCase);

        int inflight = 0;
        int pagesStarted = 0;

        foreach (var seed in seedList)
        {
            var key = NormalizeUrl(seed);
            if (visited.TryAdd(key, 0))
            {
                Interlocked.Increment(ref inflight);
                frontier.Writer.TryWrite(new CrawlItem(seed, 0));
            }
        }

        if (inflight == 0) return;  // all seeds were dupes / filtered

        var workers = new Task[_opts.DegreeOfParallelism];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(() => WorkerLoop(
                frontier, visited, hostGates, allowedHosts,
                () => Interlocked.Increment(ref pagesStarted),
                () => Volatile.Read(ref pagesStarted),
                () =>
                {
                    if (Interlocked.Decrement(ref inflight) == 0)
                        frontier.Writer.TryComplete();
                },
                enqueue: (uri, depth) =>
                {
                    Interlocked.Increment(ref inflight);
                    if (!frontier.Writer.TryWrite(new CrawlItem(uri, depth)))
                        Interlocked.Decrement(ref inflight);
                },
                ct),
                ct);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        await _opts.Output.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WorkerLoop(
        Channel<CrawlItem> frontier,
        ConcurrentDictionary<string, byte> visited,
        ConcurrentDictionary<string, HostGate> hostGates,
        HashSet<string>? allowedHosts,
        Func<int> reservePageSlot,
        Func<int> peekPageCount,
        Action finishItem,
        Action<Uri, int> enqueue,
        CancellationToken ct)
    {
        await foreach (var item in frontier.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                // Reserve this page's slot. If we're over budget,
                // skip work — but still drain the item so the
                // completion bookkeeping stays correct.
                if (reservePageSlot() > _opts.MaxPages) continue;

                var result = await ProcessAsync(item, hostGates, ct).ConfigureAwait(false);

                await _opts.Output.WriteAsync(result, ct).ConfigureAwait(false);
                PageScraped?.Invoke(this, new GoferPageEventArgs(result));
                if (OnPageScrapedAsync is { } asyncHandler)
                    await asyncHandler(result, ct).ConfigureAwait(false);

                // Frontier expansion: only when below depth limit
                // and the page parsed successfully.
                if (_opts.FollowLinks && item.Depth < _opts.MaxDepth && result.Error is null)
                {
                    foreach (var link in result.Links)
                    {
                        if (allowedHosts is not null && !allowedHosts.Contains(link.Host))
                            continue;
                        if (_opts.UrlFilter is { } filter && !filter(link))
                            continue;
                        if (peekPageCount() >= _opts.MaxPages)
                            break;
                        var key = NormalizeUrl(link);
                        if (visited.TryAdd(key, 0))
                            enqueue(link, item.Depth + 1);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // ProcessAsync is already exception-resistant; any
                // surprise here is a bookkeeping bug we swallow so
                // the worker doesn't die and strand the frontier.
            }
            finally
            {
                finishItem();
            }
        }
    }

    private async Task<GoferResult> ProcessAsync(
        CrawlItem item,
        ConcurrentDictionary<string, HostGate> hostGates,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var gate = hostGates.GetOrAdd(item.Url.Host, _ => new HostGate());
        await gate.Enter(_opts.PerHostDelay, ct).ConfigureAwait(false);

        int status = 0;
        long bytes = 0;
        string? contentType = null;
        byte[]? body = null;
        Uri finalUrl = item.Url;
        string? fetchError = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            contentType = resp.Content.Headers.ContentType?.ToString();
            body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            bytes = body.Length;
            finalUrl = resp.RequestMessage?.RequestUri ?? item.Url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            fetchError = ex.Message;
        }
        finally
        {
            gate.Exit();
        }

        if (fetchError is not null || body is null || body.Length == 0 || status >= 400)
        {
            return new GoferResult
            {
                Url = finalUrl,
                RequestedUrl = item.Url,
                Status = status,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Bytes = bytes,
                Depth = item.Depth,
                Markdown = "",
                PlainText = "",
                Error = fetchError ?? $"HTTP {status}",
            };
        }

        // Only parse text/html-ish bodies — binary content (images,
        // zip files, etc.) would produce garbage through the HTML
        // tree builder.
        if (!LooksLikeHtml(contentType, body))
        {
            return new GoferResult
            {
                Url = finalUrl,
                RequestedUrl = item.Url,
                Status = status,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Bytes = bytes,
                Depth = item.Depth,
                Markdown = "",
                PlainText = "",
                Error = $"non-html content-type: {contentType ?? "<unknown>"}",
            };
        }

        var encoding = EncodingSniffer.Sniff(body, contentType);
        var html = encoding.GetString(body);
        Document doc;
        try { doc = HtmlTreeBuilder.Parse(html); }
        catch (Exception ex)
        {
            return new GoferResult
            {
                Url = finalUrl,
                RequestedUrl = item.Url,
                Status = status,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Bytes = bytes,
                Depth = item.Depth,
                Markdown = "",
                PlainText = "",
                Error = $"html parse failed: {ex.Message}",
            };
        }

        // Always extract the article — used directly when no
        // selectors were supplied, and for metadata (title / byline
        // / description) even when selectors are.
        ArticleContent article;
        try { article = ContentExtractor.Extract(doc, finalUrl); }
        catch (Exception ex)
        {
            return new GoferResult
            {
                Url = finalUrl,
                RequestedUrl = item.Url,
                Status = status,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Bytes = bytes,
                Depth = item.Depth,
                Markdown = "",
                PlainText = "",
                Error = $"content extraction failed: {ex.Message}",
            };
        }

        var (md, plain, selections) = RenderContent(doc, article);
        var links = ExtractLinks(doc, finalUrl);

        return new GoferResult
        {
            Url = finalUrl,
            RequestedUrl = item.Url,
            Status = status,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = sw.ElapsedMilliseconds,
            Bytes = bytes,
            Depth = item.Depth,
            Title = article.Title,
            Byline = article.Byline,
            Description = article.Description,
            Lang = article.Lang,
            SiteName = article.SiteName,
            Markdown = md,
            PlainText = plain,
            Selections = selections,
            Links = links,
        };
    }

    private (string md, string plain, IReadOnlyList<GoferSelection> selections) RenderContent(
        Document doc, ArticleContent article)
    {
        if (_opts.Selectors.Count == 0)
        {
            // Full-article path: Skimmer's MarkdownFormatter gives
            // us rich markdown with title, byline, nav table, etc.
            var md = MarkdownFormatter.Format(article);
            return (md, article.PlainText ?? "", []);
        }

        // Selector path: concatenate plain-text of each match set.
        // Plain text is lossy but unambiguous — an LLM can consume
        // it without choking on half-rendered markdown. Richer
        // per-selection markdown is a worthy follow-up (see
        // GoferResult.Selections XML doc).
        var selections = new List<GoferSelection>(_opts.Selectors.Count);
        var combinedPlain = new System.Text.StringBuilder();

        foreach (var selector in _opts.Selectors)
        {
            IReadOnlyList<Element> matches;
            try { matches = doc.QuerySelectorAll(selector); }
            catch { matches = []; }

            var buf = new System.Text.StringBuilder();
            foreach (var el in matches)
            {
                var text = NormalizeWhitespace(el.TextContent);
                if (text.Length > 0)
                {
                    if (buf.Length > 0) buf.Append("\n\n");
                    buf.Append(text);
                }
            }
            var text2 = buf.ToString();
            selections.Add(new GoferSelection(selector, text2, text2, matches.Count));

            if (text2.Length > 0)
            {
                if (combinedPlain.Length > 0) combinedPlain.Append("\n\n---\n\n");
                combinedPlain.Append(text2);
            }
        }

        var combined = combinedPlain.ToString();
        return (combined, combined, selections);
    }

    private static IReadOnlyList<Uri> ExtractLinks(Document doc, Uri pageUrl)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Uri>();
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(pageUrl, href, out var abs)) continue;
            if (abs.Scheme is not ("http" or "https")) continue;
            var key = NormalizeUrl(abs);
            if (seen.Add(key)) links.Add(abs);
        }
        return links;
    }

    private static string NormalizeUrl(Uri url)
    {
        var b = new UriBuilder(url) { Fragment = "" };
        return b.Uri.AbsoluteUri;
    }

    private static bool LooksLikeHtml(string? contentType, byte[] body)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var lower = contentType.ToLowerInvariant();
            if (lower.Contains("text/html")) return true;
            if (lower.Contains("application/xhtml")) return true;
            if (lower.StartsWith("text/")) return true;
            return false;
        }
        // No Content-Type: sniff magic bytes. HTML typically
        // starts with "<" after optional whitespace / BOM.
        int i = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) i = 3;
        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < body.Length && body[i] == (byte)'<';
    }

    private static string NormalizeWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        bool inWs = false;
        bool atStart = true;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!atStart) inWs = true;
            }
            else
            {
                if (inWs) sb.Append(' ');
                sb.Append(ch);
                inWs = false;
                atStart = false;
            }
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        await _opts.Output.DisposeAsync().ConfigureAwait(false);
    }

    private readonly record struct CrawlItem(Uri Url, int Depth);

    // Per-host politeness: one request at a time per host, plus a
    // minimum inter-request delay measured from when the previous
    // one finished. Cheap and correct — the semaphore serializes
    // access to the delay window.
    private sealed class HostGate
    {
        private readonly SemaphoreSlim _sem = new(1, 1);
        private DateTimeOffset _last = DateTimeOffset.MinValue;

        public async Task Enter(TimeSpan minDelay, CancellationToken ct)
        {
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            if (minDelay > TimeSpan.Zero)
            {
                var wait = _last + minDelay - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        public void Exit()
        {
            _last = DateTimeOffset.UtcNow;
            _sem.Release();
        }
    }
}

public sealed class GoferPageEventArgs(GoferResult result) : EventArgs
{
    public GoferResult Result { get; } = result;
}
