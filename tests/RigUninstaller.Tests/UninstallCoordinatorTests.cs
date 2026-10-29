using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class UninstallCoordinatorTests
{
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeFinder _finder = new();
    private readonly FakeLog _log = new();

    private UninstallCoordinator Create() => new(_runner, _finder, _restore, _dialogs, _log);

    private static InstalledProgram Program(string? uninstall = @"""C:\A\unins000.exe"" /S", string? quiet = @"""C:\A\unins000.exe"" /VERYSILENT") =>
        new()
        {
            Name = "Acme Editor",
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
            KeyName = "Acme",
        };

    private static RunningProcessInfo Running(string name) => new(10, name, @"C:\A\" + name + ".exe");

    // ---- interactive flow ----

    [Fact]
    public async Task Cancelling_the_confirmation_changes_nothing()
    {
        _dialogs.Choice = null;

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Cancelled, outcome.Status);
        Assert.Empty(_runner.Calls);
        Assert.Equal(0, _restore.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Normal_flow_checks_apps_makes_a_restore_point_then_runs_the_uninstaller()
    {
        _dialogs.Choice = new UninstallChoice(Quiet: false, CreateRestorePoint: true);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.True(outcome.Started);
        Assert.True(outcome.RestorePointCreated);
        Assert.Equal(1, _finder.Calls);
        Assert.Equal(1, _restore.Calls);
        Assert.Equal((@"C:\A\unins000.exe", "/S"), Assert.Single(_runner.Calls));
        Assert.Contains("Acme Editor", _restore.LastDescription);
    }

    [Fact]
    public async Task Quiet_choice_runs_the_quiet_plan()
    {
        _dialogs.Choice = new UninstallChoice(Quiet: true, CreateRestorePoint: false);

        await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal("/VERYSILENT", Assert.Single(_runner.Calls).Arguments);
    }

    [Fact]
    public async Task No_restore_point_is_made_when_the_user_unticks_it()
    {
        _dialogs.Choice = new UninstallChoice(false, false);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(0, _restore.Calls);
        Assert.False(outcome.RestorePointCreated);
    }

    [Fact]
    public async Task Program_without_any_command_fails_without_asking_anything()
    {
        var outcome = await Create().RunInteractiveAsync(Program(uninstall: null, quiet: null), CancellationToken.None);

        Assert.Equal(UninstallStatus.Failed, outcome.Status);
        Assert.False(outcome.Started);
        Assert.Contains("no uninstall command", outcome.Message);
        Assert.Equal(0, _dialogs.ConfirmCalls);
        Assert.Empty(_runner.Calls);
    }

    // ---- running apps ----

    [Fact]
    public async Task Running_apps_and_cancel_stops_before_anything_happens()
    {
        _finder.Answers.Enqueue(new[] { Running("acme") });
        _dialogs.RunningAnswers.Enqueue(RunningAppsChoice.Cancel);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Cancelled, outcome.Status);
        Assert.Equal(0, _restore.Calls);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Retry_checks_again_and_continues_once_the_app_is_closed()
    {
        _finder.Answers.Enqueue(new[] { Running("acme") });
        _finder.Answers.Enqueue(Array.Empty<RunningProcessInfo>());
        _dialogs.RunningAnswers.Enqueue(RunningAppsChoice.Retry);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Equal(2, _finder.Calls);
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Continue_anyway_runs_the_uninstaller_with_the_app_still_open()
    {
        _finder.Answers.Enqueue(new[] { Running("acme") });
        _dialogs.RunningAnswers.Enqueue(RunningAppsChoice.ContinueAnyway);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Single(_runner.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("continue", StringComparison.OrdinalIgnoreCase));
    }

    // ---- restore point ----

    [Fact]
    public async Task Failed_restore_point_and_user_declines_cancels_the_uninstall()
    {
        _restore.Result = new RestorePointResult(false, "System Restore is off.");
        _dialogs.ContinueWithoutRestore = false;

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Cancelled, outcome.Status);
        Assert.Empty(_runner.Calls);
        Assert.Equal("System Restore is off.", Assert.Single(_dialogs.RestoreAsked));
    }

    [Fact]
    public async Task Failed_restore_point_and_user_continues_runs_without_one()
    {
        _restore.Result = new RestorePointResult(false, "System Restore is off.");
        _dialogs.ContinueWithoutRestore = true;

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.False(outcome.RestorePointCreated);
        Assert.Single(_runner.Calls);
    }

    // ---- running the uninstaller ----

    [Fact]
    public async Task Start_failure_is_reported_as_failed()
    {
        _runner.Result = new ProcessRunResult(false, -1, false, "The uninstaller file was not found: C:\\A\\unins000.exe");

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Failed, outcome.Status);
        Assert.False(outcome.Started);
        Assert.Contains("not found", outcome.Message);
    }

    [Fact]
    public async Task Stopping_the_wait_is_reported_separately()
    {
        _runner.Result = new ProcessRunResult(true, -1, true, null);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.StoppedWaiting, outcome.Status);
        Assert.True(outcome.Started);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    public async Task Exit_codes_that_ask_for_a_restart_are_flagged(int exitCode, bool reboot)
    {
        _runner.Result = new ProcessRunResult(true, exitCode, false, null);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.Equal(reboot, outcome.RebootRequired);
    }

    [Fact]
    public async Task Odd_exit_codes_are_still_completed_so_the_rescan_can_decide()
    {
        _runner.Result = new ProcessRunResult(true, 1602, false, null);

        var outcome = await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Equal(1602, outcome.ExitCode);
    }

    [Fact]
    public async Task Every_step_is_logged()
    {
        _dialogs.Choice = new UninstallChoice(false, true);

        await Create().RunInteractiveAsync(Program(), CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.Contains("Starting uninstall of Acme Editor"));
        Assert.Contains(_log.Lines, l => l.Contains("Restore point"));
        Assert.Contains(_log.Lines, l => l.Contains("Running uninstaller") && l.Contains("unins000.exe"));
        Assert.Contains(_log.Lines, l => l.Contains("Uninstaller finished"));
    }

    // ---- non-interactive (used by batch uninstall later) ----

    [Fact]
    public async Task RunAsync_skips_the_confirmation_and_uses_the_given_options()
    {
        var outcome = await Create().RunAsync(Program(), new UninstallOptions(Quiet: true, CreateRestorePoint: false), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Equal(0, _dialogs.ConfirmCalls);
        Assert.Equal("/VERYSILENT", Assert.Single(_runner.Calls).Arguments);
    }

    [Fact]
    public async Task RunAsync_quiet_without_a_quiet_plan_fails_instead_of_opening_windows()
    {
        var outcome = await Create().RunAsync(Program(quiet: null), new UninstallOptions(true, false), CancellationToken.None);

        Assert.Equal(UninstallStatus.Failed, outcome.Status);
        Assert.Empty(_runner.Calls);
        Assert.Contains("quiet", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }
}
