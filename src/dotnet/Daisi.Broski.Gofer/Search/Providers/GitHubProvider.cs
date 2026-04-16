using System.Text.Json;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// GitHub repository search. Honors a PAT when
/// <c>GITHUB_TOKEN</c> is in the process environment — lifts the
/// 10-rpm rate limit up to 30 rpm without a token, 5000 rpm with.
/// </summary>
public sealed class GitHubProvider(HttpClient? http = null, string? token = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "github";

    private readonly string? _token = token ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    protected override void ConfigureRequest(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(_token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://api.github.com/search/repositories?q={Q(query)}" +
            $"&per_page={Math.Min(maxResults, 100)}&sort=stars&order=desc");
        using var resp = await GetAsync(url, "application/vnd.github+json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
        var list = new List<SearchResult>();
        foreach (var item in items.EnumerateArray())
        {
            var htmlUrl = item.GetProperty("html_url").GetString();
            if (string.IsNullOrEmpty(htmlUrl) ||
                !Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri)) continue;
            var extra = new Dictionary<string, string>
            {
                ["stars"] = item.GetProperty("stargazers_count").GetInt32().ToString(),
                ["language"] = item.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                    ? lang.GetString()! : "",
            };
            list.Add(new SearchResult(
                Source: Name,
                Url: uri,
                Title: item.GetProperty("full_name").GetString(),
                Snippet: item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() : null,
                Extra: extra));
            if (list.Count >= maxResults) break;
        }
        return list;
    }
}
