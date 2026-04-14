using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Daisi.Broski.Engine.Net;

/// <summary>
/// Thin facade over <see cref="HttpClient"/> that owns cookie handling,
/// manual redirect following (capped), response size limiting, and a
/// fixed User-Agent.
///
/// We follow redirects manually rather than letting HttpClient do it
/// because we want to expose the full chain to the engine (navigation
/// events need every hop) and we want a single place to enforce the cap.
///
/// This is the bottom of the network stack for the engine. Everything
/// script-facing (<c>fetch</c>, <c>XMLHttpRequest</c>, navigation) flows
/// through this eventually.
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    private readonly HttpFetcherOptions _options;
    private readonly HttpClient _client;
    private readonly SocketsHttpHandler _handler;
    private bool _disposed;

    public HttpFetcher(HttpFetcherOptions? options = null)
    {
        _options = options ?? new HttpFetcherOptions();

        _handler = new SocketsHttpHandler
        {
            // We handle redirects ourselves — we want the full chain and
            // the ability to enforce a per-fetcher cap.
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _options.Cookies,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
        };

        _client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = _options.RequestTimeout,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
    }

    /// <summary>
    /// Fetch a URL, following up to <see cref="HttpFetcherOptions.MaxRedirects"/>
    /// 3xx responses along the way. The returned <see cref="FetchResult"/>
    /// always reflects the final (non-redirect) response.
    /// </summary>
    /// <exception cref="HttpFetcherException">
    /// Redirect cap exceeded, response body too large, or malformed Location.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Transport-level failure (DNS, TLS, connection refused, etc.).
    /// </exception>
    public Task<FetchResult> FetchAsync(Uri url, CancellationToken ct = default) =>
        FetchAsync(url, userAgentOverride: null, ct);

    /// <summary>Fetch with a per-request User-Agent override.
    /// Used by the font loader to coax Google Fonts into
    /// returning TTF instead of WOFF2 — the font service
    /// dispatches on UA, and a fresh modern UA gets WOFF2
    /// (which we don't yet decompress). An IE9-era UA gets
    /// plain TrueType tables our parser can read directly.</summary>
    public async Task<FetchResult> FetchAsync(
        Uri url, string? userAgentOverride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var chain = new List<Uri> { url };
        Uri current = url;
        int redirects = 0;

        while (true)
        {
            // Give the host-supplied interceptor a chance to
            // short-circuit before we hit the wire. Runs on
            // every hop in the redirect chain so a synthetic
            // response can replace any link in the chain
            // (useful for pinning a 302 → mock-200 path).
            if (_options.Interceptor is { } intercept)
            {
                var synthetic = intercept(new InterceptedRequest
                {
                    Url = current,
                    Method = "GET",
                });
                if (synthetic is not null)
                {
                    return BuildSyntheticResult(url, current, chain, synthetic);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (userAgentOverride is not null)
            {
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.ParseAdd(userAgentOverride);
            }
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (redirects >= _options.MaxRedirects)
                {
                    throw new HttpFetcherException(
                        $"Exceeded redirect cap ({_options.MaxRedirects}) starting at {url}.");
                }

                var next = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);

                // Guard against malformed Location headers pointing at unsupported schemes.
                if (next.Scheme is not ("http" or "https"))
                {
                    throw new HttpFetcherException(
                        $"Refusing to follow redirect to non-HTTP scheme: {next.Scheme}.");
                }

                chain.Add(next);
                current = next;
                redirects++;
                continue;
            }

            var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            return new FetchResult
            {
                RequestUrl = url,
                FinalUrl = current,
                Status = response.StatusCode,
                Body = body,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                RedirectChain = chain,
                Headers = SnapshotHeaders(response),
            };
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // ContentLength can be null (chunked) or a lie. Enforce the cap while streaming.
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > _options.MaxResponseBytes)
        {
            throw new HttpFetcherException(
                $"Response body of {contentLength} bytes exceeds cap of {_options.MaxResponseBytes}.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var rent = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(rent, ct).ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > _options.MaxResponseBytes)
            {
                throw new HttpFetcherException(
                    $"Response body exceeded cap of {_options.MaxResponseBytes} bytes while streaming.");
            }

            buffer.Write(rent, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Build a <see cref="FetchResult"/> from a host-
    /// supplied <see cref="InterceptedResponse"/>. Mirrors the
    /// shape of the real-network path so callers can't tell
    /// the difference. The <c>Content-Type</c> header gets
    /// added if the interceptor set one explicitly and didn't
    /// also set the headers map.</summary>
    private static FetchResult BuildSyntheticResult(
        Uri requestUrl, Uri finalUrl, List<Uri> chain,
        InterceptedResponse synthetic)
    {
        var headers = synthetic.Headers
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (synthetic.ContentType is { } ct
            && synthetic.Headers is null)
        {
            // Promote the explicit content-type into the
            // headers map so consumers that read through the
            // headers (the JS-side fetch path does this) see
            // the same value.
            var dict = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase) { [ "content-type" ] = new[] { ct } };
            headers = dict;
        }
        return new FetchResult
        {
            RequestUrl = requestUrl,
            FinalUrl = finalUrl,
            Status = (System.Net.HttpStatusCode)synthetic.Status,
            Body = synthetic.Body ?? Array.Empty<byte>(),
            ContentType = synthetic.ContentType,
            RedirectChain = chain,
            Headers = headers,
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotHeaders(
        HttpResponseMessage response)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in response.Headers)
        {
            dict[name] = values.ToArray();
        }
        foreach (var (name, values) in response.Content.Headers)
        {
            dict[name] = values.ToArray();
        }

        return dict;
    }

    private static bool IsRedirect(HttpStatusCode status) => status switch
    {
        HttpStatusCode.MovedPermanently => true,    // 301
        HttpStatusCode.Found => true,               // 302
        HttpStatusCode.SeeOther => true,            // 303
        HttpStatusCode.TemporaryRedirect => true,   // 307
        HttpStatusCode.PermanentRedirect => true,   // 308
        _ => false,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _handler.Dispose();
    }
}
