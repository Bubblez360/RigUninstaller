using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>The features on top of the basic list: tags, insights, batch, force remove, tools, winget, target, tracker, tabs.</summary>
public class MainViewModelFeatureTests : VmTestBase
{
    private void RemoveFromNextScan(params string[] names)
    {
        var remaining = _scanner.Result.Programs.Where(p => !names.Contains(p.Name)).ToArray();
        _scanner.Result = new ScanResult(remaining, remaining.Length, 0, 0, 0, 0);
    }

    // ---- tags and insights ----

    [Fact]
    public async Task Rows_get_their_Keep_and_Bloat_flags_and_the_bloat_list_is_reread_each_scan()
    {
        var keptProgram = Program("Kept One");
        _keep.SetKept(keptProgram, true);
        _bloat.Names.Add("junk");

        var vm = await ScannedAsync(keptProgram, Program("Junk Tool"), Program("Plain"));

        Assert.True(vm.Rows.Single(r => r.Name == "Kept One").IsKept);
        Assert.True(vm.Rows.Single(r => r.Name == "Junk Tool").IsBloat);
        Assert.False(vm.Rows.Single(r => r.Name == "Plain").IsBloat);
        Assert.Equal(1, _bloat.Reloads);
    }

    [Fact]
    public async Task Insights_fill_in_after_the_scan_and_one_failure_does_not_hide_the_others()
    {
        _insights.Handler = p => p.Name == "Bad"
            ? throw new InvalidOperationException("odd path")
            : new ProgramInsight(
                new SignatureInfo(SignatureState.Unsigned, null, "none"),
                new LastUsedInfo(new DateTime(2026, 2, 3), "Windows Prefetch data"),
                p.Name == "Broken" ? "The uninstaller file is missing." : null);

        var vm = await ScannedAsync(Program("Bad"), Program("Broken"), Program("Fine"));

        Assert.Null(vm.Rows.Single(r => r.Name == "Bad").Insight);
        Assert.True(vm.Rows.Single(r => r.Name == "Broken").IsOrphan);
        Assert.Contains("Broken entry", vm.Rows.Single(r => r.Name == "Broken").Tags);
        Assert.True(vm.Rows.Single(r => r.Name == "Fine").IsUnsigned);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("odd path"));
    }

    [Fact]
    public async Task Sorting_by_last_used_orders_by_date_with_unknown_dates_last()
    {
        _insights.Handler = p => new ProgramInsight(
            new SignatureInfo(SignatureState.Signed, "x", "ok"),
            new LastUsedInfo(p.Name switch { "Old" => new DateTime(2020, 1, 1), "New" => new DateTime(2026, 1, 1), _ => null }, "Windows Prefetch data"),
            null);
        var vm = await ScannedAsync(Program("New"), Program("Never"), Program("Old"));

        vm.SortCommand.Execute("LastUsed");

        Assert.Equal(new[] { "Old", "New", "Never" }, RowNames(vm));
        vm.SortCommand.Execute("LastUsed");
        Assert.Equal(new[] { "New", "Old", "Never" }, RowNames(vm));
    }

    // ---- selection-aware texts ----

    [Fact]
    public async Task The_main_button_and_summary_name_the_selected_program()
    {
        var vm = await ScannedAsync(
            Program("Alpha", publisher: "Acme Corp", version: "2.0", location: @"C:\Program Files\Alpha"),
            Program("A Program With A Really Long Name That Overflows"));

        Assert.Equal("Uninstall", vm.UninstallButtonText);
        Assert.Equal("Nothing selected", vm.SelectionSummary);
        Assert.Contains("Select a program", vm.SelectionDetail);

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");
        Assert.Equal("Uninstall Alpha", vm.UninstallButtonText);
        Assert.Equal("Alpha", vm.SelectionSummary);
        Assert.Equal(@"2.0  ·  Acme Corp  ·  C:\Program Files\Alpha", vm.SelectionDetail);

        vm.SelectedRow = vm.Rows.Single(r => r.Name.StartsWith("A Program"));
        Assert.EndsWith("...", vm.UninstallButtonText);
        Assert.True(vm.UninstallButtonText.Length <= "Uninstall ".Length + 25);
    }

    [Fact]
    public async Task The_selection_texts_update_when_the_selection_changes()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedRow = vm.Rows[0];

        Assert.Contains(nameof(MainViewModel.UninstallButtonText), changed);
        Assert.Contains(nameof(MainViewModel.SelectionSummary), changed);
        Assert.Contains(nameof(MainViewModel.SelectionDetail), changed);
    }

    [Fact]
    public async Task The_winget_column_has_data_only_after_the_check()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        Assert.False(vm.HasWingetData);

        await vm.WingetCheckCommand.ExecuteAsync(null);

        Assert.True(vm.HasWingetData);
    }

    // ---- keep ----

    [Fact]
    public async Task Keep_protects_a_program_from_uninstall_and_force_remove_and_from_ticking()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        vm.Rows[0].IsChecked = true;
        Assert.True(vm.UninstallCommand.CanExecute(null));
        Assert.True(vm.ForceRemoveCommand.CanExecute(null));

        vm.ToggleKeepCommand.Execute(null);

        Assert.True(vm.Rows[0].IsKept);
        Assert.False(vm.Rows[0].IsChecked);
        Assert.False(vm.Rows[0].CanTick);
        Assert.False(vm.UninstallCommand.CanExecute(null));
        Assert.False(vm.ForceRemoveCommand.CanExecute(null));
        Assert.True(_keep.IsKept(vm.Rows[0].Program));
        Assert.Contains("protected", vm.StatusText);

        vm.ToggleKeepCommand.Execute(null);

        Assert.False(_keep.IsKept(vm.Rows[0].Program));
        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    // ---- batch ----

    private async Task<MainViewModel> TickedAsync(params string[] names)
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"), Program("Gamma"));
        foreach (var name in names)
        {
            vm.Rows.Single(r => r.Name == name).IsChecked = true;
        }

        return vm;
    }

    [Fact]
    public async Task Ticking_rows_counts_them_and_labels_the_batch_button()
    {
        var vm = await TickedAsync("Alpha", "Gamma");

        Assert.Equal(2, vm.CheckedCount);
        Assert.True(vm.HasChecked);
        Assert.Equal("Uninstall 2 ticked", vm.BatchButtonText);
        Assert.True(vm.BatchUninstallCommand.CanExecute(null));

        vm.ClearChecksCommand.Execute(null);

        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.BatchUninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Batch_uninstall_runs_the_ticked_programs_then_rescans_and_looks_for_leftovers()
    {
        var vm = await TickedAsync("Alpha", "Beta");
        _batch.Handler = (programs, _, _) =>
        {
            RemoveFromNextScan("Alpha", "Beta");
            IReadOnlyList<BatchItemResult> results = programs.Select(p => new BatchItemResult(p, BatchItemStatus.Done, "ok", null)).ToList();
            return Task.FromResult(results);
        };

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Alpha", "Beta" }, _dialogs.LastBatchPrograms!.Select(p => p.Name));
        Assert.Equal(new[] { "Alpha", "Beta" }, _batch.Calls.Single().Select(p => p.Name));
        Assert.Equal(new[] { "Alpha", "Beta" }, _leftovers.Calls.Select(c => c.Program.Name));
        Assert.All(_leftovers.Calls, c => Assert.True(c.RestoreAlready));
        Assert.Equal(new[] { "Gamma" }, RowNames(vm));
        Assert.Contains("Batch: 2 removed, 0 failed", vm.StatusText);
        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.IsUninstalling);
        Assert.False(vm.ShowBatchItems);
    }

    [Fact]
    public async Task Declining_the_batch_confirmation_changes_nothing()
    {
        var vm = await TickedAsync("Alpha");
        _dialogs.BatchResponse = null;

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Empty(_batch.Calls);
        Assert.Equal(1, vm.CheckedCount);
    }

    [Fact]
    public async Task Programs_marked_Keep_are_dropped_from_the_batch()
    {
        var vm = await TickedAsync("Alpha", "Beta");
        _keep.SetKept(vm.Rows.Single(r => r.Name == "Beta").Program, true);
        vm.Rows.Single(r => r.Name == "Beta").IsKept = true;

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Alpha" }, _dialogs.LastBatchPrograms!.Select(p => p.Name));
    }

    [Fact]
    public async Task A_batch_of_only_kept_programs_explains_and_does_not_ask()
    {
        var vm = await TickedAsync("Alpha");
        vm.Rows.Single(r => r.Name == "Alpha").IsKept = true;

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Messages);
        Assert.Null(_dialogs.LastBatchPrograms);
        Assert.Empty(_batch.Calls);
    }

    [Fact]
    public async Task The_progress_list_shows_each_programs_state_while_the_batch_runs()
    {
        var vm = await TickedAsync("Alpha", "Beta");
        var gate = new TaskCompletionSource<IReadOnlyList<BatchItemResult>>();
        _batch.Handler = (programs, _, progress) =>
        {
            progress.Report(new BatchProgress(0, 2, programs[0], BatchItemStatus.Done, "ok"));
            progress.Report(new BatchProgress(1, 2, programs[1], BatchItemStatus.Running, "..."));
            return gate.Task;
        };

        var run = vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsUninstalling);
        Assert.True(vm.ShowBatchItems);
        Assert.Equal(new[] { "Done", "Running" }, vm.BatchItems.Select(i => i.StatusText));
        Assert.Contains("2 of 2", vm.BusyText);
        Assert.True(vm.CanStopWaiting);

        gate.SetResult(Array.Empty<BatchItemResult>());
        await run;
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task Failed_batch_items_are_listed_in_an_error_message()
    {
        var vm = await TickedAsync("Alpha", "Beta");
        _batch.Handler = (programs, _, _) =>
        {
            IReadOnlyList<BatchItemResult> results = new[]
            {
                new BatchItemResult(programs[0], BatchItemStatus.Failed, "file missing", null),
                new BatchItemResult(programs[1], BatchItemStatus.Skipped, "Marked Keep.", null),
            };
            return Task.FromResult(results);
        };

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains("Alpha", message.Message);
        Assert.Contains("file missing", message.Message);
        Assert.Contains("1 failed", vm.StatusText);
    }

    // ---- force remove ----

    [Fact]
    public async Task Force_remove_rescans_and_looks_for_leftovers_when_the_entry_is_gone()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");
        _forceRemove.Handler = _ =>
        {
            RemoveFromNextScan("Alpha");
            return Task.FromResult(new ForceRemoveOutcome(ForceRemoveStatus.Done, "done", null, RestorePointCreated: true));
        };

        await vm.ForceRemoveCommand.ExecuteAsync(null);

        Assert.Equal("Alpha", _forceRemove.Calls.Single().Name);
        Assert.Equal(new[] { "Beta" }, RowNames(vm));
        var call = Assert.Single(_leftovers.Calls);
        Assert.True(call.RestoreAlready);
        Assert.Contains("Alpha was force removed", vm.StatusText);
        Assert.False(vm.IsUninstalling);
    }

    [Theory]
    [InlineData(ForceRemoveStatus.Refused)]
    [InlineData(ForceRemoveStatus.Failed)]
    public async Task A_refused_or_failed_force_remove_shows_the_reason(ForceRemoveStatus status)
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _forceRemove.Handler = _ => Task.FromResult(new ForceRemoveOutcome(status, "because reasons"));

        await vm.ForceRemoveCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("because reasons", message.Message);
        Assert.Empty(_leftovers.Calls);
    }

    [Fact]
    public async Task A_cancelled_force_remove_only_leaves_a_note()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _forceRemove.Handler = _ => Task.FromResult(new ForceRemoveOutcome(ForceRemoveStatus.Cancelled, "x"));

        await vm.ForceRemoveCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Messages);
        Assert.Contains("cancelled", vm.StatusText);
        Assert.Equal(1, _scanner.Calls);
    }

    [Fact]
    public async Task A_crash_in_force_remove_becomes_a_message()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _forceRemove.Handler = _ => throw new InvalidOperationException("kaput");

        await vm.ForceRemoveCommand.ExecuteAsync(null);

        Assert.Contains("kaput", Assert.Single(_dialogs.Messages).Message);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("kaput"));
        Assert.False(vm.IsUninstalling);
    }

    // ---- CSV ----

    [Fact]
    public async Task Export_csv_writes_the_shown_programs_to_the_chosen_file()
    {
        var vm = await ScannedAsync(Program("Alpha", "Acme"), Program("Beta"));
        var file = Path.Combine(Path.GetTempPath(), "zap-export-" + Guid.NewGuid().ToString("N") + ".csv");
        _dialogs.SavePath = file;
        vm.SearchText = "alp";
        try
        {
            await vm.ExportCsvCommand.ExecuteAsync(null);

            var lines = File.ReadAllLines(file);
            Assert.StartsWith("Name,Publisher", lines[0].TrimStart('\uFEFF'));
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Alpha,Acme", lines[1]);
            Assert.Contains("Exported 1 programs", vm.StatusText);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing_and_a_bad_path_is_reported()
    {
        var vm = await ScannedAsync(Program("Alpha"));

        _dialogs.SavePath = null;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Empty(_dialogs.Messages);

        _dialogs.SavePath = Path.Combine(Path.GetTempPath(), "zap-no-such-folder-" + Guid.NewGuid().ToString("N"), "x.csv");
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(_dialogs.Messages).IsError);
    }

    // ---- winget ----

    [Fact]
    public async Task Winget_check_labels_programs_that_winget_knows_and_enables_its_menu_items()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        _winget.Packages.Add(new WingetPackage("Alpha", "Acme.Alpha", "1.0", "2.0", "winget"));
        _winget.Packages.Add(new WingetPackage("Beta", "Acme.Beta", "3.0", null, "winget"));

        await vm.WingetCheckCommand.ExecuteAsync(null);

        Assert.Equal("Update 1.0 to 2.0", vm.Rows.Single(r => r.Name == "Alpha").WingetText);
        Assert.Equal("winget", vm.Rows.Single(r => r.Name == "Beta").WingetText);
        Assert.Contains("2 of your programs, 1 with an update", vm.StatusText);

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");
        Assert.True(vm.WingetUninstallCommand.CanExecute(null));
        Assert.True(vm.WingetUpdateCommand.CanExecute(null));

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Beta");
        Assert.True(vm.WingetUninstallCommand.CanExecute(null));
        Assert.False(vm.WingetUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Winget_check_without_winget_installed_explains_how_to_get_it()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        _winget.IsAvailable = false;

        await vm.WingetCheckCommand.ExecuteAsync(null);

        Assert.Contains("winget", Assert.Single(_dialogs.Messages).Heading);
    }

    [Fact]
    public async Task Winget_uninstall_asks_first_then_runs_rescans_and_looks_for_leftovers()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        _winget.Packages.Add(new WingetPackage("Alpha", "Acme.Alpha", "1.0", null, "winget"));
        await vm.WingetCheckCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");

        _dialogs.ConfirmAnswer = false;
        await vm.WingetUninstallCommand.ExecuteAsync(null);
        Assert.Empty(_winget.Calls);

        _dialogs.ConfirmAnswer = true;
        RemoveFromNextScan("Alpha");
        await vm.WingetUninstallCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "uninstall:Acme.Alpha" }, _winget.Calls);
        Assert.Single(_leftovers.Calls);
        Assert.Contains("removed with winget", vm.StatusText);
    }

    [Fact]
    public async Task Winget_update_runs_upgrade_for_the_selected_program()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        _winget.Packages.Add(new WingetPackage("Alpha", "Acme.Alpha", "1.0", "2.0", "winget"));
        await vm.WingetCheckCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows[0];

        await vm.WingetUpdateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "upgrade:Acme.Alpha" }, _winget.Calls);
        Assert.Empty(_leftovers.Calls);
    }

    [Fact]
    public async Task A_winget_program_marked_Keep_cannot_be_uninstalled_through_winget()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        _winget.Packages.Add(new WingetPackage("Alpha", "Acme.Alpha", "1.0", null, "winget"));
        await vm.WingetCheckCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows[0];
        vm.ToggleKeepCommand.Execute(null);

        Assert.False(vm.WingetUninstallCommand.CanExecute(null));
    }

    // ---- target mode ----

    [Fact]
    public async Task Target_mode_selects_the_program_that_owns_the_window_and_clears_a_hiding_search()
    {
        var vm = await ScannedAsync(Program("Alpha", location: @"C:\Program Files\Alpha"), Program("Beta", location: @"C:\Program Files\Beta"));
        vm.SearchText = "beta";
        _windowProbe.Path = @"C:\Program Files\Alpha\alpha.exe";
        ProgramRowViewModel? revealed = null;
        vm.RowRevealRequested += row => revealed = row;

        vm.LocateWindowUnderCursor();

        Assert.Equal("Alpha", vm.SelectedRow?.Name);
        Assert.Equal("Alpha", revealed?.Name);
        Assert.Equal("", vm.SearchText);
        Assert.Contains("alpha.exe belongs to Alpha", vm.StatusText);
    }

    [Fact]
    public async Task Target_mode_says_so_when_the_window_cannot_be_identified_or_matches_nothing()
    {
        var vm = await ScannedAsync(Program("Alpha", location: @"C:\Program Files\Alpha"));

        _windowProbe.Path = null;
        vm.LocateWindowUnderCursor();
        Assert.Contains("not a program I can identify", vm.StatusText);

        _windowProbe.Path = @"D:\Elsewhere\thing.exe";
        vm.LocateWindowUnderCursor();
        Assert.Contains("does not belong to an installed program", vm.StatusText);
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public async Task Open_windows_list_puts_matched_programs_first_and_picking_one_selects_it()
    {
        var vm = await ScannedAsync(Program("Alpha", location: @"C:\Program Files\Alpha"), Program("Beta", location: @"C:\Program Files\Beta"));
        _windowProbe.Windows.Add(new OpenWindow("Some folder", @"C:\Windows\explorer.exe"));
        _windowProbe.Windows.Add(new OpenWindow("Beta - notes", @"C:\Program Files\Beta\beta.exe"));

        var choices = vm.GetOpenWindowChoices();

        Assert.Equal(new string?[] { "Beta", null }, choices.Select(c => c.ProgramName).ToArray());

        vm.SelectProgramOfWindow(choices[0]);

        Assert.Equal("Beta", vm.SelectedRow?.Name);
        Assert.Contains("beta.exe belongs to Beta", vm.StatusText);
    }

    [Fact]
    public async Task Open_windows_list_is_empty_when_no_window_is_open()
    {
        var vm = await ScannedAsync(Program("Alpha", location: @"C:\Program Files\Alpha"));

        Assert.Empty(vm.GetOpenWindowChoices());
    }

    // ---- right-click actions ----

    [Fact]
    public async Task Open_folder_registry_and_search_use_the_selected_program()
    {
        var vm = await ScannedAsync(Program("Alpha", location: @"C:\Program Files\Alpha"));
        _files.Existing.Add(@"C:\Program Files\Alpha");
        vm.SelectedRow = vm.Rows[0];

        vm.OpenFolderCommand.Execute(null);
        vm.OpenRegistryCommand.Execute(null);
        vm.SearchOnlineCommand.Execute(null);

        Assert.Equal(@"folder:C:\Program Files\Alpha", _external.Calls[0]);
        Assert.Equal(@"regedit:HKLM\SOFTWARE\x\Alpha", _external.Calls[1]);
        Assert.StartsWith("web:https://www.google.com/search?q=Alpha", _external.Calls[2]);
    }

    [Fact]
    public async Task Open_folder_says_so_when_no_folder_is_known()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];

        vm.OpenFolderCommand.Execute(null);

        Assert.Empty(_external.Calls);
        Assert.Contains("install folder", Assert.Single(_dialogs.Messages).Heading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tick_bloatware_ticks_only_shown_bloat_rows_that_are_not_kept()
    {
        _bloat.Names.Add("junk");
        var vm = await ScannedAsync(Program("Junk One"), Program("Junk Two"), Program("Plain"));
        vm.Rows.Single(r => r.Name == "Junk Two").IsKept = true;

        vm.TickBloatCommand.Execute(null);

        Assert.Equal(new[] { "Junk One" }, vm.Rows.Where(r => r.IsChecked).Select(r => r.Name));
    }

    // ---- Defender ----

    [Fact]
    public async Task Defender_scan_reports_clean_and_threats_differently()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];

        await vm.DefenderScanCommand.ExecuteAsync(null);
        _defender.Result = new DefenderResult(DefenderStatus.ThreatsFound, "2 threats");
        await vm.DefenderScanCommand.ExecuteAsync(null);

        Assert.Equal(2, _defender.Scans);
        Assert.False(_dialogs.Messages[0].IsError);
        Assert.Equal("No threats found", _dialogs.Messages[0].Heading);
        Assert.True(_dialogs.Messages[1].IsError);
        Assert.Equal("Threats found", _dialogs.Messages[1].Heading);
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task Defender_scan_without_the_tool_explains_instead_of_failing()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _defender.IsAvailable = false;

        await vm.DefenderScanCommand.ExecuteAsync(null);

        Assert.Equal(0, _defender.Scans);
        Assert.Contains("not found", Assert.Single(_dialogs.Messages).Heading);
    }

    // ---- install tracker ----

    [Fact]
    public async Task Track_install_starts_then_finishes_with_a_result_message()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        _tracker.FinishResult = new TrackResult(new[] { Program("New") }, 12, "Saved a trace of 12 items for New.");

        await vm.TrackInstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsTracking);
        Assert.Equal("Finish tracking", vm.TrackButtonText);
        Assert.Equal("Tracking is on", _dialogs.Messages.Single().Heading);
        Assert.True(vm.CancelTrackingCommand.CanExecute(null));

        await vm.TrackInstallCommand.ExecuteAsync(null);

        Assert.False(vm.IsTracking);
        Assert.Equal("Track new install", vm.TrackButtonText);
        Assert.Equal("Install tracked", _dialogs.Messages.Last().Heading);
        Assert.Contains("12 items", _dialogs.Messages.Last().Message);
        Assert.Equal(2, _scanner.Calls);
    }

    [Fact]
    public async Task Cancelling_tracking_turns_it_off()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        await vm.TrackInstallCommand.ExecuteAsync(null);

        vm.CancelTrackingCommand.Execute(null);

        Assert.False(vm.IsTracking);
        Assert.False(_tracker.IsTracking);
    }

    // ---- tabs ----

    [Fact]
    public async Task Opening_a_tab_loads_it_and_the_search_box_is_only_for_the_programs_tab()
    {
        var manager = new FakeStartupManager();
        manager.Items.Add(new StartupItem("RUN|CurrentUser|Run|Discord", "Discord", "d.exe", "Run (current user)", StartupKind.RunKey, true));
        var startup = new StartupViewModel(manager, _dialogs, _log);
        _scanner.Result = new ScanResult(new[] { Program("Alpha") }, 1, 0, 0, 0, 0);
        var vm = Create(startup: startup);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.IsProgramsTab);

        vm.SelectedTabIndex = 3;

        Assert.False(vm.IsProgramsTab);
        await WaitUntilAsync(() => manager.Lists == 1);
        await WaitUntilAsync(() => startup.Items.Count == 1);
        Assert.Equal("Discord", startup.Items[0].Name);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "timed out waiting");
    }
}
