using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Daisi.Broski.Win32;

namespace Daisi.Broski;

/// <summary>
/// Managed wrapper around a Win32 Job Object. The kernel-enforced
/// boundary for every sandbox child: memory cap, kill-on-close,
/// die-on-unhandled-exception, UI restrictions.
///
/// Lifecycle:
///
///   1. <see cref="Create"/> — creates an unnamed job object with
///      limits applied from <see cref="JobObjectOptions"/>.
///   2. <see cref="AssignProcess"/> — assigns a process handle
///      (typically from <see cref="System.Diagnostics.Process"/>)
///      to the job. The process must be in the <i>CREATE_SUSPENDED</i>
///      state at the time of assignment so no code runs before the
///      limits take effect.
///   3. <see cref="Dispose"/> — closes the job handle. If
///      <see cref="JobObjectOptions.KillOnJobClose"/> was true
///      (the default), every process still in the job dies now.
///
/// The object is Windows-only. Attempting to construct it on any
/// other OS throws <see cref="PlatformNotSupportedException"/> with
/// a pointer to the roadmap entry that tracks cross-platform sandbox
/// support.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private readonly SafeJobObjectHandle _handle;
    private bool _disposed;

    /// <summary>Construct a job object with the given options and
    /// immediately apply its limits. Throws <see cref="Win32Exception"/>
    /// on any underlying Win32 failure.</summary>
    public static JobObject Create(JobObjectOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "JobObject is Windows-only. Cross-platform sandboxing is tracked in " +
                "docs/roadmap.md phase 5 (Linux: unshare + seccomp + cgroups; " +
                "macOS: sandbox_init).");
        }

        options ??= new JobObjectOptions();
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "CreateJobObject failed.");
        }

        var job = new JobObject(handle);
        try
        {
            job.ApplyExtendedLimits(options);
            if (options.RestrictUI)
            {
                job.ApplyUIRestrictions();
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        return job;
    }

    private JobObject(SafeJobObjectHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Assign an existing process to this job. The process should
    /// still be in the <c>CREATE_SUSPENDED</c> state when this is
    /// called — assigning after the process has started running
    /// means the limits aren't enforced during the window between
    /// process creation and assignment.
    /// </summary>
    public void AssignProcess(IntPtr processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Read back the memory limit actually set on this job —
    /// useful for verifying the limits were applied correctly during
    /// testing.</summary>
    public long QueryProcessMemoryLimit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _handle,
                    JobObjectInfoClass.ExtendedLimitInformation,
                    buffer,
                    (uint)size,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "QueryInformationJobObject failed.");
            }

            var info = Marshal.PtrToStructure<JobObjectExtendedLimitInformation>(buffer);
            return (long)info.ProcessMemoryLimit;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Read back the limit flag set on this job.</summary>
    public JobObjectLimitFlagsPublic QueryLimitFlags()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _handle,
                    JobObjectInfoClass.ExtendedLimitInformation,
                    buffer,
                    (uint)size,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "QueryInformationJobObject failed.");
            }

            var info = Marshal.PtrToStructure<JobObjectExtendedLimitInformation>(buffer);
            return (JobObjectLimitFlagsPublic)info.BasicLimitInformation.LimitFlags;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ApplyExtendedLimits(JobObjectOptions options)
    {
        var info = new JobObjectExtendedLimitInformation();

        var flags = JobObjectLimitFlags.LimitBreakawayOk; // let child spawn utility processes

        if (options.KillOnJobClose) flags |= JobObjectLimitFlags.LimitKillOnJobClose;
        if (options.DieOnUnhandledException) flags |= JobObjectLimitFlags.LimitDieOnUnhandledException;

        if (options.ProcessMemoryLimitBytes > 0)
        {
            flags |= JobObjectLimitFlags.LimitProcessMemory;
            info.ProcessMemoryLimit = (nuint)options.ProcessMemoryLimitBytes;
        }

        info.BasicLimitInformation.LimitFlags = flags;

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(
                    _handle,
                    JobObjectInfoClass.ExtendedLimitInformation,
                    buffer,
                    (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetInformationJobObject(ExtendedLimitInformation) failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ApplyUIRestrictions()
    {
        var restrictions = new JobObjectBasicUIRestrictions
        {
            UIRestrictionsClass = JobObjectUIFlags.All,
        };

        int size = Marshal.SizeOf<JobObjectBasicUIRestrictions>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(restrictions, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(
                    _handle,
                    JobObjectInfoClass.BasicUIRestrictions,
                    buffer,
                    (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetInformationJobObject(BasicUIRestrictions) failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

/// <summary>
/// Public projection of <see cref="Daisi.Broski.Sandbox.Win32.JobObjectLimitFlags"/>
/// so test and inspection code can read the flags set on a job without
/// pulling in the internal Win32 namespace. Values match the Win32
/// constants bit-for-bit so callers can bitwise-compare.
/// </summary>
[Flags]
public enum JobObjectLimitFlagsPublic : uint
{
    None = 0,
    ProcessMemory = 0x00000100,
    DieOnUnhandledException = 0x00000400,
    BreakawayOk = 0x00000800,
    KillOnJobClose = 0x00002000,
}
