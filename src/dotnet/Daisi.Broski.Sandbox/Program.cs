using System.IO.Pipes;

namespace Daisi.Broski.Sandbox;

/// <summary>
/// Entry point for the sandbox child process
/// (<c>Daisi.Broski.Sandbox.exe</c>). The host spawns this process with
/// two inherited anonymous-pipe client handles on the command line,
/// then sends IPC requests over them.
///
/// Command-line contract:
///
/// <code>
///   Daisi.Broski.Sandbox.exe --in-handle &lt;N&gt; --out-handle &lt;N&gt;
/// </code>
///
/// Where <c>&lt;N&gt;</c> is a string produced by
/// <see cref="AnonymousPipeServerStream.GetClientHandleAsString"/>. Both
/// handles are <b>required</b>; the process exits with code 64
/// (EX_USAGE) if they are missing or malformed. This is a process-level
/// contract, not a user-facing CLI — the host is always the caller.
///
/// After opening the pipes, the child hands control to
/// <see cref="SandboxRuntime.RunAsync"/>, which reads requests and
/// dispatches them against a single long-lived <c>PageLoader</c>.
/// The process exits when the host closes the inbound pipe, or when
/// the host itself dies and the Win32 Job Object's kill-on-close
/// flag takes this process out.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? inHandle = null;
        string? outHandle = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in-handle":
                    if (i + 1 >= args.Length) return UsageError("missing --in-handle value");
                    inHandle = args[++i];
                    break;
                case "--out-handle":
                    if (i + 1 >= args.Length) return UsageError("missing --out-handle value");
                    outHandle = args[++i];
                    break;
                default:
                    return UsageError($"unknown argument '{args[i]}'");
            }
        }

        if (inHandle is null || outHandle is null)
        {
            return UsageError("both --in-handle and --out-handle are required");
        }

        // Open the inherited anonymous pipe handles. The directions are
        // from the child's perspective: we read from "in" and write to "out".
        using var input = new AnonymousPipeClientStream(PipeDirection.In, inHandle);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);

        try
        {
            await SandboxRuntime.RunAsync(input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            // Last-ditch error handler. The host will see the process exit
            // code and any partial IPC output it had time to send.
            Console.Error.WriteLine($"daisi-broski sandbox crashed: {ex}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"daisi-broski sandbox: {message}");
        Console.Error.WriteLine("This is an internal process, not a user-facing CLI.");
        return 64; // EX_USAGE
    }
}
