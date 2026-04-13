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
