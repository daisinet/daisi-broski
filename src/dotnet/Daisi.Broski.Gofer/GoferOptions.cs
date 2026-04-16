using Daisi.Broski.Gofer.Outputs;

namespace Daisi.Broski.Gofer;

/// <summary>
/// Configuration for a single crawl. All knobs are tuned so that
/// the default values produce a polite, fast, correctness-first
/// crawl on typical hardware; override selectively.
/// </summary>
public sealed class GoferOptions
{
    /// <summary>Number of concurrent fetch workers. Defaults to
    /// 2× logical processor count — HTTP crawls are I/O-bound so
    /// more-than-cores works well.</summary>
    public int DegreeOfParallelism { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>Cap on the number of unique URLs to visit. A hard
    /// safety rail — the crawl stops dequeuing once this many
    /// pages have been processed.</summary>
    public int MaxPages { get; set; } = 100;

    /// <summary>Max link-hops from any seed. 0 = only the seeds,
    /// 1 = seeds + their direct out-links, etc. Defaults to 2.</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Only enqueue links whose host matches the seed's
    /// host (or a seed's host, when multiple seeds are supplied).
    /// Defaults to true — crawls escape containment fast.</summary>
    public bool StayOnHost { get; set; } = true;

    /// <summary>When false, links are not followed — the crawl
    /// only scrapes the seeds. Handy for a batch scrape of a list
    /// of URLs.</summary>
    public bool FollowLinks { get; set; } = true;

    /// <summary>Per-request timeout. Applies to the entire
    /// response (headers + body) via the shared HttpClient.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum delay between consecutive requests to the
    /// same host. Politeness rail — set to TimeSpan.Zero for
    /// internal / permissioned crawls.</summary>
    public TimeSpan PerHostDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Custom headers merged into every request. Common
    /// uses: User-Agent, Accept-Language, Authorization. Host is
    /// always set by HttpClient; attempts to override it are
    /// ignored.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "Daisi.Broski.Gofer/1.0 (+https://broski.daisi.ai)",
        ["Accept"] = "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8",
    };

    /// <summary>CSS selectors to project each page down to. Empty
    /// = emit the full readability-extracted article. Non-empty =
    /// emit one <see cref="GoferSelection"/> per selector plus a
    /// <see cref="GoferResult.Markdown"/> that's the concatenation
    /// of all matches in selector-order. Supports the full CSS
    /// grammar the Engine's QuerySelectorAll accepts.</summary>
    public IList<string> Selectors { get; } = new List<string>();

    /// <summary>Sink for every result. Defaults to a no-op —
    /// consumers who wire only the <see cref="GoferCrawler.PageScraped"/>
    /// event don't need an output at all.</summary>
    public IGoferOutput Output { get; set; } = NullOutput.Instance;

    /// <summary>Filter applied to each candidate URL before it's
    /// enqueued (after host + depth + dedup checks). Return false
    /// to skip. Null = accept everything.</summary>
    public Func<Uri, bool>? UrlFilter { get; set; }
}
