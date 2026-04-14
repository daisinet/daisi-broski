using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Daisi.Broski.Ipc;

namespace Daisi.Broski;

/// <summary>
/// Host-side handle to a running <c>Daisi.Broski.Sandbox.exe</c> child
/// process. Owns the process handle, the two anonymous pipes, and the
/// <see cref="JobObject"/> that enforces the kernel-level limits.
///
/// Public surface:
/// - <see cref="SendRequestAsync"/> — serialized request/response
///   round-trip. Uses a monotonically increasing request id to
///   correlate replies. Notifications from the child are dropped for
///   now (phase 1 doesn't need them; phase 3 will add a subscription
///   API).
/// - <see cref="DisposeAsync"/> — closes pipes, waits briefly for the
///   child to exit on its own, and force-kills via the Job Object if
///   it doesn't.
///
/// The class is <b>not</b> thread-safe: the request loop is a single
/// in-flight operation at a time. Pipeline support is deferred —
/// phase 3 will add it when the JS engine wants to issue concurrent
/// fetch calls.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SandboxProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly JobObject _job;
    private readonly AnonymousPipeServerStream _toChild;
    private readonly AnonymousPipeServerStream _fromChild;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly System.Text.StringBuilder _childStderr = new();

    private long _nextId = 1;
    private bool _disposed;

    internal SandboxProcess(
        Process process,
        JobObject job,
        AnonymousPipeServerStream toChild,
        AnonymousPipeServerStream fromChild)
    {
        _process = process;
        _job = job;
        _toChild = toChild;
        _fromChild = fromChild;

        // Drain the child's stderr into a buffer so any crash output is
        // captured and can be surfaced in SandboxException messages.
        // Without this, the child's stderr fills the pipe and the child
        // blocks on its next write — and we get a confusing "pipe closed"
        // error with no explanation.
        //
        // Only attached when the Process was started with stderr
        // redirection. The atomic-launch path (native CreateProcess)
        // inherits the parent's stderr handle instead; we lose the
        // capture there but gain the closed race window in exchange.
        try
        {
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_childStderr) _childStderr.AppendLine(e.Data);
                }
            };
            _process.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
            // Process wasn't started with RedirectStandardError —
            // that's expected for the atomic-launch path.
        }
    }

    private string CapturedStderr()
    {
        lock (_childStderr) return _childStderr.ToString();
    }

    /// <summary>Is the child process still alive?</summary>
    public bool IsAlive => !_process.HasExited;

    /// <summary>
    /// Send a typed request to the child and wait for the matching
    /// response. Uses <see cref="IpcMessage"/> under the hood with an
    /// auto-incrementing id.
    /// </summary>
    public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        string method, TRequest request, CancellationToken ct = default)
        where TRequest : class
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long id = Interlocked.Increment(ref _nextId);
            var envelope = IpcMessage.Request(id, method, request);
            try
            {
                await IpcCodec.WriteAsync(_toChild, envelope, ct).ConfigureAwait(false);
            }
            catch (IOException ioex)
            {
                // The child closed its read end or died. Give stderr a
                // moment to flush so the exception message can include
                // the child's own diagnostics.
                try { _process.WaitForExit(500); } catch { }
                string exitInfo = _process.HasExited
                    ? $" (child exit code {_process.ExitCode})"
                    : "";
                var stderr = CapturedStderr();
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? ""
                    : $"\nSandbox stderr:\n{stderr}";
                throw new SandboxException(
                    $"Sandbox write failed: {ioex.Message}{exitInfo}{detail}", ioex);
            }

            // Drain incoming messages until we see the matching response.
            // Notifications from the child are currently ignored; phase 3
            // will route them to a subscriber API.
            while (true)
            {
                var reply = await IpcCodec.ReadAsync(_fromChild, ct).ConfigureAwait(false);
                if (reply is null)
                {
                    var stderr = CapturedStderr();
                    var detail = string.IsNullOrWhiteSpace(stderr)
                        ? ""
                        : $"\nSandbox stderr:\n{stderr}";
                    throw new SandboxException(
                        $"Sandbox child closed its outbound pipe before responding.{detail}");
                }

                if (reply.Kind != MessageKind.Response) continue;
                if (reply.Id != id) continue;

                if (reply.Error is { } err)
                {
                    throw new SandboxException($"[{err.Code}] {err.Message}");
                }

                var payload = reply.ResultAs<TResponse>();
                if (payload is null)
                {
                    throw new SandboxException(
                        $"Sandbox returned a response for {method} but the payload could not be deserialized.");
                }
                return payload;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Dispose the sandbox cleanly: send a <c>close</c> request, wait
    /// briefly for the child to exit on its own, then force-kill via
    /// the Job Object. Always safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Best-effort clean shutdown.
        try
        {
            var closeRequest = IpcMessage.Request(0, Methods.Close, new CloseRequest());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await IpcCodec.WriteAsync(_toChild, closeRequest, cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        try { _toChild.Dispose(); } catch { }
        try { _fromChild.Dispose(); } catch { }

        if (!_process.HasExited)
        {
            try { _process.WaitForExit(2_000); } catch { }
            if (!_process.HasExited)
            {
                // Job object handle closing will kill the child via
                // KILL_ON_JOB_CLOSE, which is the real enforcement point.
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }

        try { _process.Dispose(); } catch { }
        _job.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>Thrown by <see cref="SandboxProcess"/> when the child
/// reports an error or misbehaves at the IPC layer.</summary>
public sealed class SandboxException : Exception
{
    public SandboxException(string message) : base(message) { }
    public SandboxException(string message, Exception inner) : base(message, inner) { }
}
