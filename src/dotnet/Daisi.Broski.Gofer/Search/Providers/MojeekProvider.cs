using Daisi.Broski.Engine.Html;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Mojeek search — an independent index (not a frontend for
/// Bing/Google). Traditional SERP with <c>ul.results-standard</c>
/// containing <c>li</c> items; each item's <c>a.ob</c> is the
/// result anchor.
/// </summary>
public sealed class MojeekProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "mojeek";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri($"https://www.mojeek.com/search?q={Q(query)}");
        using var resp = await GetAsync(url, "text/html", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = HtmlTreeBuilder.Parse(html);

        var list = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try a couple of selectors — Mojeek has rotated the
        // layout a few times and we want to survive a single
        // container rename without shipping a new release.
        var items = doc.QuerySelectorAll("ul.results-standard > li, li.result");
        if (items.Count == 0)
            items = doc.QuerySelectorAll("li.result, div.result");

        foreach (var li in items)
        {
            var a = li.QuerySelector("a.ob, a.title, h2 a, a.result-link");
            if (a is null) continue;
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(url, href, out var abs)) continue;
            if (abs.Scheme is not ("http" or "https")) continue;
            if (abs.Host.Equals("www.mojeek.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(abs.AbsoluteUri)) continue;

            var snippetEl = li.QuerySelector("p.s, p.snippet, p.desc, div.s");

            list.Add(new SearchResult(
                Source: Name,
                Url: abs,
                Title: a.TextContent?.Trim(),
                Snippet: snippetEl?.TextContent?.Trim()));

            if (list.Count >= maxResults) break;
        }
        return list;
    }
}
