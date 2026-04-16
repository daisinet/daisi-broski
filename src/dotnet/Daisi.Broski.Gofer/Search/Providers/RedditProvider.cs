using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Reddit site-wide search via <c>/search.json</c>. The JSON
/// endpoint is public (no OAuth) but rate-limits hard on
/// anonymous User-Agents — the Gofer default UA clears this.
/// </summary>
public sealed class RedditProvider(HttpClient? http = null, string? subreddit = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => string.IsNullOrEmpty(subreddit) ? "reddit" : $"reddit-{subreddit}";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var basePath = string.IsNullOrEmpty(subreddit)
            ? "https://www.reddit.com/search.json"
            : $"https://www.reddit.com/r/{Q(subreddit)}/search.json?restrict_sr=true";
        var url = new Uri(
            $"{basePath}{(basePath.Contains('?') ? "&" : "?")}" +
            $"q={Q(query)}&limit={Math.Min(maxResults, 100)}&sort=relevance");
        using var resp = await GetAsync(url, "application/json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
        if (!data.TryGetProperty("children", out var children)) return [];
        var list = new List<SearchResult>();
        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("data", out var d)) continue;
            if (!d.TryGetProperty("permalink", out var perm) || perm.ValueKind != JsonValueKind.String) continue;
            var uri = new Uri($"https://www.reddit.com{perm.GetString()}");

            var extra = new Dictionary<string, string>();
            if (d.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                extra["score"] = s.GetInt32().ToString();
            if (d.TryGetProperty("num_comments", out var c) && c.ValueKind == JsonValueKind.Number)
                extra["comments"] = c.GetInt32().ToString();
            if (d.TryGetProperty("subreddit", out var sr) && sr.ValueKind == JsonValueKind.String)
                extra["subreddit"] = sr.GetString()!;

            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Snippet: d.TryGetProperty("selftext", out var st) && st.ValueKind == JsonValueKind.String
                    ? Trim(st.GetString(), 300) : null,
                Extra: extra.Count > 0 ? extra : null));
        }
        return list;
    }

    private static string? Trim(string? s, int n)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n] + "…");
}
