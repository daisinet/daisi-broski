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
    private SandboxProcess _sandbox;
    private readonly string _sandboxExePath;
    private readonly JobObjectOptions? _jobOptions;
    private readonly SemaphoreSlim _respawnLock = new(1, 1);

    private BrowserSession(SandboxProcess sandbox, string sandboxExePath, JobObjectOptions? jobOptions)
    {
        _sandbox = sandbox;
        _sandboxExePath = sandboxExePath;
        _jobOptions = jobOptions;
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
        return new BrowserSession(sandbox, sandboxExePath, jobOptions);
    }

    /// <summary>
    /// Send a request to the sandbox child, respawning a fresh child
    /// if the current one is dead on arrival (exited between
    /// operations). The respawn is single-retry — if the fresh child
    /// also fails, the SandboxException propagates with the full
    /// crash context. Per-request mid-flight crashes are NOT retried
    /// automatically because the caller's operation may have had
    /// side effects on the host side that we can't roll back.
    /// </summary>
    private async Task<TResponse> SendWithRespawnAsync<TRequest, TResponse>(
        string method, TRequest request, CancellationToken ct)
        where TRequest : class
        where TResponse : class
    {
        // Fast path: child is alive, send directly.
        if (_sandbox.IsAlive)
        {
            try
            {
                return await _sandbox
                    .SendRequestAsync<TRequest, TResponse>(method, request, ct)
                    .ConfigureAwait(false);
            }
            catch (SandboxException) when (!_sandbox.IsAlive)
            {
                // The child died mid-request. Respawn and retry
                // exactly once.
                await RespawnAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await RespawnAsync(ct).ConfigureAwait(false);
        }

        return await _sandbox
            .SendRequestAsync<TRequest, TResponse>(method, request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Launch a fresh sandbox child and swap it in for the
    /// current (dead) one. Serialized against concurrent respawns so
    /// we don't leak child processes.</summary>
    private async Task RespawnAsync(CancellationToken ct)
    {
        await _respawnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sandbox.IsAlive) return; // raced with another caller
            var old = _sandbox;
            var fresh = SandboxLauncher.Launch(_sandboxExePath, _jobOptions);
            _sandbox = fresh;
            // Dispose the old wrapper outside the lock so a slow
            // WaitForExit in there can't stall new requests.
            _ = DisposeOldAsync(old);
        }
        finally
        {
            _respawnLock.Release();
        }
    }

    private static async Task DisposeOldAsync(SandboxProcess old)
    {
        try { await old.DisposeAsync().ConfigureAwait(false); }
        catch { /* the child is already dead; cleanup is best-effort */ }
    }

    /// <summary>Navigate to a URL and parse the resulting document
    /// inside the sandbox. The returned metadata comes from the
    /// child; the document itself lives in the child process and can
    /// be queried via <see cref="QuerySelectorAllAsync"/>.</summary>
    /// <param name="includeHtml">
    /// When true, the returned <see cref="NavigateResponse.Html"/> is
    /// populated with the full decoded HTML of the final page. Opt-in
    /// so the common metadata-only case (title, status, counts) doesn't
    /// pay the serialization cost for every fetch.
    /// </param>
    public async Task<NavigateResponse> NavigateAsync(
        Uri url,
        string? userAgent = null,
        int? maxRedirects = null,
        bool includeHtml = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var request = new NavigateRequest
        {
            Url = url.AbsoluteUri,
            UserAgent = userAgent,
            MaxRedirects = maxRedirects,
            IncludeHtml = includeHtml,
        };
        return await SendWithRespawnAsync<NavigateRequest, NavigateResponse>(
            Methods.Navigate, request, ct).ConfigureAwait(false);
    }

    /// <summary>Run a CSS selector against the current document inside
    /// the sandbox and return serialized snapshots of every match.</summary>
    public async Task<IReadOnlyList<SerializedElement>> QuerySelectorAllAsync(
        string selector, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        var request = new QueryAllRequest { Selector = selector };
        var response = await SendWithRespawnAsync<QueryAllRequest, QueryAllResponse>(
            Methods.QueryAll, request, ct).ConfigureAwait(false);
        return response.Matches;
    }

    /// <summary>Fetch a URL, run its scripts inside the sandbox, and
    /// return post-script metadata + an optional selector match list
    /// or full HTML dump. Phase-4 completion: the full JS engine runs
    /// in the child process, so the host never touches untrusted
    /// script content. Roughly equivalent to the CLI's
    /// <c>daisi-broski run</c> command.</summary>
    public async Task<RunResponse> RunAsync(
        Uri url,
        bool scriptingEnabled = true,
        string? select = null,
        bool includeHtml = false,
        string? userAgent = null,
        int? maxRedirects = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var request = new RunRequest
        {
            Url = url.AbsoluteUri,
            UserAgent = userAgent,
            MaxRedirects = maxRedirects,
            ScriptingEnabled = scriptingEnabled,
            Select = select,
            IncludeHtml = includeHtml,
        };
        return await SendWithRespawnAsync<RunRequest, RunResponse>(
            Methods.Run, request, ct).ConfigureAwait(false);
    }

    /// <summary>Fetch a URL, run its scripts (optional), extract the
    /// main article content inside the sandbox, and return a
    /// cross-process snapshot. Roughly equivalent to the CLI's
    /// <c>daisi-broski-skim</c> command. The returned
    /// <see cref="SkimResponse.ContentHtml"/> is ready-to-display
    /// HTML produced by the Skimmer's HtmlFormatter.</summary>
    public async Task<SkimResponse> SkimAsync(
        Uri url,
        bool scriptingEnabled = true,
        string? userAgent = null,
        int? maxRedirects = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var request = new SkimRequest
        {
            Url = url.AbsoluteUri,
            UserAgent = userAgent,
            MaxRedirects = maxRedirects,
            ScriptingEnabled = scriptingEnabled,
        };
        return await SendWithRespawnAsync<SkimRequest, SkimResponse>(
            Methods.Skim, request, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _sandbox.DisposeAsync().ConfigureAwait(false);
        _respawnLock.Dispose();
    }
}
