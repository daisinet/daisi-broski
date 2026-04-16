using System.Text.Json.Serialization;

namespace Daisi.Broski.Gofer;

/// <summary>
/// Per-page output record. Shaped for direct consumption by an
/// LLM: the crawler always populates <see cref="Markdown"/> (full
/// article extraction by default, or the concatenated contents
/// of the user's selector list when supplied) plus structured
/// <see cref="Selections"/> when selectors were used, so prompts
/// can either splat the whole thing or cherry-pick sections.
/// </summary>
public sealed class GoferResult
{
    /// <summary>Final URL after redirect chain — this is the
    /// canonical address the LLM should cite.</summary>
    public required Uri Url { get; init; }

    /// <summary>Original seed / frontier URL that pointed here.
    /// Equal to <see cref="Url"/> when there were no redirects.</summary>
    public required Uri RequestedUrl { get; init; }

    public required int Status { get; init; }
    public required DateTimeOffset FetchedAtUtc { get; init; }
    public required long DurationMs { get; init; }
    public required long Bytes { get; init; }
    public required int Depth { get; init; }

    public string? Title { get; init; }
    public string? Byline { get; init; }
    public string? Description { get; init; }
    public string? Lang { get; init; }
    public string? SiteName { get; init; }

    /// <summary>Markdown of the selected content — either the
    /// full article (no selectors) or the concatenated subtrees
    /// matching the selectors, in the order supplied.</summary>
    public required string Markdown { get; init; }

    /// <summary>Plain-text fallback mirroring <see cref="Markdown"/>.
    /// Useful when the LLM's tokenizer chokes on markdown syntax
    /// or when embedding without surrounding structure.</summary>
    public required string PlainText { get; init; }

    /// <summary>One entry per selector in <see cref="GoferOptions.Selectors"/>
    /// (empty when no selectors were supplied). Lets callers feed
    /// just the honed part into an LLM while keeping the full
    /// <see cref="Markdown"/> for fallback.</summary>
    public IReadOnlyList<GoferSelection> Selections { get; init; } = [];

    /// <summary>Out-links discovered on the page. The crawler uses
    /// these internally to walk; downstream consumers can use the
    /// list as a knowledge-graph seed.</summary>
    public IReadOnlyList<Uri> Links { get; init; } = [];

    /// <summary>Populated when the fetch or parse threw — always
    /// null on happy path. <see cref="Status"/> will still reflect
    /// a numeric HTTP-ish code (0 for network-level failures).</summary>
    public string? Error { get; init; }
}

public sealed record GoferSelection(
    string Selector,
    string Markdown,
    string PlainText,
    [property: JsonPropertyName("matchCount")] int MatchCount);
