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
    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var chain = new List<Uri> { url };
        Uri current = url;
        int redirects = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
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
