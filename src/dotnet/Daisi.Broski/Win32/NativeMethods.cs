using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Daisi.Broski.Win32;

/// <summary>
/// Raw Win32 P/Invoke declarations for the sandbox subsystem. Everything
/// here is internal — callers should go through the managed wrappers
/// (<see cref="JobObject"/>, the eventual <see cref="SandboxLauncher"/>,
/// etc.) rather than touching kernel32 directly.
///
/// Signatures follow the Win32 C headers exactly. Return-value
/// conventions: most functions return <c>BOOL</c> (non-zero = success);
/// failures populate <see cref="Marshal.GetLastWin32Error"/>, so every
/// call goes through <see cref="LibraryImportAttribute"/> with
/// <c>SetLastError = true</c>.
/// </summary>
internal static partial class NativeMethods
{
    internal const string Kernel32 = "kernel32.dll";

    // -------------------------------------------------------------
    // Job Object APIs
    // -------------------------------------------------------------

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeJobObjectHandle CreateJobObject(
        IntPtr lpJobAttributes, string? lpName);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(
        SafeJobObjectHandle hJob,
        IntPtr hProcess);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(
        SafeJobObjectHandle hJob,
        JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    // -------------------------------------------------------------
    // Handle cleanup
    // -------------------------------------------------------------

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Safe handle for a Win32 Job Object. Ensures the handle is closed
/// on finalization even if the managed <see cref="Daisi.Broski.Sandbox.JobObject"/>
/// wrapper is dropped without disposal.
/// </summary>
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobObjectHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>
/// <c>JOBOBJECTINFOCLASS</c> — the infoClass parameter to
/// <c>Set/QueryInformationJobObject</c>. Only the classes we actually
/// use are listed; see the Windows SDK for the full enumeration.
/// </summary>
internal enum JobObjectInfoClass
{
    BasicLimitInformation = 2,
    BasicUIRestrictions = 4,
    ExtendedLimitInformation = 9,
}

/// <summary>
/// <c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> — the "basic" subset of the
/// Job Object limit structure, embedded in
/// <see cref="JobObjectExtendedLimitInformation"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public JobObjectLimitFlags LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary>
/// <c>IO_COUNTERS</c> — embedded in
/// <see cref="JobObjectExtendedLimitInformation"/>. We never read or
/// set these; the layout is present only so the extended struct
/// is the right size for the Win32 API.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>
/// <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> — the superset structure
/// passed to <c>SetInformationJobObject</c> with
/// <see cref="JobObjectInfoClass.ExtendedLimitInformation"/>. This is
/// where we set the per-process memory cap, kill-on-close, and
/// die-on-unhandled-exception flags.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

[Flags]
internal enum JobObjectLimitFlags : uint
{
    // Basic limits
    LimitWorkingSet = 0x00000001,
    LimitProcessTime = 0x00000002,
    LimitJobTime = 0x00000004,
    LimitActiveProcess = 0x00000008,
    LimitAffinity = 0x00000010,
    LimitPriorityClass = 0x00000020,
    LimitPreserveJobTime = 0x00000040,
    LimitSchedulingClass = 0x00000080,

    // Extended limits
    LimitProcessMemory = 0x00000100,
    LimitJobMemory = 0x00000200,
    LimitDieOnUnhandledException = 0x00000400,
    LimitBreakawayOk = 0x00000800,
    LimitSilentBreakawayOk = 0x00001000,
    LimitKillOnJobClose = 0x00002000,
    LimitSubsetAffinity = 0x00004000,
}

/// <summary>
/// <c>JOBOBJECT_BASIC_UI_RESTRICTIONS</c>. Applied via
/// <see cref="JobObjectInfoClass.BasicUIRestrictions"/> to block a
/// sandbox child from interacting with the desktop, clipboard,
/// handles outside the job, etc.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicUIRestrictions
{
    public JobObjectUIFlags UIRestrictionsClass;
}

[Flags]
internal enum JobObjectUIFlags : uint
{
    None = 0,
    HandlesRestricted = 0x00000001,
    ReadClipboardRestricted = 0x00000002,
    WriteClipboardRestricted = 0x00000004,
    SystemParametersRestricted = 0x00000008,
    DisplaySettingsRestricted = 0x00000010,
    GlobalAtomsRestricted = 0x00000020,
    DesktopRestricted = 0x00000040,
    ExitWindowsRestricted = 0x00000080,

    /// <summary>Convenience: every restriction flag Windows offers.</summary>
    All =
        HandlesRestricted |
        ReadClipboardRestricted |
        WriteClipboardRestricted |
        SystemParametersRestricted |
        DisplaySettingsRestricted |
        GlobalAtomsRestricted |
        DesktopRestricted |
        ExitWindowsRestricted,
}
