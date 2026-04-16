using Daisi.Broski.Engine.Html;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Brave Search HTML SERP. Brave uses Svelte with hashed class
/// suffixes (<c>svelte-1rq4ngz</c>) that rotate between deploys,
/// so we match by prefix: <c>div.snippet</c> is the container,
/// <c>a[href]</c> inside gives the link, <c>.title</c> / <c>.snippet-description</c>
/// give visible text. Skip infobox / sidebar cards (no canonical
/// result URL).
/// </summary>
public sealed class BraveSearchProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "brave";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri($"https://search.brave.com/search?q={Q(query)}&source=web");
        using var resp = await GetAsync(url, "text/html", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = HtmlTreeBuilder.Parse(html);

        var list = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snippet in doc.QuerySelectorAll("div.snippet, div.result-wrapper"))
        {
            var a = snippet.QuerySelector("a[href]");
            if (a is null) continue;
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(url, href, out var abs)) continue;
            if (abs.Scheme is not ("http" or "https")) continue;
            // Brave sends some internal routes back through search.brave.com;
            // skip those as they're not real external hits.
            if (abs.Host.Equals("search.brave.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(abs.AbsoluteUri)) continue;

            var titleEl = snippet.QuerySelector(".title, h1, h2, h3");
            var snippetEl = snippet.QuerySelector(".snippet-description, .snippet-content");

            list.Add(new SearchResult(
                Source: Name,
                Url: abs,
                Title: titleEl?.TextContent?.Trim(),
                Snippet: snippetEl?.TextContent?.Trim()));

            if (list.Count >= maxResults) break;
        }
        return list;
    }
}
