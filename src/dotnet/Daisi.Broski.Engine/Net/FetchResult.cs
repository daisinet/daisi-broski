using System.Net;

namespace Daisi.Broski.Engine.Net;

/// <summary>
/// The outcome of a single <see cref="HttpFetcher.FetchAsync"/> call.
/// Carries the final response body, metadata, and the full redirect chain
/// that led to it.
/// </summary>
public sealed class FetchResult
{
    public required Uri RequestUrl { get; init; }

    /// <summary>The URL of the response after following any redirects.</summary>
    public required Uri FinalUrl { get; init; }

    public required HttpStatusCode Status { get; init; }

    /// <summary>Response body bytes. May be empty but is never null.</summary>
    public required byte[] Body { get; init; }

    /// <summary>
    /// Content-Type header value as served, or null if none was supplied.
    /// The engine parses charset out of this downstream; we do not touch it here.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Every location visited in the redirect chain, including <see cref="RequestUrl"/>
    /// as the first element and <see cref="FinalUrl"/> as the last. Length-1 if
    /// no redirect occurred.
    /// </summary>
    public required IReadOnlyList<Uri> RedirectChain { get; init; }

    /// <summary>Case-insensitive response header snapshot. Multi-value headers are preserved.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; }
}
