using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Daisi.Broski;

/// <summary>
/// Spawns a new <c>Daisi.Broski.Sandbox.exe</c> child process under a
/// fresh <see cref="JobObject"/>, wires up two anonymous pipes for
/// bidirectional IPC, and returns a managed <see cref="SandboxProcess"/>
/// handle for the caller to drive.
///
/// Boot sequence:
///
///   1. Create anonymous pipe pair for host → child (server Out, client In).
///   2. Create anonymous pipe pair for child → host (server In, client Out).
///   3. Create a Job Object with the requested limits.
///   4. Start the child .exe with the two client-handle strings on the
///      command line so they're inherited through STARTUPINFO handle
///      inheritance.
///   5. Dispose the client-side handle references in the parent — the
///      child already inherited them, and the parent holding them open
///      would prevent the child from ever seeing EOF on close.
///   6. Assign the child to the Job Object.
///
/// Race window: steps 4 and 6 are not atomic. There is a window
/// between process creation and job assignment during which the child
/// runs without the job's memory cap. Architecture.md §5.8 documents a
/// stricter variant using native <c>CreateProcess(CREATE_SUSPENDED)</c>
/// that avoids this window entirely. For phase 1 the window is ~a few
/// milliseconds, during which the child only parses argv and opens its
/// inherited pipe handles — it does no network or parse work. If the
/// threat model ever demands strictness, swap this launcher for one
/// that goes through native CreateProcess.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SandboxLauncher
{
    /// <summary>
    /// Spawn a sandbox child and return a handle to it. The caller owns
    /// the returned <see cref="SandboxProcess"/> and must dispose it.
    /// </summary>
    /// <param name="executablePath">Absolute path to <c>Daisi.Broski.Sandbox.exe</c>.
    /// Typically resolved via <see cref="ResolveDefaultSandboxPath"/>.</param>
    /// <param name="options">Job Object limits to apply to the child.
    /// Defaults to the standard phase-1 profile (256 MiB, kill-on-close,
    /// die-on-unhandled-exception, UI restrictions).</param>
    public static SandboxProcess Launch(string executablePath, JobObjectOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"Sandbox executable not found at '{executablePath}'.", executablePath);
        }

        var job = JobObject.Create(options);

        AnonymousPipeServerStream? toChild = null;
        AnonymousPipeServerStream? fromChild = null;
        Process? process = null;

        try
        {
            // Pipe 1: parent writes, child reads.
            toChild = new AnonymousPipeServerStream(
                PipeDirection.Out, HandleInheritability.Inheritable);
            var toChildClientHandle = toChild.GetClientHandleAsString();

            // Pipe 2: parent reads, child writes.
            fromChild = new AnonymousPipeServerStream(
                PipeDirection.In, HandleInheritability.Inheritable);
            var fromChildClientHandle = fromChild.GetClientHandleAsString();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--in-handle");
            startInfo.ArgumentList.Add(toChildClientHandle);
            startInfo.ArgumentList.Add("--out-handle");
            startInfo.ArgumentList.Add(fromChildClientHandle);

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Process.Start returned null for the sandbox executable.");

            // The child inherited the client ends; dispose our local
            // references so the child is the sole owner of its side.
            toChild.DisposeLocalCopyOfClientHandle();
            fromChild.DisposeLocalCopyOfClientHandle();

            // Assign to the job AFTER process creation. See the class
            // doc comment for the race-window discussion.
            job.AssignProcess(process.Handle);

            return new SandboxProcess(process, job, toChild, fromChild);
        }
        catch
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            toChild?.Dispose();
            fromChild?.Dispose();
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Locate <c>Daisi.Broski.Sandbox.exe</c> next to the host assembly.
    /// Works for development (<c>bin/Debug/net10.0/</c>) and for
    /// single-folder deployments. Throws <see cref="FileNotFoundException"/>
    /// if the exe isn't there — callers who want a custom path should
    /// pass it to <see cref="Launch"/> directly.
    ///
    /// A <c>Daisi.Broski.Sandbox.exe</c> that is <i>not</i> accompanied
    /// by its managed <c>.dll</c> is a half-copy left behind by MSBuild
    /// (typically from a test project with
    /// <c>ReferenceOutputAssembly="false"</c>). Running it produces a
    /// cryptic "application to execute does not exist" error from the
    /// .NET apphost. We skip such half-copies and keep searching.
    /// </summary>
    public static string ResolveDefaultSandboxPath()
    {
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "Daisi.Broski.Sandbox.exe");
        if (IsCompleteSandboxExe(candidate)) return candidate;

        // Fallback: walk up to the solution root during development.
        var probe = dir;
        while (!string.IsNullOrEmpty(probe))
        {
            var guess = Path.Combine(probe, "Daisi.Broski.Sandbox", "bin", "Debug", "net10.0",
                "Daisi.Broski.Sandbox.exe");
            if (IsCompleteSandboxExe(guess)) return guess;
            probe = Path.GetDirectoryName(probe);
        }

        throw new FileNotFoundException(
            "Could not locate a complete Daisi.Broski.Sandbox.exe (the apphost " +
            "and its managed .dll must both be present). Build the " +
            "Daisi.Broski.Sandbox project or pass an explicit path to " +
            "SandboxLauncher.Launch.");
    }

    private static bool IsCompleteSandboxExe(string exePath)
    {
        if (!File.Exists(exePath)) return false;
        var dllPath = Path.ChangeExtension(exePath, ".dll");
        return File.Exists(dllPath);
    }
}
