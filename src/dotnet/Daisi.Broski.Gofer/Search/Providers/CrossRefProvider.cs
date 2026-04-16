using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// CrossRef works search — scholarly articles via DOI. Each hit
/// resolves to its DOI URL (https://doi.org/...), which the
/// crawler follows to the publisher's landing page.
/// </summary>
public sealed class CrossRefProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "crossref";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://api.crossref.org/works?query={Q(query)}" +
            $"&rows={Math.Min(maxResults, 100)}");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("message", out var msg)) return [];
        if (!msg.TryGetProperty("items", out var items)) return [];
        var list = new List<SearchResult>();
        foreach (var item in items.EnumerateArray())
        {
            // Prefer URL; fall back to DOI.
            string? href = null;
            if (item.TryGetProperty("URL", out var u) && u.ValueKind == JsonValueKind.String)
                href = u.GetString();
            if (string.IsNullOrEmpty(href) && item.TryGetProperty("DOI", out var d))
                href = $"https://doi.org/{d.GetString()}";
            if (string.IsNullOrEmpty(href)
                || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;

            string? title = null;
            if (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Array
                && t.GetArrayLength() > 0)
                title = t[0].GetString();

            string? snippet = null;
            if (item.TryGetProperty("abstract", out var abs) && abs.ValueKind == JsonValueKind.String)
                snippet = abs.GetString();

            var extra = new Dictionary<string, string>();
            if (item.TryGetProperty("DOI", out var doi)) extra["doi"] = doi.GetString() ?? "";
            if (item.TryGetProperty("container-title", out var ct2) && ct2.ValueKind == JsonValueKind.Array
                && ct2.GetArrayLength() > 0)
                extra["journal"] = ct2[0].GetString() ?? "";

            list.Add(new SearchResult(Name, uri, title, snippet, extra.Count > 0 ? extra : null));
        }
        return list;
    }
}
