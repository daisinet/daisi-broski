using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// The Guardian Open Platform — search across ~2M Guardian
/// articles going back to 1999. The free "test" key (used when
/// no env var is set) is rate-limited to ~12 calls/sec / 5K
/// calls/day, plenty for research queries; callers can drop in
/// a real key via the <c>GUARDIAN_API_KEY</c> environment variable
/// (obtainable free from open-platform.theguardian.com).
/// </summary>
public sealed class GuardianProvider(HttpClient? http = null, string? apiKey = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "guardian";

    private readonly string _apiKey = apiKey
        ?? Environment.GetEnvironmentVariable("GUARDIAN_API_KEY")
        ?? "test";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://content.guardianapis.com/search?q={Q(query)}" +
            $"&api-key={Q(_apiKey)}&show-fields=trailText" +
            $"&page-size={Math.Min(maxResults, 50)}");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("response", out var r)) return [];
        if (!r.TryGetProperty("results", out var results)) return [];
        var list = new List<SearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("webUrl", out var u) || u.ValueKind != JsonValueKind.String) continue;
            if (!Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)) continue;

            string? snippet = null;
            if (item.TryGetProperty("fields", out var f)
                && f.TryGetProperty("trailText", out var tt)
                && tt.ValueKind == JsonValueKind.String)
                snippet = tt.GetString();

            var extra = new Dictionary<string, string>();
            if (item.TryGetProperty("sectionName", out var sn) && sn.ValueKind == JsonValueKind.String)
                extra["section"] = sn.GetString()!;
            if (item.TryGetProperty("webPublicationDate", out var d) && d.ValueKind == JsonValueKind.String)
                extra["publishedAt"] = d.GetString()!;

            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: item.TryGetProperty("webTitle", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Snippet: snippet,
                Extra: extra.Count > 0 ? extra : null));
        }
        return list;
    }
}
