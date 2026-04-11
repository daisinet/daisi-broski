using System.Diagnostics;
using Daisi.Broski.Engine.Tests.Net;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Cli;

/// <summary>
/// Black-box tests for the <c>daisi-broski</c> CLI. Spawns the real
/// built executable as a subprocess against a <see cref="LocalHttpServer"/>
/// fixture and verifies stdout / stderr / exit code.
///
/// Windows-only. The CLI works on other platforms via <c>--no-sandbox</c>,
/// but the default path (which is what we most want to exercise) requires
/// the Win32 Job Object infrastructure in the host library. Phase 5 will
/// add cross-platform sandboxing; these tests will get a matching
/// non-Windows variant then.
/// </summary>
public class CliSmokeTests
{
    [Fact]
    public async Task Fetch_with_select_returns_matching_elements_via_sandbox()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        const string html = """
            <!DOCTYPE html>
            <html>
              <head><title>cli smoke</title></head>
              <body>
                <a class="storylink">Alpha</a>
                <a class="storylink">Beta</a>
                <a>Unrelated</a>
                <a class="storylink">Gamma</a>
              </body>
            </html>
            """;

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, html);
            return Task.CompletedTask;
        });

        var result = await RunCli(
            "fetch", server.BaseUrl.AbsoluteUri,
            "--select", ".storylink");

        Assert.Equal(0, result.ExitCode);

        var lines = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        Assert.Equal(["Alpha", "Beta", "Gamma"], lines);
        Assert.Contains("3 match(es)", result.Stderr);
        Assert.DoesNotContain("[no-sandbox]", result.Stderr); // default path
    }

    [Fact]
    public async Task Fetch_with_no_sandbox_flag_uses_in_process_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var server = new LocalHttpServer(ctx =>
        {
            LocalHttpServer.WriteText(ctx, "<html><body><h1>ok</h1></body></html>");
            return Task.CompletedTask;
        });

        var result = await RunCli(
            "fetch", server.BaseUrl.AbsoluteUri,
            "--no-sandbox",
            "--select", "h1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.Stdout);
        Assert.Contains("[no-sandbox]", result.Stderr);
    }

    [Fact]
    public async Task Fetch_with_bad_url_exits_with_usage_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        var result = await RunCli("fetch", "not-a-url");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("not an absolute", result.Stderr);
    }

    [Fact]
    public async Task Fetch_with_no_arguments_shows_usage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        var result = await RunCli("fetch");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("requires a URL", result.Stderr);
    }

    [Fact]
    public async Task Help_with_no_args_prints_usage_and_exits_nonzero()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        var result = await RunCli();

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Usage:", result.Stdout);
    }

    // -------- helpers --------

    private static async Task<CliResult> RunCli(params string[] args)
    {
        var exe = ResolveCliPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for the CLI");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // Generous cap so slow sandbox-startup paths don't false-fail.
        if (!proc.WaitForExit(15_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("CLI did not exit within 15 seconds");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CliResult(proc.ExitCode, stdout, stderr);
    }

    private static string ResolveCliPath()
    {
        // Same half-copy caveat as SandboxLauncher: MSBuild copies the
        // apphost .exe into the test's bin dir even with
        // ReferenceOutputAssembly="false", but not the managed .dll.
        // Running the half-copy produces a cryptic "application to
        // execute does not exist" error. Require both to exist.
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "daisi-broski.exe");
        if (IsCompleteCliExe(candidate)) return candidate;

        // Fallback: walk up looking for the project's bin dir.
        var probe = dir;
        while (!string.IsNullOrEmpty(probe))
        {
            var guess = Path.Combine(probe, "Daisi.Broski.Cli", "bin", "Debug", "net10.0",
                "daisi-broski.exe");
            if (IsCompleteCliExe(guess)) return guess;
            probe = Path.GetDirectoryName(probe);
        }

        throw new FileNotFoundException(
            "Could not locate a complete daisi-broski.exe (apphost + managed .dll). " +
            "Build the CLI project first.");
    }

    private static bool IsCompleteCliExe(string exePath)
    {
        if (!File.Exists(exePath)) return false;
        var dll = Path.ChangeExtension(exePath, ".dll");
        return File.Exists(dll);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
