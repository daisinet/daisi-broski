namespace Daisi.Broski.Engine.Net;

/// <summary>
/// Thrown when a fetch cannot complete for a reason the engine considers
/// fatal to *this specific request*: redirect cap exceeded, response body
/// too large, malformed Location header. Transport-level failures
/// (DNS, TLS, connection refused) surface as <see cref="HttpRequestException"/>
/// from the underlying HttpClient and are not wrapped.
/// </summary>
public sealed class HttpFetcherException : Exception
{
    public HttpFetcherException(string message) : base(message) { }
    public HttpFetcherException(string message, Exception inner) : base(message, inner) { }
}
