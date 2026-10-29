using System.Diagnostics;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>Runs harmless cmd.exe commands only; no real program is ever uninstalled here.</summary>
public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Returns_the_exit_code_of_the_process()
    {
        var result = await new ProcessRunner().RunAsync(Cmd, "/c exit 7", CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task Missing_file_is_reported_not_thrown()
    {
        var result = await new ProcessRunner().RunAsync(@"C:\definitely\missing\unins000.exe", "/S", CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Waits_for_child_processes_that_outlive_the_first_process()
    {
        // cmd exits at once; the detached ping keeps running ~2 seconds. The runner must wait for it too.
        var watch = Stopwatch.StartNew();

        var result = await new ProcessRunner().RunAsync(Cmd, "/c start \"\" /b ping -n 3 127.0.0.1 >nul", CancellationToken.None);

        watch.Stop();
        Assert.True(result.Started);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"returned after only {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancel_stops_waiting_without_reporting_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        var result = await new ProcessRunner().RunAsync(Cmd, "/c ping -n 6 127.0.0.1 >nul", cts.Token);

        Assert.True(result.Started);
        Assert.True(result.Canceled);
    }
}
