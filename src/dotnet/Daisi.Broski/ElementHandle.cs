using System.Runtime.Versioning;
using Daisi.Broski.Ipc;

namespace Daisi.Broski;

/// <summary>
/// A live handle to a DOM element inside the sandbox. Inherits
/// the generic property / method helpers from <see cref="JsHandle"/>
/// and adds typed shortcuts for the DOM operations that every
/// host driver wants: click, get / set / remove attributes, read
/// tag name / text content / innerHTML, dispatch events.
///
/// <para>
/// Construction goes through
/// <see cref="BrowserSession.QuerySelectorHandleAsync"/> /
/// <see cref="BrowserSession.QuerySelectorAllHandlesAsync"/>, or
/// via <see cref="BrowserSession.EvaluateHandleAsync"/> when the
/// evaluated expression returns a DOM node. The sandbox prefers
/// the JS-bridge wrapper when an engine is attached, so the
/// shortcuts below route through the normal JS call path —
/// <c>el.click()</c>, <c>el.setAttribute(name, value)</c>, etc. —
/// and run every DOM mutation observer / event listener the page
/// installed. Mutations made this way are visible to subsequent
/// queries and evaluate calls on the same session.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElementHandle : JsHandle
{
    internal ElementHandle(BrowserSession session, long id, string type)
        : base(session, id, type) { }

    /// <summary>Remote <c>tagName</c>. Uppercase per DOM spec
    /// (<c>"DIV"</c>, <c>"A"</c>, <c>"INPUT"</c>).</summary>
    public Task<string?> GetTagNameAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("tagName", ct);

    /// <summary>Concatenated text of all descendant text nodes.
    /// Round-trips as a string; use <see cref="SetTextContentAsync"/>
    /// to replace the subtree with a single text node.</summary>
    public Task<string?> GetTextContentAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("textContent", ct);

    public Task SetTextContentAsync(string value, CancellationToken ct = default) =>
        SetPropertyAsync("textContent", value, ct);

    /// <summary>Remote <c>innerHTML</c>. Supported end-to-end: read
    /// returns the serialized descendants, write re-parses the
    /// fragment against the element and swaps in the new
    /// subtree.</summary>
    public Task<string?> GetInnerHtmlAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("innerHTML", ct);

    public Task SetInnerHtmlAsync(string html, CancellationToken ct = default) =>
        SetPropertyAsync("innerHTML", html, ct);

    /// <summary>Serialized outer HTML (element + descendants).
    /// Read-only on this shortcut; writing outerHTML is rarely
    /// useful and can be issued via
    /// <see cref="JsHandle.SetPropertyAsync(string, string, CancellationToken)"/>
    /// directly.</summary>
    public Task<string?> GetOuterHtmlAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("outerHTML", ct);

    public Task<string?> GetIdAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("id", ct);

    public Task<string?> GetClassNameAsync(CancellationToken ct = default) =>
        GetPropertyAsync<string>("className", ct);

    /// <summary>Equivalent of <c>el.getAttribute(name)</c>. Returns
    /// <c>null</c> for absent attributes — DOM semantics, not an
    /// error.</summary>
    public Task<string?> GetAttributeAsync(string name, CancellationToken ct = default) =>
        CallMethodAsync<string>("getAttribute", new[] { IpcValue.Of(name) }, ct);

    public Task SetAttributeAsync(string name, string value, CancellationToken ct = default) =>
        CallMethodRawAsync(
            "setAttribute",
            new[] { IpcValue.Of(name), IpcValue.Of(value) },
            ct);

    public Task RemoveAttributeAsync(string name, CancellationToken ct = default) =>
        CallMethodRawAsync(
            "removeAttribute",
            new[] { IpcValue.Of(name) },
            ct);

    public async Task<bool> HasAttributeAsync(string name, CancellationToken ct = default)
    {
        var v = await CallMethodRawAsync(
            "hasAttribute",
            new[] { IpcValue.Of(name) },
            ct).ConfigureAwait(false);
        return v.Boolean ?? false;
    }

    /// <summary>Invoke <c>el.click()</c>. The engine fires the
    /// standard bubbling click event, so any handler attached via
    /// <c>addEventListener("click", ...)</c> or <c>onclick</c>
    /// will run; the host sees the post-event document state on
    /// the next query / evaluate.</summary>
    public Task ClickAsync(CancellationToken ct = default) =>
        CallMethodRawAsync("click", null, ct);

    /// <summary>Find the first descendant matching <paramref name="selector"/>.
    /// Returns <c>null</c> when the element has no matching
    /// descendant. Uses <c>el.querySelector</c> under the hood.</summary>
    public async Task<ElementHandle?> QuerySelectorAsync(
        string selector, CancellationToken ct = default)
    {
        var v = await CallMethodRawAsync(
            "querySelector",
            new[] { IpcValue.Of(selector) },
            ct).ConfigureAwait(false);
        if (v.Kind != "handle" || v.HandleId is not long id) return null;
        return new ElementHandle(_session, id, v.HandleType ?? "Element");
    }

    /// <summary>Dispatch a pre-constructed event handle on this
    /// element. The event must have been minted via
    /// <see cref="BrowserSession.EvaluateHandleAsync"/> (e.g.
    /// <c>new Event('input', {bubbles:true})</c>) and is released
    /// normally.</summary>
    public async Task<bool> DispatchEventAsync(
        JsHandle @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var v = await CallMethodRawAsync(
            "dispatchEvent",
            new[] { IpcValue.Handle(@event.Id, @event.Type) },
            ct).ConfigureAwait(false);
        return v.Boolean ?? true;
    }
}
