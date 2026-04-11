using System.Diagnostics;
using System.Runtime.Versioning;
using Daisi.Broski.Sandbox;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Sandbox;

/// <summary>
/// Tests for <see cref="JobObject"/>. Windows-only — every test skips
/// cleanly via <see cref="Assert.SkipWhen"/> on other platforms.
///
/// These tests do NOT assign arbitrary child processes. Assigning the
/// current test runner to the job would hard-crash it as soon as the
/// kill-on-close handle closed at the end of the test. Instead we
/// create a suspended cmd.exe, assign it, verify, and kill it.
/// </summary>
[SupportedOSPlatform("windows")]
public class JobObjectTests
{
    [Fact]
    public void Create_and_dispose_does_not_throw()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var job = JobObject.Create();
        // No-op: just construct and dispose.
    }

    [Fact]
    public void Create_with_memory_limit_applies_that_limit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const long limit = 128L * 1024 * 1024;
        using var job = JobObject.Create(new JobObjectOptions
        {
            ProcessMemoryLimitBytes = limit,
        });

        Assert.Equal(limit, job.QueryProcessMemoryLimit());
    }

    [Fact]
    public void Create_sets_kill_on_close_flag_by_default()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var job = JobObject.Create();

        var flags = job.QueryLimitFlags();
        Assert.True(flags.HasFlag(JobObjectLimitFlagsPublic.KillOnJobClose));
        Assert.True(flags.HasFlag(JobObjectLimitFlagsPublic.DieOnUnhandledException));
        Assert.True(flags.HasFlag(JobObjectLimitFlagsPublic.ProcessMemory));
    }

    [Fact]
    public void Create_without_memory_limit_omits_the_process_memory_flag()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var job = JobObject.Create(new JobObjectOptions
        {
            ProcessMemoryLimitBytes = 0,
        });

        var flags = job.QueryLimitFlags();
        Assert.False(flags.HasFlag(JobObjectLimitFlagsPublic.ProcessMemory));
    }

    [Fact]
    public void Assigning_a_process_to_the_job_and_killing_it_does_not_take_us_down()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        // Spawn a real child we can safely assign and kill. cmd.exe with
        // no arguments waits on stdin; we kill it explicitly at the end.
        using var job = JobObject.Create(new JobObjectOptions
        {
            ProcessMemoryLimitBytes = 64L * 1024 * 1024,
            KillOnJobClose = false, // don't kill on dispose during the test
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 30 > NUL",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            job.AssignProcess(process!.Handle);
            // If we got here, assignment succeeded. Clean up.
        }
        finally
        {
            try { process!.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
        }
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        var job = JobObject.Create();
        job.Dispose();
        job.Dispose(); // second call should not throw
    }

    [Fact]
    public void QueryProcessMemoryLimit_after_dispose_throws_ObjectDisposedException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        var job = JobObject.Create();
        job.Dispose();
        Assert.Throws<ObjectDisposedException>(() => job.QueryProcessMemoryLimit());
    }
}
