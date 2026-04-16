using Daisi.Broski.Gofer.Search;
using Daisi.Broski.Gofer.Search.Providers;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Gofer;

/// <summary>
/// Live integration tests — hit the real upstream endpoints.
/// Each test is resilient to transient failures (empty result set
/// is treated as a skip, not a failure) so CI doesn't churn when
/// a provider blips. Runs serially (one xunit collection) to be
/// polite to the shared endpoints.
/// </summary>
[Collection("live-search")]
public class SearchProviderLiveTests
{
    private const string Query = "rust programming language";
    private const int MaxResults = 5;

    private static CancellationToken Timeout(int seconds = 20)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static void AssertHitsUsable(IReadOnlyList<SearchResult> hits, string name)
    {
        // "Zero hits" is ambiguous — it could be a bot block, a
        // network blip, or a real empty result. We assert that WHEN
        // there are hits, the shape is usable; otherwise we note
        // the skip reason in the message so test output is
        // diagnostic.
        if (hits.Count == 0)
        {
            Assert.Skip($"{name} returned 0 hits (rate-limited / network / index miss) — test indeterminate.");
            return;
        }
        Assert.All(hits, h =>
        {
            Assert.Equal(name, h.Source);
            Assert.True(h.Url.IsAbsoluteUri, $"{name} returned non-absolute URL: {h.Url}");
            Assert.Contains(h.Url.Scheme, new[] { "http", "https" });
        });
    }

    [Fact] public async Task Wikipedia_returns_hits()
    {
        var hits = await new WikipediaProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "wikipedia-en");
    }

    [Fact] public async Task Arxiv_returns_hits()
    {
        var hits = await new ArxivProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "arxiv");
    }

    [Fact] public async Task GitHub_returns_hits()
    {
        var hits = await new GitHubProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "github");
    }

    [Fact] public async Task HackerNews_returns_hits()
    {
        var hits = await new HackerNewsProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "hackernews");
    }

    [Fact] public async Task StackExchange_returns_hits()
    {
        var hits = await new StackExchangeProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "stackexchange-stackoverflow");
    }

    [Fact] public async Task CrossRef_returns_hits()
    {
        var hits = await new CrossRefProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "crossref");
    }

    [Fact] public async Task OpenLibrary_returns_hits()
    {
        var hits = await new OpenLibraryProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "openlibrary");
    }

    [Fact] public async Task Reddit_returns_hits()
    {
        var hits = await new RedditProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "reddit");
    }

    [Fact] public async Task DuckDuckGo_returns_hits()
    {
        var hits = await new DuckDuckGoProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "duckduckgo");
    }

    [Fact] public async Task Brave_returns_hits()
    {
        var hits = await new BraveSearchProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "brave");
    }

    [Fact] public async Task Mojeek_returns_hits()
    {
        var hits = await new MojeekProvider().SearchAsync(Query, MaxResults, Timeout());
        AssertHitsUsable(hits, "mojeek");
    }

    // News-specific providers use a news-shaped query so the index
    // is more likely to have a non-empty response.
    private const string NewsQuery = "artificial intelligence";

    [Fact] public async Task Gdelt_returns_hits()
    {
        var hits = await new GdeltProvider().SearchAsync(NewsQuery, MaxResults, Timeout());
        AssertHitsUsable(hits, "gdelt");
    }

    [Fact] public async Task Guardian_returns_hits()
    {
        var hits = await new GuardianProvider().SearchAsync(NewsQuery, MaxResults, Timeout());
        AssertHitsUsable(hits, "guardian");
    }

    [Fact] public async Task BingNews_returns_hits()
    {
        var hits = await new BingNewsProvider().SearchAsync(NewsQuery, MaxResults, Timeout());
        AssertHitsUsable(hits, "bingnews");
    }
}

/// <summary>
/// Smoke test for the multi-search + crawl pipeline. Picks a
/// small, reliable subset (Wikipedia + arXiv + GitHub) so the
/// run doesn't take forever, then verifies the seed URLs round-
/// trip into GoferResults with non-empty markdown.
/// </summary>
[Collection("live-search")]
public class SearchPipelineLiveTests
{
    [Fact]
    public async Task Pipeline_searches_dedupes_and_crawls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pipeline = SearchPipeline.FromSources(
            SearchSource.Wikipedia | SearchSource.Arxiv | SearchSource.GitHub);

        var hits = await pipeline.SearchAsync("rust programming", perProviderLimit: 3, cts.Token);
        if (hits.Count == 0)
        {
            Assert.Skip("All 3 providers returned 0 hits — test indeterminate.");
            return;
        }

        // Each hit is unique (dedup worked) and attributed.
        var urls = hits.Select(h => h.Url.AbsoluteUri.ToLowerInvariant()).ToHashSet();
        Assert.Equal(urls.Count, hits.Count);
        Assert.Contains(hits, h => h.Source.StartsWith("wikipedia"));

        // Crawl the top 3 and confirm we got real content back.
        var crawlerOpts = new Daisi.Broski.Gofer.GoferOptions
        {
            DegreeOfParallelism = 4,
            PerHostDelay = TimeSpan.Zero,
            RequestTimeout = TimeSpan.FromSeconds(20),
        };
        var results = await pipeline.SearchAndCrawlAsync(
            "rust programming", crawlerOpts,
            perProviderLimit: 3, maxCrawled: 3, cts.Token);

        if (results.Count == 0)
        {
            Assert.Skip("Crawl produced 0 results (upstream 4xx / timeout).");
            return;
        }

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Crawl);
            // Search attribution present for every crawl result.
            Assert.NotNull(r.Search);
        });
        Assert.Contains(results, r => !string.IsNullOrEmpty(r.Crawl.Markdown));
    }
}
