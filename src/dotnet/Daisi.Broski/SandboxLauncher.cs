using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Daisi.Broski.Win32;

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
/// Atomic launch: <see cref="Launch"/> uses native
/// <c>CreateProcessW(CREATE_SUSPENDED)</c> so the child is created
/// with its main thread frozen, assigned to the Job Object while
/// still suspended, and only then resumed. The window where the
/// child could execute outside the job's memory cap is zero —
/// the first user-mode instruction after resume already has the
/// cap in effect. Phase-4 completion — the previous
/// <c>Process.Start</c> + <c>AssignProcessToJobObject</c>
/// sequence left a ~few-millisecond race window.
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
        ProcessInformation pi = default;
        bool processCreated = false;

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

            // Build the command line for native CreateProcess. The
            // first token is the program name (quoted if it contains
            // spaces — paths into Program Files typically do).
            string cmdLine =
                QuoteIfNeeded(executablePath) +
                " --in-handle " + toChildClientHandle +
                " --out-handle " + fromChildClientHandle;

            var si = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>() };

            // CREATE_SUSPENDED: the child's main thread starts
            // frozen. We assign to the Job Object before resuming
            // so there's never a moment when the child runs without
            // the memory cap + UI restrictions. CREATE_NO_WINDOW
            // suppresses the console window flash on a Windows
            // desktop host.
            bool ok = NativeMethods.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: cmdLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: true,
                dwCreationFlags: ProcessCreationFlags.CreateSuspended
                               | ProcessCreationFlags.CreateNoWindow,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: null,
                lpStartupInfo: si,
                lpProcessInformation: out pi);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"CreateProcess failed (win32 error {err}) for '{executablePath}'.");
            }
            processCreated = true;

            // The child inherited the client ends; dispose our local
            // references so the child is the sole owner of its side.
            toChild.DisposeLocalCopyOfClientHandle();
            fromChild.DisposeLocalCopyOfClientHandle();

            // Assign to the job BEFORE the first user-mode instruction
            // runs. This is the point of the atomic launch path.
            job.AssignProcess(pi.hProcess);

            // Wrap the raw process handle in a managed Process so the
            // rest of SandboxProcess can use the existing machinery.
            // Process.GetProcessById re-opens via pid which is safe —
            // we still need to release pi.hProcess / pi.hThread
            // because GetProcessById creates its own handle.
            var process = Process.GetProcessById((int)pi.dwProcessId);

            // Resume the main thread now that all restrictions are
            // in place.
            if (NativeMethods.ResumeThread(pi.hThread) == unchecked((uint)-1))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"ResumeThread failed (win32 error {err}).");
            }

            // Close the raw handles CreateProcess returned — the
            // managed Process owns its own duplicate.
            NativeMethods.CloseHandle(pi.hThread);
            NativeMethods.CloseHandle(pi.hProcess);
            pi = default;

            return new SandboxProcess(process, job, toChild, fromChild);
        }
        catch
        {
            if (processCreated && pi.hProcess != IntPtr.Zero)
            {
                try { NativeMethods.TerminateProcess(pi.hProcess, 1); } catch { }
                if (pi.hThread != IntPtr.Zero) NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }
            toChild?.Dispose();
            fromChild?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static string QuoteIfNeeded(string path)
    {
        if (path.Length == 0) return "\"\"";
        if (path.IndexOfAny([' ', '\t', '"']) < 0) return path;
        // Simple quoting — the executable path never contains literal
        // quote characters in practice, so we don't need the full
        // MSVC escape dance here.
        return "\"" + path + "\"";
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
