using Daisi.Broski.Gofer.Search.Providers;

namespace Daisi.Broski.Gofer.Search;

/// <summary>
/// Multi-search + crawl orchestrator. Runs every provider
/// concurrently for a given query, dedupes the combined hits by
/// URL, then hands the winners to a <see cref="GoferCrawler"/>
/// as seeds so every result page gets scraped into an LLM-ready
/// <see cref="GoferResult"/>. The pipeline swallows individual
/// provider failures — one flaky upstream shouldn't take down
/// the whole research run.
/// </summary>
public sealed class SearchPipeline
{
    private readonly IReadOnlyList<ISearchProvider> _providers;

    public SearchPipeline(params ISearchProvider[] providers)
        : this((IEnumerable<ISearchProvider>)providers) { }

    public SearchPipeline(IEnumerable<ISearchProvider> providers)
    {
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        if (_providers.Count == 0)
            throw new ArgumentException("SearchPipeline needs at least one provider.", nameof(providers));
    }

    /// <summary>Convenience constructor: build the pipeline from a
    /// <see cref="SearchSource"/> flag bitmask. Every flag bit that's
    /// set maps to a default-configured provider instance (shared
    /// HttpClient, default auth/env-derived tokens). Pass additional
    /// custom providers via <paramref name="extraProviders"/> — e.g.
    /// a second Wikipedia instance for a different language, or a
    /// fully custom <see cref="ISearchProvider"/>.</summary>
    public static SearchPipeline FromSources(
        SearchSource sources,
        HttpClient? sharedHttp = null,
        params ISearchProvider[] extraProviders)
    {
        var list = new List<ISearchProvider>();
        if (sources.HasFlag(SearchSource.Wikipedia))     list.Add(new WikipediaProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Arxiv))         list.Add(new ArxivProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.GitHub))        list.Add(new GitHubProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.HackerNews))    list.Add(new HackerNewsProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.StackExchange)) list.Add(new StackExchangeProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.CrossRef))      list.Add(new CrossRefProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.OpenLibrary))   list.Add(new OpenLibraryProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Reddit))        list.Add(new RedditProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.DuckDuckGo))    list.Add(new DuckDuckGoProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Brave))         list.Add(new BraveSearchProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Mojeek))        list.Add(new MojeekProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Gdelt))         list.Add(new GdeltProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.Guardian))      list.Add(new GuardianProvider(sharedHttp));
        if (sources.HasFlag(SearchSource.BingNews))      list.Add(new BingNewsProvider(sharedHttp));
        if (extraProviders is { Length: > 0 }) list.AddRange(extraProviders);
        return new SearchPipeline(list);
    }

    /// <summary>Run every provider in parallel, merge + dedup hits
    /// by normalized URL (fragment stripped, lower-cased host).
    /// Returns the unified list — does NOT crawl. Use this when
    /// you only want the URL list (for a ranker, a citation
    /// prompt, etc.) and don't need page contents.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int perProviderLimit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        var tasks = _providers
            .Select(p => SafeSearch(p, query, perProviderLimit, ct))
            .ToArray();
        var perProvider = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Dedup by normalized URL (fragment stripped, lowercased
        // host, trailing slash trimmed). First provider wins, so
        // earlier-listed providers' metadata is preserved.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SearchResult>();
        foreach (var batch in perProvider)
        foreach (var hit in batch)
        {
            var key = NormalizeForDedup(hit.Url);
            if (seen.Add(key)) merged.Add(hit);
        }
        return merged;
    }

    /// <summary>Full pipeline: search → dedup → crawl the top
    /// <paramref name="maxCrawled"/> URLs with Gofer. Returns the
    /// crawl results paired with the originating search hits so
    /// the LLM can see both the search attribution (which engine
    /// found this) and the extracted page content.</summary>
    public async Task<IReadOnlyList<SearchAndCrawlResult>> SearchAndCrawlAsync(
        string query,
        GoferOptions crawlerOptions,
        int perProviderLimit = 10,
        int maxCrawled = 20,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(crawlerOptions);

        var hits = await SearchAsync(query, perProviderLimit, ct).ConfigureAwait(false);
        var seeds = hits.Take(maxCrawled).Select(h => h.Url).ToList();
        if (seeds.Count == 0) return [];

        // Force a non-walking crawl regardless of caller's MaxDepth —
        // we already picked the pages we want; don't hop from them.
        // Users who want a two-level expansion can call Gofer
        // directly with the search URLs as seeds.
        var fixedOptions = Clone(crawlerOptions);
        fixedOptions.MaxDepth = 0;
        fixedOptions.MaxPages = Math.Max(fixedOptions.MaxPages, seeds.Count);
        fixedOptions.StayOnHost = false;

        // Map url → first search hit so we can re-attach search
        // metadata to each crawl result.
        var byUrl = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits)
            byUrl.TryAdd(NormalizeForDedup(h.Url), h);

        var pairs = new List<SearchAndCrawlResult>();
        var lockObj = new object();

        await using var gofer = new GoferCrawler(fixedOptions);
        gofer.PageScraped += (_, e) =>
        {
            byUrl.TryGetValue(NormalizeForDedup(e.Result.RequestedUrl), out var search);
            lock (lockObj)
                pairs.Add(new SearchAndCrawlResult(search, e.Result));
        };
        await gofer.RunAsync(seeds, ct).ConfigureAwait(false);
        return pairs;
    }

    private static async Task<IReadOnlyList<SearchResult>> SafeSearch(
        ISearchProvider p, string q, int n, CancellationToken ct)
    {
        try { return await p.SearchAsync(q, n, ct).ConfigureAwait(false); }
        catch { return []; }
    }

    private static string NormalizeForDedup(Uri u)
    {
        var b = new UriBuilder(u) { Fragment = "" };
        var s = b.Uri.AbsoluteUri;
        // Lowercase the scheme + host; leave the path case-sensitive
        // because some sites (Wikipedia, GitHub) do differentiate.
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return s;
        var hostEnd = s.IndexOf('/', schemeEnd + 3);
        if (hostEnd < 0) hostEnd = s.Length;
        var prefix = s[..hostEnd].ToLowerInvariant();
        var rest = s[hostEnd..];
        if (rest.Length > 1 && rest.EndsWith('/')) rest = rest[..^1];
        return prefix + rest;
    }

    private static GoferOptions Clone(GoferOptions src)
    {
        var dst = new GoferOptions
        {
            DegreeOfParallelism = src.DegreeOfParallelism,
            MaxPages = src.MaxPages,
            MaxDepth = src.MaxDepth,
            StayOnHost = src.StayOnHost,
            FollowLinks = src.FollowLinks,
            RequestTimeout = src.RequestTimeout,
            PerHostDelay = src.PerHostDelay,
            Output = src.Output,
            UrlFilter = src.UrlFilter,
        };
        // Replace default headers with the caller's (preserves custom
        // User-Agent, Authorization, etc.).
        dst.Headers.Clear();
        foreach (var kv in src.Headers) dst.Headers[kv.Key] = kv.Value;
        foreach (var s in src.Selectors) dst.Selectors.Add(s);
        return dst;
    }
}

/// <summary>Pair of (which search hit pointed at this URL) + (what
/// the crawler extracted from it). The <see cref="Search"/> field
/// is null when the crawl discovered a URL the search didn't
/// originally surface (shouldn't happen with MaxDepth=0, but the
/// shape is permissive).</summary>
public sealed record SearchAndCrawlResult(
    SearchResult? Search,
    GoferResult Crawl);
