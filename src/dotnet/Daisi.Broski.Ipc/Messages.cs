using System.Text.Json.Serialization;

namespace Daisi.Broski.Ipc;

/// <summary>
/// Phase-1 request/response DTOs for host ↔ sandbox communication.
/// These are the only methods the sandbox understands right now.
/// The protocol is designed to grow additively — new methods can be
/// added without changing the envelope format, and old clients
/// receive an <see cref="IpcError"/> with code <c>"method_not_found"</c>
/// for anything they don't recognize.
/// </summary>
public static class Methods
{
    public const string Navigate = "navigate";
    public const string QueryAll = "query_all";
    public const string Close = "close";

    // Notifications (sandbox → host)
    public const string NavigationStarted = "navigation_started";
    public const string NavigationCompleted = "navigation_completed";
    public const string NavigationFailed = "navigation_failed";
}

/// <summary>Request payload for <see cref="Methods.Navigate"/>.</summary>
public sealed class NavigateRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Optional override for the default User-Agent.</summary>
    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; init; }

    /// <summary>Maximum redirects to follow. Defaults to 20 on the sandbox side.</summary>
    [JsonPropertyName("max_redirects")]
    public int? MaxRedirects { get; init; }
}

/// <summary>Response payload for <see cref="Methods.Navigate"/> on success.</summary>
public sealed class NavigateResponse
{
    [JsonPropertyName("final_url")]
    public required string FinalUrl { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("encoding")]
    public required string Encoding { get; init; }

    [JsonPropertyName("redirect_chain")]
    public required IReadOnlyList<string> RedirectChain { get; init; }

    [JsonPropertyName("byte_count")]
    public required int ByteCount { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

/// <summary>Request payload for <see cref="Methods.QueryAll"/>.</summary>
public sealed class QueryAllRequest
{
    [JsonPropertyName("selector")]
    public required string Selector { get; init; }
}

/// <summary>Response payload for <see cref="Methods.QueryAll"/>.</summary>
public sealed class QueryAllResponse
{
    [JsonPropertyName("matches")]
    public required IReadOnlyList<SerializedElement> Matches { get; init; }
}

/// <summary>
/// Cross-process snapshot of a DOM element. Only the parts a caller
/// might reasonably want: the tag, the attributes, and the text content.
/// If the caller needs more — child structure, raw HTML — we add a
/// second serialization level later. Keeping this small now keeps the
/// IPC payload bounded and the codec cheap.
/// </summary>
public sealed class SerializedElement
{
    [JsonPropertyName("tag")]
    public required string TagName { get; init; }

    [JsonPropertyName("attrs")]
    public required IReadOnlyDictionary<string, string> Attributes { get; init; }

    [JsonPropertyName("text")]
    public required string TextContent { get; init; }
}

public sealed class CloseRequest { }
public sealed class CloseResponse { }

/// <summary>Payload for <see cref="Methods.NavigationCompleted"/>.</summary>
public sealed class NavigationCompletedNotification
{
    [JsonPropertyName("final_url")]
    public required string FinalUrl { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }
}

/// <summary>Payload for <see cref="Methods.NavigationFailed"/>.</summary>
public sealed class NavigationFailedNotification
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }
}
