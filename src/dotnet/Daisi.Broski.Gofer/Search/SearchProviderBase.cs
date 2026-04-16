using System.Net.Http.Headers;

namespace Daisi.Broski.Gofer.Search;

/// <summary>
/// Boilerplate every provider shares: a reusable <see cref="HttpClient"/>
/// with the Gofer default User-Agent, friendly timeouts, and helper
/// methods for GET + JSON / GET + XML / GET + HTML. Providers that
/// need custom auth or headers override <see cref="ConfigureRequest"/>.
/// </summary>
public abstract class SearchProviderBase
{
    protected HttpClient Http { get; }

    protected SearchProviderBase(HttpClient? http = null)
    {
        Http = http ?? DefaultHttp.Value;
    }

    public abstract string Name { get; }

    /// <summary>Build + send a GET; hook point for per-provider
    /// headers (Accept, Authorization, …).</summary>
    protected virtual async Task<HttpResponseMessage> GetAsync(
        Uri url, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd(accept);
        ConfigureRequest(req);
        return await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Override to stamp provider-specific headers on every
    /// request (e.g. Authorization, X-Rate-Limit-Bypass).</summary>
    protected virtual void ConfigureRequest(HttpRequestMessage req) { }

    /// <summary>URL-encode and stitch into a query string.</summary>
    protected static string Q(string value) => Uri.EscapeDataString(value);

    /// <summary>Lazy singleton fallback — one HttpClient for the
    /// whole process when a caller doesn't supply their own. Reused
    /// instead of "new HttpClient per provider" to avoid socket
    /// exhaustion under high fan-out.</summary>
    private static readonly Lazy<HttpClient> DefaultHttp = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Daisi.Broski.Gofer/1.0 (+https://broski.daisi.ai)");
        // From-header is a sign of a cooperative crawler — some APIs
        // throttle less aggressively when it's present.
        client.DefaultRequestHeaders.From = "broski@daisi.net";
        return client;
    });
}
