using System.Runtime.Versioning;
using Daisi.Broski.Ipc;

namespace Daisi.Broski;

/// <summary>
/// The public phase-1 API. Opens a sandboxed browser session backed
/// by a fresh <see cref="SandboxProcess"/>, and exposes typed
/// navigate / query methods. Dispose (async) tears the sandbox down.
///
/// Typical use:
///
/// <code>
///   await using var session = BrowserSession.Create();
///   var nav = await session.NavigateAsync(new Uri("https://example.com"));
///   var links = await session.QuerySelectorAllAsync("a[href]");
/// </code>
///
/// This type is a thin typed wrapper around <see cref="SandboxProcess"/>.
/// It exists so consumers don't have to know about the underlying IPC
/// method names or the DTO types on the wire. In phase 3, script-facing
/// methods (<c>EvaluateAsync</c>, <c>DispatchEventAsync</c>, ...)
/// will be added here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly SandboxProcess _sandbox;

    private BrowserSession(SandboxProcess sandbox)
    {
        _sandbox = sandbox;
    }

    /// <summary>Create a session backed by a freshly-spawned sandbox
    /// child. Uses the default sandbox path resolution and the
    /// default <see cref="JobObjectOptions"/>.</summary>
    public static BrowserSession Create(JobObjectOptions? jobOptions = null)
    {
        var exe = SandboxLauncher.ResolveDefaultSandboxPath();
        return Create(exe, jobOptions);
    }

    /// <summary>Create a session backed by a sandbox child at an
    /// explicit executable path.</summary>
    public static BrowserSession Create(string sandboxExePath, JobObjectOptions? jobOptions = null)
    {
        var sandbox = SandboxLauncher.Launch(sandboxExePath, jobOptions);
        return new BrowserSession(sandbox);
    }

    /// <summary>Navigate to a URL and parse the resulting document
    /// inside the sandbox. The returned metadata comes from the
    /// child; the document itself lives in the child process and can
    /// be queried via <see cref="QuerySelectorAllAsync"/>.</summary>
    public async Task<NavigateResponse> NavigateAsync(
        Uri url, string? userAgent = null, int? maxRedirects = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var request = new NavigateRequest
        {
            Url = url.AbsoluteUri,
            UserAgent = userAgent,
            MaxRedirects = maxRedirects,
        };
        return await _sandbox
            .SendRequestAsync<NavigateRequest, NavigateResponse>(Methods.Navigate, request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Run a CSS selector against the current document inside
    /// the sandbox and return serialized snapshots of every match.</summary>
    public async Task<IReadOnlyList<SerializedElement>> QuerySelectorAllAsync(
        string selector, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        var request = new QueryAllRequest { Selector = selector };
        var response = await _sandbox
            .SendRequestAsync<QueryAllRequest, QueryAllResponse>(Methods.QueryAll, request, ct)
            .ConfigureAwait(false);
        return response.Matches;
    }

    public ValueTask DisposeAsync() => _sandbox.DisposeAsync();
}
