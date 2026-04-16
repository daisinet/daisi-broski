using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Stack Exchange 2.3 search. Defaults to stackoverflow; pass a
/// different <c>site</c> (serverfault, superuser, askubuntu, …).
/// The API serves gzipped by default — DefaultHttp has
/// AutomaticDecompression on, so the raw JSON lands directly.
/// </summary>
public sealed class StackExchangeProvider(HttpClient? http = null, string site = "stackoverflow", string? key = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => $"stackexchange-{site}";

    private readonly string? _key = key ?? Environment.GetEnvironmentVariable("STACKEXCHANGE_KEY");

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var pageSize = Math.Min(maxResults, 100);
        var url = new Uri(
            $"https://api.stackexchange.com/2.3/search/advanced?order=desc&sort=votes" +
            $"&q={Q(query)}&site={site}&pagesize={pageSize}" +
            (string.IsNullOrEmpty(_key) ? "" : $"&key={Q(_key)}"));
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
        var list = new List<SearchResult>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("link", out var link) || link.ValueKind != JsonValueKind.String) continue;
            if (!Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)) continue;
            var extra = new Dictionary<string, string>();
            if (item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                extra["score"] = s.GetInt32().ToString();
            if (item.TryGetProperty("answer_count", out var ac) && ac.ValueKind == JsonValueKind.Number)
                extra["answers"] = ac.GetInt32().ToString();
            if (item.TryGetProperty("is_answered", out var ia))
                extra["answered"] = ia.GetBoolean() ? "true" : "false";

            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    // StackExchange returns HTML-encoded titles.
                    ? System.Net.WebUtility.HtmlDecode(t.GetString()!)
                    : null,
                Extra: extra));
        }
        return list;
    }
}
