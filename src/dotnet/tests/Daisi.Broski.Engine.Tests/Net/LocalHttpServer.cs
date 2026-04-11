using System.Net;
using System.Text;

namespace Daisi.Broski.Engine.Tests.Net;

/// <summary>
/// Tiny scriptable HTTP server backed by <see cref="HttpListener"/> (BCL only).
///
/// Tests register a handler delegate and get back a base URL. The server
/// runs on a random free loopback port and shuts down cleanly on Dispose.
///
/// This exists because the network stack's tests cannot reach the public
/// internet in CI. It's small enough to live in-repo without becoming its
/// own sub-project.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Func<HttpListenerContext, Task> _handler;

    public Uri BaseUrl { get; }

    public LocalHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;

        // Port 0 asks the OS for any free port; we read the actual port back
        // from the Prefix after Start().
        int port = GetFreeTcpPort();
        BaseUrl = new Uri($"http://127.0.0.1:{port}/");

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
        _listener.Start();

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped — expected on shutdown.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Don't await — let requests run concurrently. We still observe
            // exceptions via the unhandled task observer; for tests, any
            // failure here surfaces through the assertion on the client side.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _handler(ctx).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort 500 so the client sees something, not a hang.
                    try
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                    }
                    catch { /* already closed */ }
                }
            });
        }
    }

    public static void WriteText(HttpListenerContext ctx, string body, string contentType = "text/html; charset=utf-8", int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.LongLength;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    public static void Redirect(HttpListenerContext ctx, Uri target, int status = 302)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers["Location"] = target.AbsoluteUri;
        ctx.Response.Close();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
