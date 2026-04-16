using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// GDELT Doc API — a free, global news-aggregation search across
/// tens of thousands of outlets in 65+ languages. No API key
/// required; the service does rate-limit anonymous callers, so
/// the shared <see cref="SearchProviderBase"/> User-Agent
/// identifies us as a cooperative crawler to get a softer cap.
/// </summary>
public sealed class GdeltProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "gdelt";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://api.gdeltproject.org/api/v2/doc/doc?query={Q(query)}" +
            $"&mode=artlist&format=json&maxrecords={Math.Min(maxResults, 250)}" +
            $"&sort=hybridrel");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // GDELT occasionally returns non-JSON on rate-limit — the
        // TryParse guard swallows that silently per the provider
        // contract ("one flaky upstream shouldn't take down the run").
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { return []; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("articles", out var arts)) return [];
            var list = new List<SearchResult>();
            foreach (var art in arts.EnumerateArray())
            {
                var href = art.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() : null;
                if (string.IsNullOrEmpty(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;

                var extra = new Dictionary<string, string>();
                if (art.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String)
                    extra["domain"] = d.GetString()!;
                if (art.TryGetProperty("seendate", out var sd) && sd.ValueKind == JsonValueKind.String)
                    extra["seenDate"] = sd.GetString()!;
                if (art.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
                    extra["language"] = lang.GetString()!;
                if (art.TryGetProperty("sourcecountry", out var sc) && sc.ValueKind == JsonValueKind.String)
                    extra["country"] = sc.GetString()!;

                list.Add(new SearchResult(
                    Source: Name,
                    Url: uri,
                    Title: art.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null,
                    Snippet: null,
                    Extra: extra.Count > 0 ? extra : null));

                if (list.Count >= maxResults) break;
            }
            return list;
        }
    }
}
