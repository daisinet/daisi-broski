namespace Daisi.Broski.Sandbox;

/// <summary>
/// Configuration for a new <see cref="JobObject"/>. Every option
/// corresponds to a Win32 Job Object limit flag or UI restriction.
///
/// Defaults are chosen to match the phase-1 architecture doc §5.8
/// budget: 256 MiB per sandbox child, kill-on-close so stragglers
/// don't outlive the host, die-on-unhandled-exception so a native
/// crash in the child doesn't leave it in a broken state, and UI
/// restrictions blocking desktop / clipboard / global atom access.
/// </summary>
public sealed class JobObjectOptions
{
    /// <summary>
    /// Maximum process private commit in bytes. The kernel kills any
    /// process in the job that exceeds this. Default 256 MiB.
    /// Set to 0 to disable the cap.
    /// </summary>
    public long ProcessMemoryLimitBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// When true, closing the last handle to the job object
    /// terminates every process in the job. This is the most
    /// important lifetime invariant for the sandbox: if the host
    /// crashes, every sandbox child dies with it, no stragglers.
    /// Default: true. Do not disable in production.
    /// </summary>
    public bool KillOnJobClose { get; init; } = true;

    /// <summary>
    /// When true, an unhandled exception in a child process is
    /// treated as process termination (rather than invoking the
    /// default Windows "app crashed" dialog + wait). Default: true.
    /// </summary>
    public bool DieOnUnhandledException { get; init; } = true;

    /// <summary>
    /// Apply the full set of UI restrictions to the job: block
    /// desktop, clipboard, global atoms, handles outside the job,
    /// display-settings changes, exit-windows, system parameter
    /// changes. Default: true. There is essentially never a reason
    /// to run a sandbox child with UI access.
    /// </summary>
    public bool RestrictUI { get; init; } = true;
}
