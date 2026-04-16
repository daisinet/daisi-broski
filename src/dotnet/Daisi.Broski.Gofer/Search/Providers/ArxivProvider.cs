using System.Xml.Linq;

namespace Daisi.Broski.Gofer.Search.Providers;

/// <summary>
/// arXiv's query API returns an Atom feed — one &lt;entry&gt; per
/// paper. We map each entry into a SearchResult whose URL is the
/// abstract page (https://arxiv.org/abs/…) so the crawler picks
/// up the human-readable view rather than the PDF.
/// </summary>
public sealed class ArxivProvider(HttpClient? http = null)
    : SearchProviderBase(http), ISearchProvider
{
    public override string Name => "arxiv";

    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var url = new Uri(
            $"https://export.arxiv.org/api/query?search_query=all:{Q(query)}" +
            $"&start=0&max_results={maxResults}");
        using var resp = await GetAsync(url, "application/atom+xml,application/xml", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return []; }

        var list = new List<SearchResult>();
        foreach (var entry in doc.Descendants(AtomNs + "entry"))
        {
            var title = (string?)entry.Element(AtomNs + "title");
            var summary = (string?)entry.Element(AtomNs + "summary");
            // Prefer the abs/ link (Atom links with rel="alternate"
            // have no rel attribute in arXiv's feed); fallback to id.
            Uri? u = null;
            foreach (var link in entry.Elements(AtomNs + "link"))
            {
                var type = (string?)link.Attribute("type");
                var href = (string?)link.Attribute("href");
                if (href is null) continue;
                if (type == "text/html" && Uri.TryCreate(href, UriKind.Absolute, out u)) break;
            }
            u ??= Uri.TryCreate((string?)entry.Element(AtomNs + "id") ?? "",
                UriKind.Absolute, out var id) ? id : null;
            if (u is null) continue;

            var authors = entry.Elements(AtomNs + "author")
                .Select(a => (string?)a.Element(AtomNs + "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();
            var extra = new Dictionary<string, string>();
            if (authors.Length > 0) extra["authors"] = string.Join("; ", authors);

            list.Add(new SearchResult(
                Source: Name,
                Url: u,
                Title: title?.Trim(),
                Snippet: summary?.Trim(),
                Extra: extra.Count > 0 ? extra : null));

            if (list.Count >= maxResults) break;
        }
        return list;
    }
}
