using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ForceRemoveCoordinatorTests
{
    private readonly FakeLeftoverCleaner _cleaner = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeKeep _keep = new();
    private readonly FakeFiles _files = new();
    private readonly MemoryJournal _journal = new();
    private readonly FakeLog _log = new();

    private ForceRemoveCoordinator Create() =>
        new(_cleaner, _restore, _dialogs, _keep, _files, new SafetyGuard(VmTestBase.TestRoots), _log, _journal);

    private static InstalledProgram Program(string? location = @"C:\Program Files\Acme", string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme") => new()
    {
        Name = "Acme",
        InstallLocation = location,
        RegistryKeyPath = key,
        KeyName = "Acme",
    };

    private Task<ForceRemoveOutcome> Run(InstalledProgram program, bool restoreAlready = false, params InstalledProgram[] others) =>
        Create().RunAsync(program, others, restoreAlready, CancellationToken.None);

    [Fact]
    public async Task A_program_marked_Keep_is_refused_before_any_question()
    {
        var program = Program();
        _keep.SetKept(program, true);

        var outcome = await Run(program);

        Assert.Equal(ForceRemoveStatus.Refused, outcome.Status);
        Assert.Contains("Keep", outcome.Message);
        Assert.Empty(_cleaner.Calls);
        Assert.Null(_dialogs.LastForceRemoveItems);
    }

    [Fact]
    public async Task A_protected_registry_entry_is_refused()
    {
        var outcome = await Run(Program(key: @"HKLM\SYSTEM\CurrentControlSet\Services\x"));

        Assert.Equal(ForceRemoveStatus.Refused, outcome.Status);
        Assert.Empty(_cleaner.Calls);
    }

    [Fact]
    public async Task The_warning_lists_the_registry_entry_and_the_install_folder()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");

        await Run(Program());

        Assert.Equal(2, _dialogs.LastForceRemoveItems!.Count);
        Assert.Contains(_dialogs.LastForceRemoveItems, i => i.Kind == LeftoverKind.RegistryKey);
        Assert.Contains(_dialogs.LastForceRemoveItems, i => i.Kind == LeftoverKind.Folder && i.Path == @"C:\Program Files\Acme");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\Acme", "protected")]
    [InlineData(@"C:\Program Files\Gone", "does not exist")]
    [InlineData(null, "No install folder")]
    public async Task The_folder_is_left_out_when_it_is_protected_missing_or_unknown(string? location, string noteText)
    {
        _files.Existing.Add(@"C:\Windows\System32\Acme");

        await Run(Program(location));

        var item = Assert.Single(_dialogs.LastForceRemoveItems!);
        Assert.Equal(LeftoverKind.RegistryKey, item.Kind);
        Assert.Contains(noteText, _dialogs.LastForceRemoveNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_folder_another_program_uses_is_left_out()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");
        var other = new InstalledProgram { Name = "Acme Plugin", InstallLocation = @"C:\Program Files\Acme\Plugin", RegistryKeyPath = @"HKLM\x\p", KeyName = "p" };

        await Run(Program(), false, other);

        Assert.Single(_dialogs.LastForceRemoveItems!);
        Assert.Contains("Acme Plugin", _dialogs.LastForceRemoveNote);
    }

    [Fact]
    public async Task Backing_out_of_the_warning_changes_nothing()
    {
        _dialogs.ForceRemoveResponse = null;

        var outcome = await Run(Program());

        Assert.Equal(ForceRemoveStatus.Cancelled, outcome.Status);
        Assert.Empty(_cleaner.Calls);
        Assert.Equal(0, _restore.Calls);
    }

    [Fact]
    public async Task Confirmed_removal_makes_a_restore_point_then_cleans_exactly_the_listed_items()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");

        var outcome = await Run(Program());

        Assert.Equal(ForceRemoveStatus.Done, outcome.Status);
        Assert.True(outcome.RestorePointCreated);
        Assert.Equal(1, _restore.Calls);
        Assert.Equal(_dialogs.LastForceRemoveItems, Assert.Single(_cleaner.Calls));
        Assert.Contains(_journal.Entries, e => e.Kind == BackupKinds.RestorePoint);
    }

    [Fact]
    public async Task No_restore_point_is_made_when_unticked_or_already_made()
    {
        _dialogs.ForceRemoveResponse = new ForceRemoveChoice(CreateRestorePoint: false);
        await Run(Program());

        _dialogs.ForceRemoveResponse = new ForceRemoveChoice(CreateRestorePoint: true);
        await Run(Program(), restoreAlready: true);

        Assert.Equal(0, _restore.Calls);
    }

    [Fact]
    public async Task A_failed_restore_point_and_a_no_cancels_everything()
    {
        _restore.Result = new RestorePointResult(false, "off");
        _dialogs.ContinueWithoutRestore = false;

        var outcome = await Run(Program());

        Assert.Equal(ForceRemoveStatus.Cancelled, outcome.Status);
        Assert.Empty(_cleaner.Calls);
    }

    [Fact]
    public async Task A_failed_restore_point_and_a_yes_goes_on()
    {
        _restore.Result = new RestorePointResult(false, "off");
        _dialogs.ContinueWithoutRestore = true;

        var outcome = await Run(Program());

        Assert.Equal(ForceRemoveStatus.Done, outcome.Status);
        Assert.False(outcome.RestorePointCreated);
    }

    [Fact]
    public async Task When_nothing_could_be_removed_the_failure_is_reported()
    {
        var item = new LeftoverItem(LeftoverKind.RegistryKey, @"HKLM\x", Confidence.High, "r");
        _cleaner.Result = new CleanupResult(0, 0, 0, new[] { new CleanupFailure(item, "Access denied.") }, false);

        var outcome = await Run(Program());

        Assert.Equal(ForceRemoveStatus.Failed, outcome.Status);
        Assert.Contains("Access denied", outcome.Message);
    }
}

public class BatchCoordinatorTests
{
    private readonly FakeCoordinator _uninstall = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeKeep _keep = new();
    private readonly MemoryJournal _journal = new();
    private readonly FakeLog _log = new();
    private readonly List<BatchProgress> _progress = new();

    private BatchCoordinator Create() => new(_uninstall, _restore, _dialogs, _keep, _log, _journal);

    private sealed class ListProgress : IProgress<BatchProgress>
    {
        private readonly List<BatchProgress> _list;

        public ListProgress(List<BatchProgress> list) => _list = list;

        public void Report(BatchProgress value) => _list.Add(value);
    }

    private static InstalledProgram P(string name, string? quiet = null) => new()
    {
        Name = name,
        UninstallString = $@"""C:\{name}\unins.exe""",
        QuietUninstallString = quiet,
        RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
        KeyName = name,
    };

    private Task<IReadOnlyList<BatchItemResult>> Run(BatchOptions options, CancellationToken ct, params InstalledProgram[] programs) =>
        Create().RunAsync(programs, options, new ListProgress(_progress), ct);

    private static readonly BatchOptions NoRestore = new(PreferQuiet: false, CreateRestorePoint: false);

    [Fact]
    public async Task Programs_are_uninstalled_in_order_and_progress_is_reported()
    {
        var results = await Run(NoRestore, CancellationToken.None, P("A"), P("B"));

        Assert.Equal(new[] { "A", "B" }, _uninstall.Requested.Select(p => p.Name));
        Assert.All(results, r => Assert.Equal(BatchItemStatus.Done, r.Status));
        Assert.Equal(
            new[] { BatchItemStatus.Running, BatchItemStatus.Done, BatchItemStatus.Running, BatchItemStatus.Done },
            _progress.Select(p => p.Status));
        Assert.All(_progress, p => Assert.Equal(2, p.Total));
    }

    [Fact]
    public async Task One_restore_point_covers_the_whole_batch()
    {
        await Run(new BatchOptions(false, true), CancellationToken.None, P("A"), P("B"));

        Assert.Equal(1, _restore.Calls);
        Assert.All(_uninstall.OptionsUsed, o => Assert.False(o.CreateRestorePoint));
        Assert.Contains(_journal.Entries, e => e.Kind == BackupKinds.RestorePoint);
    }

    [Fact]
    public async Task A_failed_restore_point_and_a_no_cancels_every_program()
    {
        _restore.Result = new RestorePointResult(false, "off");
        _dialogs.ContinueWithoutRestore = false;

        var results = await Run(new BatchOptions(false, true), CancellationToken.None, P("A"), P("B"));

        Assert.All(results, r => Assert.Equal(BatchItemStatus.Cancelled, r.Status));
        Assert.Empty(_uninstall.Requested);
    }

    [Fact]
    public async Task Programs_marked_Keep_are_skipped_and_the_others_still_run()
    {
        var kept = P("Kept");
        _keep.SetKept(kept, true);

        var results = await Run(NoRestore, CancellationToken.None, kept, P("B"));

        Assert.Equal(BatchItemStatus.Skipped, results[0].Status);
        Assert.Equal(new[] { "B" }, _uninstall.Requested.Select(p => p.Name));
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_next_program()
    {
        _uninstall.Handler = (program, _) => Task.FromResult(program.Name == "A"
            ? new UninstallOutcome(UninstallStatus.Failed, "file missing")
            : new UninstallOutcome(UninstallStatus.Completed, "ok", 0, true));

        var results = await Run(NoRestore, CancellationToken.None, P("A"), P("B"));

        Assert.Equal(BatchItemStatus.Failed, results[0].Status);
        Assert.Equal("file missing", results[0].Message);
        Assert.Equal(BatchItemStatus.Done, results[1].Status);
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_failure_of_that_program_only()
    {
        _uninstall.Handler = (program, _) => program.Name == "A"
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new UninstallOutcome(UninstallStatus.Completed, "ok", 0, true));

        var results = await Run(NoRestore, CancellationToken.None, P("A"), P("B"));

        Assert.Equal(BatchItemStatus.Failed, results[0].Status);
        Assert.Equal(BatchItemStatus.Done, results[1].Status);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("boom"));
    }

    [Fact]
    public async Task Stopping_the_wait_stops_the_rest_of_the_batch()
    {
        _uninstall.Handler = (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.StoppedWaiting, "stopped", null, true));

        var results = await Run(NoRestore, CancellationToken.None, P("A"), P("B"), P("C"));

        Assert.Single(_uninstall.Requested);
        Assert.Equal(new[] { BatchItemStatus.Cancelled, BatchItemStatus.Cancelled, BatchItemStatus.Cancelled }, results.Select(r => r.Status));
    }

    [Fact]
    public async Task A_cancelled_token_marks_the_remaining_programs_as_not_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await Run(NoRestore, cts.Token, P("A"), P("B"));

        Assert.Empty(_uninstall.Requested);
        Assert.All(results, r => Assert.Equal(BatchItemStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task Quiet_is_used_only_for_programs_that_have_a_quiet_command()
    {
        await Run(new BatchOptions(PreferQuiet: true, CreateRestorePoint: false), CancellationToken.None, P("Loud"), P("Silent", quiet: @"""C:\Silent\unins.exe"" /S"));

        Assert.Equal(new[] { false, true }, _uninstall.OptionsUsed.Select(o => o.Quiet));
    }
}

public class DefenderScannerTests
{
    private readonly FakeConsoleRunner _console = new();
    private readonly FakeFiles _files = new();
    private readonly FakeLog _log = new();

    private DefenderScanner Create() => new(_console, new SafetyGuard(VmTestBase.TestRoots), _files, _log, @"C:\fake\MpCmdRun.exe");

    private static InstalledProgram Program(string? location = @"C:\Program Files\Acme") => new()
    {
        Name = "Acme",
        InstallLocation = location,
        RegistryKeyPath = @"HKLM\x\Acme",
        KeyName = "Acme",
    };

    [Fact]
    public async Task A_clean_scan_runs_a_custom_scan_on_the_install_folder()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");
        _console.Result = new ScriptResult(0, "Scan starting...\r\nScan finished.\r\nScanning C:\\Program Files\\Acme found no threats.");

        var result = await Create().ScanProgramAsync(Program(), CancellationToken.None);

        Assert.Equal(DefenderStatus.Clean, result.Status);
        var call = Assert.Single(_console.Calls);
        Assert.Equal(new[] { "-Scan", "-ScanType", "3", "-File", @"C:\Program Files\Acme" }, call.Args);
    }

    [Fact]
    public async Task Threats_are_reported_with_their_count()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");
        _console.Result = new ScriptResult(2, "Scan finished.\r\nScanning C:\\Program Files\\Acme found 3 threats.");

        var result = await Create().ScanProgramAsync(Program(), CancellationToken.None);

        Assert.Equal(DefenderStatus.ThreatsFound, result.Status);
        Assert.Contains("3 threat", result.Message);
    }

    [Fact]
    public async Task A_scan_that_could_not_finish_is_a_failure_with_the_exit_code()
    {
        _files.Existing.Add(@"C:\Program Files\Acme");
        _console.Result = new ScriptResult(2, "Scan failed: service not running");

        var result = await Create().ScanProgramAsync(Program(), CancellationToken.None);

        Assert.Equal(DefenderStatus.Failed, result.Status);
        Assert.Contains("exit code 2", result.Message);
    }

    [Fact]
    public async Task Nothing_is_scanned_when_no_install_folder_is_known()
    {
        var result = await Create().ScanProgramAsync(Program(location: null), CancellationToken.None);

        Assert.Equal(DefenderStatus.NothingToScan, result.Status);
        Assert.Empty(_console.Calls);
    }

    [Fact]
    public void The_scanner_is_available_only_when_the_tool_exists()
    {
        Assert.False(Create().IsAvailable);
    }
}

public class UninstallCoordinatorDefenderTests
{
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeDefender _defender = new();
    private readonly MemoryJournal _journal = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeLog _log = new();

    private UninstallCoordinator Create() =>
        new(_runner, new FakeFinder(), _restore, _dialogs, _log, _defender, _journal);

    private static InstalledProgram Program() => new()
    {
        Name = "Acme",
        UninstallString = @"""C:\A\unins000.exe""",
        RegistryKeyPath = @"HKLM\x\Acme",
        KeyName = "Acme",
    };

    [Fact]
    public async Task Threats_and_a_no_stop_the_uninstall_before_it_starts()
    {
        _defender.Result = new DefenderResult(DefenderStatus.ThreatsFound, "Threat!");
        _dialogs.ContinueAfterThreats = false;

        var outcome = await Create().RunAsync(Program(), new UninstallOptions(false, false, ScanWithDefender: true), CancellationToken.None);

        Assert.Equal(UninstallStatus.Cancelled, outcome.Status);
        Assert.Empty(_runner.Calls);
        Assert.Equal(1, _dialogs.ThreatPrompts);
    }

    [Fact]
    public async Task Threats_and_a_yes_go_on()
    {
        _defender.Result = new DefenderResult(DefenderStatus.ThreatsFound, "Threat!");
        _dialogs.ContinueAfterThreats = true;

        var outcome = await Create().RunAsync(Program(), new UninstallOptions(false, false, ScanWithDefender: true), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task A_clean_or_failed_scan_does_not_ask_anything()
    {
        _defender.Result = new DefenderResult(DefenderStatus.Failed, "service off");

        var outcome = await Create().RunAsync(Program(), new UninstallOptions(false, false, ScanWithDefender: true), CancellationToken.None);

        Assert.Equal(UninstallStatus.Completed, outcome.Status);
        Assert.Equal(0, _dialogs.ThreatPrompts);
        Assert.Equal(1, _defender.Scans);
    }

    [Fact]
    public async Task No_scan_happens_unless_it_was_asked_for_or_the_tool_is_missing()
    {
        await Create().RunAsync(Program(), new UninstallOptions(false, false), CancellationToken.None);
        _defender.IsAvailable = false;
        await Create().RunAsync(Program(), new UninstallOptions(false, false, ScanWithDefender: true), CancellationToken.None);

        Assert.Equal(0, _defender.Scans);
    }

    [Fact]
    public async Task A_made_restore_point_is_journaled()
    {
        await Create().RunAsync(Program(), new UninstallOptions(false, true), CancellationToken.None);

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(BackupKinds.RestorePoint, entry.Kind);
        Assert.Contains("Acme", entry.Target);
    }
}
