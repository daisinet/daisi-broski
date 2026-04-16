namespace Daisi.Broski.Gofer.Search;

/// <summary>
/// Runs a search against a single upstream (Wikipedia, arXiv,
/// GitHub, DuckDuckGo, …) and returns hits. Providers are
/// independent, stateless, and safe to call concurrently.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable label used as <see cref="SearchResult.Source"/>
    /// on every hit this provider emits — e.g. <c>"wikipedia"</c>.
    /// The pipeline uses it for dedup and for attribution.</summary>
    string Name { get; }

    /// <summary>Run the query; return up to <paramref name="maxResults"/>
    /// hits. Implementations that can't return any hits (zero matches,
    /// rate-limit, transient failure) must return an empty list rather
    /// than throwing — the pipeline swallows provider failures so one
    /// flaky upstream doesn't take down the whole search.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct);
}
