using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class StartupViewModelTests
{
    private readonly FakeStartupManager _manager = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeLog _log = new();

    private StartupViewModel Create() => new(_manager, _dialogs, _log);

    private static StartupItem Item(string name, bool enabled) =>
        new($"RUN|CurrentUser|Run|{name}", name, name + ".exe", "Run (current user)", StartupKind.RunKey, enabled);

    [Fact]
    public async Task Loading_lists_the_items_with_their_state_and_the_right_button_text()
    {
        _manager.Items.Add(Item("Discord", true));
        _manager.Items.Add(Item("Medal", false));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(new[] { "On", "Off" }, vm.Items.Select(i => i.StateText));
        Assert.Equal(new[] { "Turn off", "Turn on" }, vm.Items.Select(i => i.ActionText));
        Assert.Contains("2 startup items, 1 turned on", vm.StatusText);
    }

    [Fact]
    public async Task Toggling_flips_the_state_through_the_manager_and_reloads()
    {
        _manager.Items.Add(Item("Discord", true));
        var vm = Create();
        await vm.LoadAsync();

        await vm.ToggleCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("set:RUN|CurrentUser|Run|Discord:False", Assert.Single(_manager.Calls));
        Assert.Equal(2, _manager.Lists);
    }

    [Fact]
    public async Task A_failed_change_is_shown()
    {
        _manager.Items.Add(Item("Discord", true));
        _manager.Result = new StartupChangeResult(false, "Access is denied.");
        var vm = Create();
        await vm.LoadAsync();

        await vm.ToggleCommand.ExecuteAsync(vm.Items[0]);

        Assert.True(Assert.Single(_dialogs.Messages).IsError);
        Assert.Contains("Access is denied", _dialogs.Messages[0].Message);
    }

    [Fact]
    public async Task Activate_loads_once()
    {
        var vm = Create();

        vm.Activate();
        await Task.Delay(100);
        vm.Activate();
        await Task.Delay(100);

        Assert.Equal(1, _manager.Lists);
    }
}

public class UndoViewModelTests
{
    private sealed class FakeUndo : IUndoService
    {
        public List<UndoItem> Items { get; } = new();

        public List<UndoItem> Restored { get; } = new();

        public UndoResult Result { get; set; } = new(true, "Restored.");

        public IReadOnlyList<UndoItem> List() => Items.ToList();

        public Task<UndoResult> RestoreAsync(UndoItem item, CancellationToken cancellationToken)
        {
            Restored.Add(item);
            return Task.FromResult(Result);
        }
    }

    private readonly FakeUndo _undo = new();
    private readonly FakeDialogs _dialogs = new();

    private static UndoItem Item(string kind, bool restored = false) =>
        new(new BackupEntry(new DateTime(2026, 10, 30, 9, 5, 0), kind, "Acme", @"C:\x", null), "Recycle Bin item", @"C:\x", restored);

    [Fact]
    public void Refresh_lists_the_journal_and_flags_an_empty_one()
    {
        var vm = new UndoViewModel(_undo, _dialogs);
        vm.Refresh();
        Assert.True(vm.IsEmpty);

        _undo.Items.Add(Item(BackupKinds.RecycledItem));
        vm.Refresh();

        Assert.False(vm.IsEmpty);
        Assert.Equal("2026-10-30 09:05", vm.Items[0].DateText);
    }

    [Fact]
    public async Task Restore_asks_first_then_restores_and_reports()
    {
        _undo.Items.Add(Item(BackupKinds.RecycledItem));
        var vm = new UndoViewModel(_undo, _dialogs);
        vm.Refresh();

        _dialogs.ConfirmAnswer = false;
        await vm.RestoreCommand.ExecuteAsync(vm.Items[0]);
        Assert.Empty(_undo.Restored);

        _dialogs.ConfirmAnswer = true;
        await vm.RestoreCommand.ExecuteAsync(vm.Items[0]);

        Assert.Single(_undo.Restored);
        Assert.Equal("Restored", Assert.Single(_dialogs.Messages).Heading);
    }

    [Fact]
    public async Task A_failed_restore_is_an_error_message()
    {
        _undo.Items.Add(Item(BackupKinds.RecycledItem));
        _undo.Result = new UndoResult(false, "No longer in the Recycle Bin.");
        var vm = new UndoViewModel(_undo, _dialogs);
        vm.Refresh();

        await vm.RestoreCommand.ExecuteAsync(vm.Items[0]);

        Assert.True(Assert.Single(_dialogs.Messages).IsError);
    }

    [Fact]
    public void An_already_restored_item_cannot_be_restored_again()
    {
        _undo.Items.Add(Item(BackupKinds.RecycledItem, restored: true));
        var vm = new UndoViewModel(_undo, _dialogs);
        vm.Refresh();

        Assert.False(vm.RestoreCommand.CanExecute(vm.Items[0]));
        Assert.Equal("Restored", vm.Items[0].StateText);
    }
}

public class StoreAppsViewModelTests
{
    private sealed class FakeStoreService : IStoreAppService
    {
        public List<StoreApp> Apps { get; } = new();

        public List<string> Removed { get; } = new();

        public StoreRemoveResult Result { get; set; } = new(true, "Removed.");

        public Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoreApp>>(Apps.ToList());

        public Task<StoreRemoveResult> RemoveAsync(StoreApp app, CancellationToken cancellationToken)
        {
            Removed.Add(app.Name);
            return Task.FromResult(Result);
        }
    }

    private readonly FakeStoreService _service = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeDialogs _dialogs = new();

    private StoreAppsViewModel Create() => new(_service, _restore, _dialogs, new FakeLog());

    private static StoreApp App(string name, bool framework = false) =>
        new(name, name + "_1_x64__abc", "CN=Contoso", "1.0", framework, false, "Store");

    [Fact]
    public async Task System_packages_are_hidden_until_asked_for_and_the_search_filters_the_rest()
    {
        _service.Apps.AddRange(new[] { App("Contoso.Notes"), App("Contoso.Mail"), App("Microsoft.VCLibs.140", framework: true) });
        var vm = Create();
        await vm.LoadAsync();

        Assert.Equal(2, vm.Items.Count);

        vm.ShowSystem = true;
        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.Items.Single(i => i.Name.StartsWith("Microsoft")).CanRemove);

        vm.SearchText = "mail";
        Assert.Equal("Contoso.Mail", Assert.Single(vm.Items).Name);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task A_protected_package_has_no_working_remove_button()
    {
        _service.Apps.Add(App("Microsoft.VCLibs.140", framework: true));
        var vm = Create();
        vm.ShowSystem = true;
        await vm.LoadAsync();

        Assert.False(vm.RemoveCommand.CanExecute(vm.Items[0]));
    }

    [Fact]
    public async Task Removing_asks_makes_a_restore_point_removes_and_reloads()
    {
        _service.Apps.Add(App("Contoso.Notes"));
        var vm = Create();
        await vm.LoadAsync();

        _dialogs.ConfirmAnswer = false;
        await vm.RemoveCommand.ExecuteAsync(vm.Items[0]);
        Assert.Empty(_service.Removed);
        Assert.Equal(0, _restore.Calls);

        _dialogs.ConfirmAnswer = true;
        await vm.RemoveCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(new[] { "Contoso.Notes" }, _service.Removed);
        Assert.Equal(1, _restore.Calls);
        Assert.Equal("Removed", Assert.Single(_dialogs.Messages).Heading);
    }

    [Fact]
    public async Task No_restore_point_and_a_no_means_nothing_is_removed()
    {
        _service.Apps.Add(App("Contoso.Notes"));
        _restore.Result = new RestorePointResult(false, "off");
        _dialogs.ContinueWithoutRestore = false;
        var vm = Create();
        await vm.LoadAsync();

        await vm.RemoveCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(_service.Removed);
    }
}

public class DiskUsageViewModelTests
{
    private sealed class FakeSizer : IFolderSizer
    {
        public long? GetSize(string folder, CancellationToken cancellationToken) => null;
    }

    private static InstalledProgram P(string name, long kb) => new()
    {
        Name = name,
        EstimatedSizeKb = kb,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    [Fact]
    public async Task The_chart_rows_are_sorted_and_scaled_to_the_biggest()
    {
        var vm = new DiskUsageViewModel(new FakeSizer(), new SafetyGuard(VmTestBase.TestRoots), new FakeFiles(), new FakeLog());
        vm.SetPrograms(new[] { P("Small", 1024), P("Big", 4096) });

        await vm.ComputeAsync();

        Assert.Equal(new[] { "Big", "Small" }, vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.Equal(4096L * 1024, r.Maximum));
        Assert.Equal("4 MB", vm.Rows[0].SizeText);
        Assert.Contains("Top 2 of 2", vm.StatusText);
    }

    [Fact]
    public async Task Activate_measures_only_when_the_programs_changed()
    {
        var vm = new DiskUsageViewModel(new FakeSizer(), new SafetyGuard(VmTestBase.TestRoots), new FakeFiles(), new FakeLog());
        vm.SetPrograms(new[] { P("A", 100) });

        vm.Activate();
        // Wait until the refresh has fully ended: a second Activate while it still runs would be ignored.
        await WaitAsync(() => vm.Rows.Count == 1 && !vm.IsMeasuring && vm.RefreshCommand.CanExecute(null));
        vm.SetPrograms(new[] { P("A", 100), P("B", 200) });
        vm.Activate();
        await WaitAsync(() => vm.Rows.Count == 2);

        Assert.Equal("B", vm.Rows[0].Name);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "timed out waiting");
    }
}

public class WindowProbeTests
{
    [Fact]
    public void Listing_open_windows_returns_titled_windows_with_real_exe_paths_and_no_duplicates()
    {
        // Read-only: asks Windows which windows are open; the content depends on the machine.
        var windows = new WindowProbe().ListOpenWindows();

        Assert.All(windows, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Title));
            Assert.True(Path.IsPathRooted(w.ExePath));
        });
        Assert.Equal(windows.Count, windows.Select(w => w.ExePath.ToLowerInvariant()).Distinct().Count());
    }
}
