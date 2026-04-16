namespace Daisi.Broski.Gofer.Search;

/// <summary>
/// [Flags] enumeration of first-party providers the pipeline can
/// turn on and off in one call. Composable with bitwise-OR:
/// <c>SearchSource.Wikipedia | SearchSource.Arxiv | SearchSource.GitHub</c>.
/// Use <see cref="All"/> for every built-in provider, or
/// <see cref="Web"/> for the HTML SERP subset.
///
/// <para>Providers not listed here (or custom ones) can still be
/// added directly by passing <see cref="ISearchProvider"/>
/// instances to the <see cref="SearchPipeline"/> constructor.</para>
/// </summary>
[Flags]
public enum SearchSource
{
    None          = 0,
    Wikipedia     = 1 << 0,
    Arxiv         = 1 << 1,
    GitHub        = 1 << 2,
    HackerNews    = 1 << 3,
    StackExchange = 1 << 4,
    CrossRef      = 1 << 5,
    OpenLibrary   = 1 << 6,
    Reddit        = 1 << 7,
    DuckDuckGo    = 1 << 8,
    Brave         = 1 << 9,
    Mojeek        = 1 << 10,
    Gdelt         = 1 << 11,
    Guardian      = 1 << 12,
    BingNews      = 1 << 13,

    /// <summary>The three HTML SERPs — use as a broad-web net.</summary>
    Web = DuckDuckGo | Brave | Mojeek,

    /// <summary>News aggregators — GDELT (global, 65+ langs),
    /// Guardian Open Platform, and Bing News RSS. Best for
    /// current-events / breaking-news research.</summary>
    News = Gdelt | Guardian | BingNews,

    /// <summary>The scholarly / reference set — Wikipedia, arXiv,
    /// CrossRef, OpenLibrary.</summary>
    Scholarly = Wikipedia | Arxiv | CrossRef | OpenLibrary,

    /// <summary>The tech / community set — GitHub, HN, Stack
    /// Exchange, Reddit.</summary>
    Community = GitHub | HackerNews | StackExchange | Reddit,

    All = Wikipedia | Arxiv | GitHub | HackerNews | StackExchange
        | CrossRef | OpenLibrary | Reddit | DuckDuckGo | Brave | Mojeek
        | Gdelt | Guardian | BingNews,
}
