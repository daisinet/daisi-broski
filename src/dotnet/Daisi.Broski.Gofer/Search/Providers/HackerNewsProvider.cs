using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Hacker News search via Algolia. The <c>/search</c> endpoint
/// returns popularity-ranked hits; <c>/search_by_date</c> sorts
/// chronologically — we use the former because LLM research
/// benefits from top-weighted results.
/// </summary>
public sealed class HackerNewsProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "hackernews";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://hn.algolia.com/api/v1/search?query={Q(query)}" +
            $"&hitsPerPage={maxResults}&tags=story");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("hits", out var hits)) return [];
        var list = new List<SearchResult>();
        foreach (var hit in hits.EnumerateArray())
        {
            // Prefer the story's external URL; fall back to the HN
            // discussion page when the story is a "Show HN"-style
            // post with no external link.
            string? href = null;
            if (hit.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                href = u.GetString();
            if (string.IsNullOrEmpty(href)
                && hit.TryGetProperty("objectID", out var id))
                href = $"https://news.ycombinator.com/item?id={id.GetString()}";
            if (string.IsNullOrEmpty(href)
                || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;

            var extra = new Dictionary<string, string>();
            if (hit.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number)
                extra["points"] = p.GetInt32().ToString();
            if (hit.TryGetProperty("num_comments", out var c) && c.ValueKind == JsonValueKind.Number)
                extra["comments"] = c.GetInt32().ToString();
            if (hit.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String)
                extra["author"] = a.GetString()!;

            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: hit.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Snippet: hit.TryGetProperty("story_text", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null,
                Extra: extra.Count > 0 ? extra : null));
        }
        return list;
    }
}
