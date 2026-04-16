using System.Xml.Linq;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// Bing News RSS feed. Returns global news aggregation results
/// for a search query. Bing wraps most result links in a
/// <c>bing.com/news/apiclick.aspx</c> click-tracking redirect —
/// we decode the <c>url</c> query parameter to land on the real
/// publisher article. Feed also exposes each item's source (NYT,
/// Reuters, …) via the <c>News:Source</c> custom namespace.
/// </summary>
public sealed class BingNewsProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "bingnews";

    private static readonly XNamespace NewsNs = "https://www.bing.com/news/search";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri($"https://www.bing.com/news/search?q={Q(query)}&format=RSS");
        using var resp = await GetAsync(url, "application/rss+xml,application/xml,text/xml", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return []; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SearchResult>();

        foreach (var item in doc.Descendants("item"))
        {
            var link = (string?)item.Element("link");
            var real = UnwrapApiClick(link);
            if (real is null) continue;
            if (!seen.Add(real.AbsoluteUri)) continue;

            // Item-level namespace lookup — the feed declares a
            // News: namespace whose URI can differ by locale, so
            // match the element by local name instead of a fixed
            // XName.
            string? source = null;
            foreach (var el in item.Elements())
            {
                if (el.Name.LocalName == "Source")
                {
                    source = el.Value;
                    break;
                }
            }

            var extra = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(source)) extra["source"] = source;
            var pubDate = (string?)item.Element("pubDate");
            if (!string.IsNullOrEmpty(pubDate)) extra["publishedAt"] = pubDate;

            list.Add(new SearchResult(
                Source: Name,
                Url: real,
                Title: (string?)item.Element("title"),
                Snippet: (string?)item.Element("description"),
                Extra: extra.Count > 0 ? extra : null));

            if (list.Count >= maxResults) break;
        }
        return list;
    }

    private static Uri? UnwrapApiClick(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var abs)) return null;
        if (abs.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)
            && abs.AbsolutePath.EndsWith("apiclick.aspx", StringComparison.OrdinalIgnoreCase))
        {
            var target = GetQueryParam(abs.Query, "url");
            if (!string.IsNullOrEmpty(target)
                && Uri.TryCreate(target, UriKind.Absolute, out var inner)
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
