using System.Net;

namespace Daisi.Broski.Engine.Net;

/// <summary>
/// Configuration for <see cref="HttpFetcher"/>. A single options instance
/// should outlive many fetches — the cookie jar is carried on it.
/// </summary>
public sealed class HttpFetcherOptions
{
    /// <summary>
    /// Maximum number of 3xx responses to follow before we give up.
    /// 20 matches what every mainstream browser uses. A value of 0 disables
    /// redirect following entirely.
    /// </summary>
    public int MaxRedirects { get; init; } = 20;

    /// <summary>
    /// User-Agent to advertise. Defaults to a recent Chromium string; see
    /// architecture.md §5.6 for why we do that by default.
    /// </summary>
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Cookie jar shared across fetches. Create a fresh instance per
    /// <see cref="BrowserSession"/> so sessions cannot cross-contaminate.
    /// </summary>
    public CookieContainer Cookies { get; init; } = new();

    /// <summary>
    /// Per-request hard timeout. The underlying HttpClient also honors
    /// <see cref="CancellationToken"/> passed to FetchAsync.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum response body size in bytes. Bodies exceeding this are
    /// truncated and <see cref="HttpFetcherException"/> is thrown.
    /// Default 50 MiB — generous for HTML/JS, tight enough to stop
    /// accidental gigabyte downloads from blowing the Job Object budget.
    /// </summary>
    public long MaxResponseBytes { get; init; } = 50L * 1024 * 1024;
}
