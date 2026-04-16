using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// OpenLibrary books search. Each hit becomes a link to the
/// /works/… page on openlibrary.org, where the crawler finds the
/// canonical metadata block (authors, first-publish date, etc.).
/// </summary>
public sealed class OpenLibraryProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "openlibrary";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://openlibrary.org/search.json?q={Q(query)}&limit={maxResults}");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("docs", out var docs)) return [];
        var list = new List<SearchResult>();
        foreach (var d in docs.EnumerateArray())
        {
            if (!d.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
            var key = k.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            var uri = new Uri($"https://openlibrary.org{key}");

            string? snippet = null;
            if (d.TryGetProperty("author_name", out var a) && a.ValueKind == JsonValueKind.Array
                && a.GetArrayLength() > 0)
                snippet = "by " + string.Join(", ", a.EnumerateArray()
                    .Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)));

            var extra = new Dictionary<string, string>();
            if (d.TryGetProperty("first_publish_year", out var y) && y.ValueKind == JsonValueKind.Number)
                extra["firstPublishYear"] = y.GetInt32().ToString();
            if (d.TryGetProperty("edition_count", out var ec) && ec.ValueKind == JsonValueKind.Number)
                extra["editions"] = ec.GetInt32().ToString();

            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Snippet: snippet,
                Extra: extra.Count > 0 ? extra : null));
        }
        return list;
    }
}
