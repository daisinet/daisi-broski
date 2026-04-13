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

    // Phase-4 completion: exposes the phase-3 JS engine + Skimmer
    // across the sandbox boundary. The host never runs untrusted
    // script code; the child process eats the exploit if one ever
    // escapes a polyfill.
    public const string Run = "run";
    public const string Skim = "skim";

    // Phase-4 live handle table: lets the host hold references to
    // live JS / DOM objects in the sandbox and drive interactive
    // operations (click, set attribute, read property, call method,
    // evaluate an arbitrary script expression). Handles are opaque
    // `long` ids minted by the sandbox and released explicitly by
    // the host — the child holds weak references so a script-side
    // GC still reclaims abandoned objects.
    public const string Evaluate = "evaluate";
    public const string GetProperty = "get_property";
    public const string SetProperty = "set_property";
    public const string CallMethod = "call_method";
    public const string QueryHandles = "query_handles";
    public const string ReleaseHandles = "release_handles";

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

    /// <summary>
    /// When true, the response populates <see cref="NavigateResponse.Html"/>
    /// with the decoded HTML body of the final page. Opt-in so callers
    /// that only need metadata (<c>daisi-broski fetch --select</c>)
    /// don't pay the ~50 KiB–MiB serialization cost per request.
    /// </summary>
    [JsonPropertyName("include_html")]
    public bool IncludeHtml { get; init; }
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

    /// <summary>
    /// The full decoded HTML of the final page. Only populated when
    /// <see cref="NavigateRequest.IncludeHtml"/> was set on the request.
    /// </summary>
    [JsonPropertyName("html")]
    public string? Html { get; init; }
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

// ============================================================
// Phase-4 completion: Run (fetch + run scripts + return DOM info)
// ============================================================

/// <summary>Request payload for <see cref="Methods.Run"/>. Mirrors
/// <see cref="NavigateRequest"/> plus a scripting toggle and an
/// optional selector — when the selector is present the response
/// carries matched elements only, keeping the wire payload
/// small; otherwise the full post-script HTML comes back.</summary>
public sealed class RunRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("max_redirects")]
    public int? MaxRedirects { get; init; }

    /// <summary>When true (default), the sandbox runs the page's
    /// blocking + deferred scripts before returning. Equivalent
    /// to the engine's BroskiOptions.ScriptingEnabled.</summary>
    [JsonPropertyName("scripting_enabled")]
    public bool ScriptingEnabled { get; init; } = true;

    /// <summary>Optional CSS selector — when present, the response
    /// populates <see cref="RunResponse.Matches"/> with the matched
    /// elements instead of the full post-script HTML.</summary>
    [JsonPropertyName("select")]
    public string? Select { get; init; }

    /// <summary>When true and no <c>select</c> is provided, the
    /// response populates <see cref="RunResponse.Html"/> with the
    /// full serialized post-script document. Large payload — opt-in.</summary>
    [JsonPropertyName("include_html")]
    public bool IncludeHtml { get; init; }
}

/// <summary>Response payload for <see cref="Methods.Run"/>.</summary>
public sealed class RunResponse
{
    [JsonPropertyName("final_url")]
    public required string FinalUrl { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Total <c>&lt;script&gt;</c> elements the engine saw
    /// (inline + external, including async / module / JSON types
    /// that were skipped).</summary>
    [JsonPropertyName("scripts_total")]
    public required int ScriptsTotal { get; init; }

    /// <summary>Number of scripts that ran to completion.</summary>
    [JsonPropertyName("scripts_ran")]
    public required int ScriptsRan { get; init; }

    /// <summary>Number of scripts that threw during execution.</summary>
    [JsonPropertyName("scripts_errored")]
    public required int ScriptsErrored { get; init; }

    /// <summary>Element count after scripts finished. Useful for
    /// reporting hydration collapse on SPA sites (Next.js drops
    /// 2000 → 500 elements as suspense boundaries resolve).</summary>
    [JsonPropertyName("element_count")]
    public required int ElementCount { get; init; }

    /// <summary>Post-script HTML when the request opted in via
    /// <see cref="RunRequest.IncludeHtml"/> and no selector was
    /// provided. Null otherwise.</summary>
    [JsonPropertyName("html")]
    public string? Html { get; init; }

    /// <summary>Selector matches when <see cref="RunRequest.Select"/>
    /// was provided. Empty when there was no selector.</summary>
    [JsonPropertyName("matches")]
    public required IReadOnlyList<SerializedElement> Matches { get; init; }
}

// ============================================================
// Phase-4 completion: Skim (fetch + run scripts + extract article)
// ============================================================

/// <summary>Request payload for <see cref="Methods.Skim"/>.</summary>
public sealed class SkimRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("max_redirects")]
    public int? MaxRedirects { get; init; }

    [JsonPropertyName("scripting_enabled")]
    public bool ScriptingEnabled { get; init; } = true;
}

/// <summary>Response payload for <see cref="Methods.Skim"/> —
/// a cross-process snapshot of an <c>ArticleContent</c>. Everything
/// the three formatters need to re-render (HtmlFormatter output
/// included as a string) without re-running the extractor.</summary>
public sealed class SkimResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("byline")]
    public string? Byline { get; init; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; init; }

    [JsonPropertyName("lang")]
    public string? Lang { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("site_name")]
    public string? SiteName { get; init; }

    [JsonPropertyName("hero_image")]
    public string? HeroImage { get; init; }

    [JsonPropertyName("plain_text")]
    public required string PlainText { get; init; }

    [JsonPropertyName("word_count")]
    public required int WordCount { get; init; }

    /// <summary>Pre-rendered HTML body via the Skimmer's
    /// HtmlFormatter. Lets the host reproduce the reader view
    /// without serializing a live DOM tree.</summary>
    [JsonPropertyName("content_html")]
    public string? ContentHtml { get; init; }

    [JsonPropertyName("images")]
    public required IReadOnlyList<SerializedLink> Images { get; init; }

    [JsonPropertyName("links")]
    public required IReadOnlyList<SerializedLink> Links { get; init; }

    [JsonPropertyName("nav_links")]
    public required IReadOnlyList<SerializedLink> NavLinks { get; init; }
}

/// <summary>Cross-process shape for an extracted link / image
/// pair. Same JSON shape as the Skimmer's <c>ExtractedLink</c> /
/// <c>ExtractedImage</c> record structs — we don't share the
/// Skimmer types across the IPC boundary to keep
/// <c>Daisi.Broski.Ipc</c> engine-free.</summary>
public sealed class SerializedLink
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

// ============================================================
// Phase-4 live handle table — cross-process references to live
// JS / DOM objects in the sandbox.
// ============================================================

/// <summary>Discriminated cross-process shape for any JS value.
/// <see cref="Kind"/> selects which of the optional fields
/// carries the payload; all others are null.
///
/// <list type="bullet">
/// <item><c>undefined</c> / <c>null</c> — no payload.</item>
/// <item><c>bool</c> — <see cref="Boolean"/>.</item>
/// <item><c>number</c> — <see cref="Number"/> (double; covers the
///   JS Number primitive including <c>NaN</c> / <c>±Infinity</c>).</item>
/// <item><c>string</c> — <see cref="String"/>.</item>
/// <item><c>bigint</c> — <see cref="String"/> (the decimal digit
///   string; BigInt has no finite-precision .NET primitive we can
///   transport, and round-tripping via base-10 stays lossless).</item>
/// <item><c>handle</c> — <see cref="HandleId"/> plus
///   <see cref="HandleType"/> (<c>"Element"</c>, <c>"Document"</c>,
///   <c>"Array"</c>, <c>"Function"</c>, <c>"Object"</c>).</item>
/// </list>
/// </summary>
public sealed class IpcValue
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("bool")]
    public bool? Boolean { get; init; }

    [JsonPropertyName("num")]
    public double? Number { get; init; }

    [JsonPropertyName("str")]
    public string? String { get; init; }

    [JsonPropertyName("handle")]
    public long? HandleId { get; init; }

    [JsonPropertyName("htype")]
    public string? HandleType { get; init; }

    public static IpcValue Undefined() => new() { Kind = "undefined" };
    public static IpcValue Null() => new() { Kind = "null" };
    public static IpcValue Of(bool b) => new() { Kind = "bool", Boolean = b };
    public static IpcValue Of(double n) => new() { Kind = "number", Number = n };
    public static IpcValue Of(string s) => new() { Kind = "string", String = s };
    public static IpcValue BigInt(string digits) => new() { Kind = "bigint", String = digits };
    public static IpcValue Handle(long id, string type) =>
        new() { Kind = "handle", HandleId = id, HandleType = type };
}

public sealed class EvaluateRequest
{
    [JsonPropertyName("script")]
    public required string Script { get; init; }
}

public sealed class EvaluateResponse
{
    [JsonPropertyName("value")]
    public required IpcValue Value { get; init; }
}

public sealed class GetPropertyRequest
{
    [JsonPropertyName("handle")]
    public required long Handle { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class GetPropertyResponse
{
    [JsonPropertyName("value")]
    public required IpcValue Value { get; init; }
}

public sealed class SetPropertyRequest
{
    [JsonPropertyName("handle")]
    public required long Handle { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required IpcValue Value { get; init; }
}

public sealed class SetPropertyResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }
}

public sealed class CallMethodRequest
{
    [JsonPropertyName("handle")]
    public required long Handle { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("args")]
    public required IReadOnlyList<IpcValue> Args { get; init; }
}

public sealed class CallMethodResponse
{
    [JsonPropertyName("value")]
    public required IpcValue Value { get; init; }
}

public sealed class QueryHandlesRequest
{
    [JsonPropertyName("selector")]
    public required string Selector { get; init; }
}

public sealed class QueryHandlesResponse
{
    [JsonPropertyName("handles")]
    public required IReadOnlyList<IpcValue> Handles { get; init; }
}

public sealed class ReleaseHandlesRequest
{
    [JsonPropertyName("handles")]
    public required IReadOnlyList<long> Handles { get; init; }
}

public sealed class ReleaseHandlesResponse
{
    [JsonPropertyName("released")]
    public required int Released { get; init; }
}

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
