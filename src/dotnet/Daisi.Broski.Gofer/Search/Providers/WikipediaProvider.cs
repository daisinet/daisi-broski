using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Wikipedia OpenSearch. Returns one hit per matching article —
/// English by default; pass a different language code to search
/// another wiki. OpenSearch returns four parallel arrays
/// (terms / titles / snippets / urls) which we zip into records.
/// </summary>
public sealed class WikipediaProvider(HttpClient? http = null, string lang = "en")
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => $"wikipedia-{lang}";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://{lang}.wikipedia.org/w/api.php?action=opensearch" +
            $"&search={Q(query)}&limit={maxResults}&format=json");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        // OpenSearch returns [query, [titles], [snippets], [urls]].
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 4) return [];
        var titles = root[1];
        var snippets = root[2];
        var urls = root[3];

        int count = Math.Min(titles.GetArrayLength(), maxResults);
        var list = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
        {
            var href = urls[i].GetString();
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: titles[i].GetString(),
                Snippet: snippets[i].GetString()));
        }
        return list;
    }
}
