using Daisi.Broski.Engine.Html;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// DuckDuckGo HTML SERP (html.duckduckgo.com). DDG wraps result
/// links in a click-tracking redirect of the form
/// <c>//duckduckgo.com/l/?uddg=&lt;URL-encoded real URL&gt;&amp;rut=…</c>;
/// we unwrap the <c>uddg</c> param to land on the canonical URL.
/// </summary>
public sealed class DuckDuckGoProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "duckduckgo";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri($"https://html.duckduckgo.com/html/?q={Q(query)}");
        using var resp = await GetAsync(url, "text/html", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = HtmlTreeBuilder.Parse(html);

        var list = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in doc.QuerySelectorAll("div.result, div.web-result"))
        {
            var a = result.QuerySelector("a.result__a, a.result__url");
            if (a is null) continue;
            var href = a.GetAttribute("href");
            var title = a.TextContent?.Trim();
            var real = UnwrapRedirect(href, url);
            if (real is null || !seen.Add(real.AbsoluteUri)) continue;

            var snippetEl = result.QuerySelector("a.result__snippet, div.result__snippet");
            var snippet = snippetEl?.TextContent?.Trim();

            list.Add(new SearchResult(
                Source: Name,
                Url: real,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                Snippet: string.IsNullOrWhiteSpace(snippet) ? null : snippet));

            if (list.Count >= maxResults) break;
        }
        return list;
    }

    /// <summary>Decode DDG's /l/?uddg=... redirect into the real
    /// target URL. Falls through to the href unchanged when the
    /// redirect shape isn't recognized.</summary>
    private static Uri? UnwrapRedirect(string? href, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (!Uri.TryCreate(pageUrl, href, out var abs)) return null;
        if (abs.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && abs.AbsolutePath.EndsWith("/l/", StringComparison.Ordinal))
        {
            var uddg = GetQueryParam(abs.Query, "uddg");
            if (!string.IsNullOrEmpty(uddg)
                && Uri.TryCreate(uddg, UriKind.Absolute, out var inner)
                && inner.Scheme is "http" or "https")
            {
                return inner;
            }
        }
        return abs.Scheme is "http" or "https" ? abs : null;
    }

    private static string? GetQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var q = query[0] == '?' ? query[1..] : query;
        foreach (var pair in q.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.AsSpan(0, eq).SequenceEqual(name))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
