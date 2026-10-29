using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigUninstaller.Models;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class MainViewModelTests : VmTestBase
{
    // ---- scan ----

    [Fact]
    public async Task ScanCommand_fills_Programs_and_status()
    {
        _scanner.Result = new ScanResult(new[] { Program("A"), Program("B"), Program("C") }, 10, 4, 2, 1, 0);
        var vm = Create();

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Programs.Count);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains("3 programs", vm.StatusText);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ScanCommand_failure_is_logged_and_shown_not_thrown()
    {
        _scanner.Throw = new InvalidOperationException("boom");
        var vm = Create();

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("failed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("boom"));
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task Status_shows_total_size_of_all_programs()
    {
        var vm = await ScannedAsync(Program("A", sizeKb: 1024), Program("B", sizeKb: 2048), Program("C"));

        Assert.Contains("3 programs", vm.StatusText);
        Assert.Contains("3 MB", vm.StatusText);
    }

    [Fact]
    public async Task Icons_are_assigned_to_rows_after_scan_and_counted_in_the_log()
    {
        var withIcon = Program("HasIcon");
        _icons.Icons[withIcon.Name] = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[1], 1);
        var vm = await ScannedAsync(withIcon, Program("NoIcon"));

        Assert.True(vm.Rows.Single(r => r.Name == "HasIcon").HasIcon);
        Assert.False(vm.Rows.Single(r => r.Name == "NoIcon").HasIcon);
        Assert.Contains(_log.Lines, l => l.Contains("Icons loaded: 1 of 2"));
    }

    // ---- search ----

    [Fact]
    public async Task SearchText_filters_rows_and_status_shows_shown_of_total()
    {
        var vm = await ScannedAsync(
            Program("Java Kit", publisher: "Oracle", sizeKb: 1024),
            Program("Notepad", sizeKb: 1024),
            Program("Java Docs", publisher: "Oracle", sizeKb: 1024));

        vm.SearchText = "java";

        Assert.Equal(new[] { "Java Docs", "Java Kit" }, RowNames(vm));
        Assert.Contains("2 of 3 programs", vm.StatusText);
        Assert.Contains("2 MB", vm.StatusText);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public async Task SearchText_with_no_match_sets_HasNoResults()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));

        vm.SearchText = "zzz";

        Assert.Empty(vm.Rows);
        Assert.True(vm.HasNoResults);
        Assert.Contains("zzz", vm.NoResultsText);
        Assert.Equal("0 of 2 programs", vm.StatusText);
    }

    [Fact]
    public async Task ClearSearchCommand_restores_all_rows()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SearchText = "alp";

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal("", vm.SearchText);
        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public async Task ClearSearchCommand_can_only_run_when_there_is_text()
    {
        var vm = await ScannedAsync(Program("Alpha"));

        Assert.False(vm.ClearSearchCommand.CanExecute(null));
        vm.SearchText = "a";
        Assert.True(vm.ClearSearchCommand.CanExecute(null));
    }

    [Fact]
    public void HasNoResults_is_false_before_any_scan()
    {
        Assert.False(Create().HasNoResults);
    }

    // ---- sort ----

    [Fact]
    public async Task Default_sort_is_name_ascending()
    {
        var vm = await ScannedAsync(Program("beta"), Program("Alpha"));

        Assert.Equal(SortColumn.Name, vm.SortColumn);
        Assert.False(vm.SortDescending);
        Assert.Equal(new[] { "Alpha", "beta" }, RowNames(vm));
    }

    [Fact]
    public async Task SortCommand_on_same_column_toggles_direction()
    {
        var vm = await ScannedAsync(Program("A"), Program("B"));

        vm.SortCommand.Execute("Name");

        Assert.True(vm.SortDescending);
        Assert.Equal(new[] { "B", "A" }, RowNames(vm));
    }

    [Fact]
    public async Task SortCommand_on_new_column_starts_ascending()
    {
        var vm = await ScannedAsync(Program("A", sizeKb: 300), Program("B", sizeKb: 100), Program("C", sizeKb: 200));
        vm.SortCommand.Execute("Name"); // now descending

        vm.SortCommand.Execute("Size");

        Assert.Equal(SortColumn.Size, vm.SortColumn);
        Assert.False(vm.SortDescending);
        Assert.Equal(new[] { "B", "C", "A" }, RowNames(vm));
    }

    [Fact]
    public async Task SortCommand_ignores_unknown_column_names()
    {
        var vm = await ScannedAsync(Program("A"), Program("B"));

        vm.SortCommand.Execute("Nonsense");
        vm.SortCommand.Execute(null);

        Assert.Equal(SortColumn.Name, vm.SortColumn);
        Assert.False(vm.SortDescending);
    }

    // ---- selection and rescan ----

    [Fact]
    public async Task SelectedRow_is_kept_while_visible_and_cleared_when_filtered_out()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Beta");

        vm.SearchText = "b";
        Assert.Equal("Beta", vm.SelectedRow?.Name);

        vm.SearchText = "alp";
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public async Task Rescan_keeps_search_text_and_sort()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SearchText = "a";
        vm.SortCommand.Execute("Name");

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal("a", vm.SearchText);
        Assert.True(vm.SortDescending);
        Assert.Equal(new[] { "Beta", "Alpha" }, RowNames(vm));
    }

    // ---- uninstall ----

    private void RemoveFromNextScan(params string[] names)
    {
        var remaining = _scanner.Result.Programs.Where(p => !names.Contains(p.Name)).ToArray();
        _scanner.Result = new ScanResult(remaining, remaining.Length, 0, 0, 0, 0);
    }

    [Fact]
    public async Task UninstallCommand_needs_a_selected_program()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));

        Assert.False(vm.UninstallCommand.CanExecute(null));

        vm.SelectedRow = vm.Rows[0];

        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Uninstall_runs_the_selected_program_then_rescans_and_reports_removal()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");
        _coordinator.Handler = (_, _) =>
        {
            RemoveFromNextScan("Alpha");
            return Task.FromResult(new UninstallOutcome(UninstallStatus.Completed, "Done.", 0, Started: true));
        };

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Equal("Alpha", _coordinator.Requested.Single().Name);
        Assert.Equal(2, _scanner.Calls);
        Assert.Equal(new[] { "Beta" }, RowNames(vm));
        Assert.Contains("Alpha was removed", vm.StatusText);
        Assert.Empty(_dialogs.Messages);
        Assert.False(vm.IsUninstalling);
    }

    // ---- leftovers after an uninstall ----

    private async Task<MainViewModel> UninstalledAsync(bool restorePointCreated = false)
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Alpha");
        _coordinator.Handler = (_, _) =>
        {
            RemoveFromNextScan("Alpha");
            return Task.FromResult(new UninstallOutcome(UninstallStatus.Completed, "Done.", 0, Started: true, RestorePointCreated: restorePointCreated));
        };
        return vm;
    }

    [Fact]
    public async Task A_removed_program_triggers_the_leftover_scan_with_the_other_programs()
    {
        var vm = await UninstalledAsync(restorePointCreated: true);

        await vm.UninstallCommand.ExecuteAsync(null);

        var call = Assert.Single(_leftovers.Calls);
        Assert.Equal("Alpha", call.Program.Name);
        Assert.Equal(new[] { "Beta" }, call.Others.Select(p => p.Name));
        Assert.True(call.RestoreAlready);
    }

    [Fact]
    public async Task The_leftover_summary_is_added_to_the_status_note()
    {
        var vm = await UninstalledAsync();
        _leftovers.Handler = (_, _, _, _) => Task.FromResult(new LeftoverFlowResult(
            LeftoverFlowStatus.Cleaned, 3, 2, 0, Array.Empty<CleanupFailure>(), "Cleaned 2 leftovers."));

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("Alpha was removed", vm.StatusText);
        Assert.Contains("Cleaned 2 leftovers.", vm.StatusText);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task Leftover_failures_are_listed_in_an_error_message()
    {
        var vm = await UninstalledAsync();
        var item = new LeftoverItem(LeftoverKind.Folder, @"C:\Program Files\Acme", Confidence.High, "same name");
        _leftovers.Handler = (_, _, _, _) => Task.FromResult(new LeftoverFlowResult(
            LeftoverFlowStatus.Cleaned, 1, 0, 1, new[] { new CleanupFailure(item, "The file is in use.") }, "Cleaned 0 leftovers, 1 failed."));

        await vm.UninstallCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains(@"C:\Program Files\Acme", message.Message);
        Assert.Contains("in use", message.Message);
    }

    [Fact]
    public async Task A_program_that_is_still_listed_gets_no_leftover_scan()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(_leftovers.Calls);
    }

    [Fact]
    public async Task A_crash_in_the_leftover_scan_is_logged_and_shown_but_the_removal_still_counts()
    {
        var vm = await UninstalledAsync();
        _leftovers.Handler = (_, _, _, _) => throw new InvalidOperationException("scan exploded");

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("scan exploded"));
        Assert.Contains("scan exploded", Assert.Single(_dialogs.Messages).Message);
        Assert.Contains("Alpha was removed", vm.StatusText);
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task While_looking_for_leftovers_the_overlay_says_so_and_has_no_stop_button()
    {
        var vm = await UninstalledAsync();
        var gate = new TaskCompletionSource<LeftoverFlowResult>();
        _leftovers.Handler = (_, _, _, _) => gate.Task;

        var running = vm.UninstallCommand.ExecuteAsync(null);
        await Task.Delay(300);

        Assert.True(vm.IsUninstalling);
        Assert.Contains("leftovers", vm.BusyText);
        Assert.False(vm.CanStopWaiting);

        gate.SetResult(new LeftoverFlowResult(LeftoverFlowStatus.NoneFound, 0, 0, 0, Array.Empty<CleanupFailure>(), "No leftovers found."));
        await running;
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task Uninstall_that_leaves_the_program_listed_explains_why()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];

        await vm.UninstallCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("still", message.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(message.IsError);
    }

    [Fact]
    public async Task Uninstall_that_could_not_start_shows_the_error_and_skips_the_rescan()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _coordinator.Handler = (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.Failed, "The uninstaller is not a valid program: C:\\a\\u.exe"));

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Equal(1, _scanner.Calls);
        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("not a valid program", message.Message);
        Assert.True(message.IsError);
    }

    [Fact]
    public async Task Cancelled_uninstall_leaves_everything_alone()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _coordinator.Handler = (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.Cancelled, "Cancelled."));

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Equal(1, _scanner.Calls);
        Assert.Empty(_dialogs.Messages);
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task While_uninstalling_other_actions_are_blocked_and_Stop_waiting_cancels()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        var gate = new TaskCompletionSource<UninstallOutcome>();
        CancellationToken seen = default;
        _coordinator.Handler = (_, token) =>
        {
            seen = token;
            return gate.Task;
        };

        var running = vm.UninstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsUninstalling);
        Assert.Contains("Alpha", vm.BusyText);
        Assert.False(vm.UninstallCommand.CanExecute(null));
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.StopWaitingCommand.CanExecute(null));

        vm.StopWaitingCommand.Execute(null);
        Assert.True(seen.IsCancellationRequested);

        gate.SetResult(new UninstallOutcome(UninstallStatus.StoppedWaiting, "Stopped.", null, Started: true));
        await running;

        Assert.False(vm.IsUninstalling);
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.Contains("Stopped waiting", vm.StatusText);
    }

    [Fact]
    public async Task Unexpected_exception_is_logged_and_shown_not_thrown()
    {
        var vm = await ScannedAsync(Program("Alpha"));
        vm.SelectedRow = vm.Rows[0];
        _coordinator.Handler = (_, _) => throw new InvalidOperationException("kaboom");

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("kaboom"));
        Assert.Contains("kaboom", Assert.Single(_dialogs.Messages).Message);
        Assert.False(vm.IsUninstalling);
    }

    // ---- shell ----

    [Fact]
    public void ToggleThemeCommand_switches_theme_and_button_text()
    {
        var vm = Create();
        var before = vm.ThemeButtonText;

        vm.ToggleThemeCommand.Execute(null);

        Assert.True(_theme.IsDark);
        Assert.NotEqual(before, vm.ThemeButtonText);
    }

    [Fact]
    public void ViewLogCommand_opens_log_window()
    {
        Create().ViewLogCommand.Execute(null);

        Assert.Equal(1, _windows.LogShown);
    }
}
