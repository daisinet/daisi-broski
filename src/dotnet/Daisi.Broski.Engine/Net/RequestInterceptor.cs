namespace Daisi.Broski.Engine.Net;

/// <summary>
/// Host-supplied hook that can short-circuit an outbound HTTP
/// request before it reaches the wire. Used for:
///
/// <list type="bullet">
/// <item><b>Test scaffolding</b> — return a canned response for
///   a known URL so a unit test doesn't need a real HTTP
///   listener.</item>
/// <item><b>Ad / tracker blocking</b> — return a 204 (or any
///   chosen status) for hosts on a blocklist so the page
///   loads without paying the request RTT.</item>
/// <item><b>Offline replay</b> — serve previously-captured
///   responses from a snapshot directory so a script can run
///   deterministically.</item>
/// </list>
///
/// <para>
/// The interceptor returns <c>null</c> to let the request
/// proceed normally. Returning an
/// <see cref="InterceptedResponse"/> short-circuits the network
/// call entirely; <see cref="HttpFetcher.FetchAsync"/> wraps it
/// in a <see cref="FetchResult"/> that reflects the synthetic
/// response. Throwing surfaces as a regular exception to the
/// caller — useful for "blocked URLs raise here" semantics.
/// </para>
///
/// <para>
/// Wired into the network stack at the lowest layer so every
/// path that fetches HTTP — page navigation, script tags,
/// <c>fetch()</c>, <c>XMLHttpRequest</c> — sees the same
/// interception decisions.
/// </para>
/// </summary>
/// <param name="request">The about-to-be-issued request, with
/// the absolute URL and the HTTP method (always <c>GET</c> in
/// v1 — the underlying fetcher doesn't yet support body
/// pass-through; future slices will widen this).</param>
public delegate InterceptedResponse? RequestInterceptor(InterceptedRequest request);

/// <summary>Cross-thread snapshot of an outgoing request.
/// Future slices may add more fields (headers, body) once
/// the network stack threads them through.</summary>
public sealed class InterceptedRequest
{
    public required Uri Url { get; init; }
    public required string Method { get; init; }
}

/// <summary>Synthetic response the interceptor can return
/// in lieu of hitting the network. All fields are optional;
/// missing ones get sensible defaults
/// (<c>Status = 200</c>, empty body, <c>text/plain</c>).</summary>
public sealed class InterceptedResponse
{
    public int Status { get; init; } = 200;
    public string? StatusReason { get; init; }
    public string? ContentType { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Headers { get; init; }
}
