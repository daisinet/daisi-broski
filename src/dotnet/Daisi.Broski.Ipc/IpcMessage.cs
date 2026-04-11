using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daisi.Broski.Ipc;

/// <summary>
/// Wire-level message envelope between the host process and the sandbox
/// child process. The shape is loosely JSON-RPC 2.0 without the
/// <c>"jsonrpc":"2.0"</c> field — we don't need cross-vendor compat,
/// we need a format our own codec can read quickly and our own types
/// can serialize cleanly.
///
/// One <see cref="IpcMessage"/> instance carries exactly one of three
/// roles, distinguished by <see cref="Kind"/>:
///
///   - <see cref="MessageKind.Request"/> — host → sandbox (typically).
///     Has a non-zero <see cref="Id"/> that the response must echo.
///     <see cref="Method"/> is the RPC name. <see cref="Params"/> is the
///     serialized request payload.
///
///   - <see cref="MessageKind.Response"/> — reply to a request.
///     Carries the same <see cref="Id"/>. Exactly one of
///     <see cref="Result"/> / <see cref="Error"/> is populated.
///
///   - <see cref="MessageKind.Notification"/> — fire-and-forget, usually
///     sandbox → host (ConsoleMessage, NavigationCompleted, ...).
///     <see cref="Id"/> is zero. <see cref="Params"/> carries the payload.
///
/// The payload blobs are held as <see cref="JsonElement"/> so the
/// envelope codec doesn't need to know about every DTO type. Callers
/// deserialize the payload on their own schedule via <see cref="ParamsAs{T}"/>
/// / <see cref="ResultAs{T}"/>.
/// </summary>
public sealed class IpcMessage
{
    [JsonPropertyName("kind")]
    public MessageKind Kind { get; init; }

    /// <summary>Request / response correlation id. Zero for notifications.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>Method name for requests and notifications; null for
    /// responses.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>Request / notification payload.</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    /// <summary>Response payload (on success).</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    /// <summary>Response payload (on failure).</summary>
    [JsonPropertyName("error")]
    public IpcError? Error { get; init; }

    public static IpcMessage Request(long id, string method, object? @params) =>
        new()
        {
            Kind = MessageKind.Request,
            Id = id,
            Method = method,
            Params = Serialize(@params),
        };

    public static IpcMessage Response(long id, object? result) =>
        new()
        {
            Kind = MessageKind.Response,
            Id = id,
            Result = Serialize(result),
        };

    public static IpcMessage ResponseError(long id, string code, string message) =>
        new()
        {
            Kind = MessageKind.Response,
            Id = id,
            Error = new IpcError(code, message),
        };

    public static IpcMessage Notification(string method, object? @params) =>
        new()
        {
            Kind = MessageKind.Notification,
            Id = 0,
            Method = method,
            Params = Serialize(@params),
        };

    /// <summary>Deserialize <see cref="Params"/> as <typeparamref name="T"/>,
    /// or return default if <see cref="Params"/> is null.</summary>
    public T? ParamsAs<T>()
    {
        if (Params is not { } p) return default;
        return JsonSerializer.Deserialize<T>(p.GetRawText());
    }

    /// <summary>Deserialize <see cref="Result"/> as <typeparamref name="T"/>,
    /// or return default if <see cref="Result"/> is null.</summary>
    public T? ResultAs<T>()
    {
        if (Result is not { } r) return default;
        return JsonSerializer.Deserialize<T>(r.GetRawText());
    }

    private static JsonElement? Serialize(object? value)
    {
        if (value is null) return null;
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        // Clone so the element outlives the using block.
        return doc.RootElement.Clone();
    }
}

public enum MessageKind
{
    Request = 1,
    Response = 2,
    Notification = 3,
}

public sealed record IpcError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
