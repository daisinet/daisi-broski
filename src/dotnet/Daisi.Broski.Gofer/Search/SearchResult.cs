namespace Daisi.Broski.Gofer.Search;

/// <summary>
/// A single search hit returned by an <see cref="ISearchProvider"/>.
/// The shape is deliberately thin: the only guaranteed fields are a
/// URL and a source label so the pipeline can dedupe and attribute;
/// title / snippet are best-effort, and <see cref="Extra"/> carries
/// provider-specific metadata (authors, stars, DOI, subreddit, …)
/// for callers that want more than a URL list.
/// </summary>
public sealed record SearchResult(
    string Source,
    Uri Url,
    string? Title,
    string? Snippet = null,
    IReadOnlyDictionary<string, string>? Extra = null);
