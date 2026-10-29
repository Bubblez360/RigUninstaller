using System.Text.Json;
using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class StartupApprovedTests
{
    [Fact]
    public void Missing_or_even_first_byte_means_enabled_and_odd_means_disabled()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled(Array.Empty<byte>()));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 2, 0, 0, 0 }));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 6, 0, 0, 0 }));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 3, 0, 0, 0 }));
    }

    [Fact]
    public void Created_values_are_twelve_bytes_and_a_disabled_one_carries_the_time()
    {
        var enabled = StartupApproved.Create(true, new DateTime(2026, 5, 1));
        var disabled = StartupApproved.Create(false, new DateTime(2026, 5, 1));

        Assert.Equal(12, enabled.Length);
        Assert.Equal(2, enabled[0]);
        Assert.Equal(3, disabled[0]);
        Assert.NotEqual(0L, BitConverter.ToInt64(disabled, 4));
        Assert.Equal(0L, BitConverter.ToInt64(enabled, 4));
    }
}

public class StartupManagerTests
{
    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public Dictionary<(StartupHive, bool), List<(string, string)>> Runs { get; } = new();

        public Dictionary<(StartupHive, string, string), byte[]> Approved { get; } = new();

        public List<string> Folder { get; } = new();

        public Exception? ThrowOnSet { get; set; }

        public IReadOnlyList<(string Name, string Command)> GetRunValues(StartupHive hive, bool wow64) =>
            Runs.TryGetValue((hive, wow64), out var list) ? list : new List<(string, string)>();

        public byte[]? GetApproved(StartupHive hive, string approvedKey, string name) =>
            Approved.TryGetValue((hive, approvedKey, name), out var data) ? data : null;

        public void SetApproved(StartupHive hive, string approvedKey, string name, byte[] data)
        {
            if (ThrowOnSet is not null) throw ThrowOnSet;
            Approved[(hive, approvedKey, name)] = data;
        }

        public IReadOnlyList<string> GetStartupFolderFiles(bool allUsers) => allUsers ? new List<string>() : Folder;

        public void SetRunValue(StartupHive hive, string name, string command)
        {
            if (ThrowOnSet is not null) throw ThrowOnSet;
            if (!Runs.TryGetValue((hive, false), out var list))
            {
                Runs[(hive, false)] = list = new List<(string, string)>();
            }

            list.RemoveAll(v => v.Item1.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Add((name, command));
        }
    }

    private readonly FakeStartupRegistry _registry = new();
    private readonly FakeShell _shell = new();
    private readonly MemoryJournal _journal = new();
    private readonly FakeLog _log = new();

    private StartupManager Create() => new(_registry, _shell, _journal, _log);

    private const string NoTasks = "[]";

    public StartupManagerTests()
    {
        _shell.Handler = (_, _) => new ScriptResult(0, NoTasks);
    }

    [Fact]
    public async Task Lists_run_keys_startup_folder_files_and_logon_tasks()
    {
        _registry.Runs[(StartupHive.CurrentUser, false)] = new() { ("Discord", @"C:\Discord\Update.exe --start") };
        _registry.Runs[(StartupHive.LocalMachine, false)] = new() { ("Realtek", @"C:\Realtek\rtk.exe") };
        _registry.Runs[(StartupHive.LocalMachine, true)] = new() { ("OldTool", @"C:\Old\tool.exe") };
        _registry.Folder.Add(@"C:\Users\Tester\Startup\Notes.lnk");
        _shell.Handler = (_, _) => new ScriptResult(0, JsonSerializer.Serialize(new[]
        {
            new { Path = @"\MyTask", Name = "MyTask", Enabled = false, Command = @"C:\my.exe -x" },
        }));

        var items = await Create().ListAsync(CancellationToken.None);

        Assert.Equal(5, items.Count);
        Assert.Contains(items, i => i.Name == "Discord" && i.Kind == StartupKind.RunKey && i.Location == "Run (current user)");
        Assert.Contains(items, i => i.Name == "OldTool" && i.Location.Contains("32-bit"));
        Assert.Contains(items, i => i.Name == "Notes" && i.Kind == StartupKind.StartupFolder);
        var task = Assert.Single(items, i => i.Kind == StartupKind.ScheduledTask);
        Assert.False(task.IsEnabled);
        Assert.Equal("TASK|\\MyTask", task.Id);
    }

    [Fact]
    public async Task An_item_is_off_when_Windows_has_it_marked_disabled()
    {
        _registry.Runs[(StartupHive.CurrentUser, false)] = new() { ("Discord", "d.exe"), ("Other", "o.exe") };
        _registry.Approved[(StartupHive.CurrentUser, "Run", "Discord")] = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        var items = await Create().ListAsync(CancellationToken.None);

        Assert.False(items.Single(i => i.Name == "Discord").IsEnabled);
        Assert.True(items.Single(i => i.Name == "Other").IsEnabled);
    }

    [Fact]
    public async Task Turning_a_run_item_off_writes_the_disabled_marker_and_journals_the_old_state()
    {
        _registry.Runs[(StartupHive.CurrentUser, false)] = new() { ("Discord", "d.exe") };
        var item = (await Create().ListAsync(CancellationToken.None)).Single();

        var result = await Create().SetEnabledAsync(item, false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, _registry.Approved[(StartupHive.CurrentUser, "Run", "Discord")][0]);
        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(BackupKinds.StartupChange, entry.Kind);
        Assert.Equal("enabled", entry.Detail);
        Assert.Equal(item.Id, entry.Target);
        Assert.Contains(_log.Lines, l => l.Contains("disabled") && l.Contains("not deleted"));
    }

    [Fact]
    public async Task Startup_folder_items_use_the_StartupFolder_marker_with_the_file_name()
    {
        _registry.Folder.Add(@"C:\Users\Tester\Startup\Notes.lnk");
        var item = (await Create().ListAsync(CancellationToken.None)).Single();

        await Create().SetEnabledAsync(item, false, CancellationToken.None);

        Assert.True(_registry.Approved.ContainsKey((StartupHive.CurrentUser, "StartupFolder", "Notes.lnk")));
    }

    [Fact]
    public async Task A_registry_error_is_reported_and_nothing_is_journaled()
    {
        _registry.Runs[(StartupHive.LocalMachine, false)] = new() { ("Realtek", "r.exe") };
        var item = (await Create().ListAsync(CancellationToken.None)).Single();
        _registry.ThrowOnSet = new UnauthorizedAccessException("denied");

        var result = await Create().SetEnabledAsync(item, false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("denied", result.Message);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task A_task_is_turned_off_through_PowerShell_with_the_path_in_the_environment()
    {
        var task = new StartupItem(@"TASK|\Folder\MyTask", "MyTask", "x", "Task Scheduler (at logon)", StartupKind.ScheduledTask, true);
        _shell.Handler = (_, _) => new ScriptResult(0, "OK");

        var result = await Create().SetEnabledAsync(task, false, CancellationToken.None);

        Assert.True(result.Success);
        var call = Assert.Single(_shell.Calls);
        Assert.Equal(@"\Folder\MyTask", call.Environment["RIG_TASK_PATH"]);
        Assert.Equal("0", call.Environment["RIG_TASK_ENABLE"]);
        Assert.DoesNotContain("MyTask", call.Script);
        Assert.Single(_journal.Entries);
    }

    [Fact]
    public async Task A_failed_task_change_reports_the_message_without_journaling()
    {
        var task = new StartupItem(@"TASK|\T", "T", "x", "loc", StartupKind.ScheduledTask, true);
        _shell.Handler = (_, _) => new ScriptResult(2, "FAIL: Access is denied.");

        var result = await Create().SetEnabledAsync(task, false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Message);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task An_unknown_id_is_refused_and_a_broken_task_list_is_ignored()
    {
        var bad = await Create().RestoreAsync("NOPE|x", true, CancellationToken.None);
        _shell.Handler = (_, _) => new ScriptResult(0, "[{ not json");

        Assert.False(bad.Success);
        Assert.Empty(await Create().ListAsync(CancellationToken.None));
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN"));
    }
}

public class UndoServiceTests
{
    private readonly MemoryJournal _journal = new();
    private readonly FakeConsoleRunner _console = new();
    private readonly FakeShell _shell = new();
    private readonly FakeFiles _files = new();
    private readonly FakeExternal _external = new();
    private readonly FakeStartupManager _startup = new();
    private readonly FakeLog _log = new();

    private UndoService Create() => new(_journal, _console, _shell, _files, _external, _startup, _log);

    private static BackupEntry Entry(string kind, string target, string? file = null, string? detail = null, int minute = 0) =>
        new(new DateTime(2026, 10, 30, 9, minute, 0), kind, "Acme", target, file, detail);

    [Fact]
    public void The_list_is_newest_first_and_marks_what_was_already_restored()
    {
        var old = Entry(BackupKinds.RecycledItem, @"C:\a", minute: 1);
        var recent = Entry(BackupKinds.RegistryExport, @"HKCU\Software\A", @"C:\b\1.reg", minute: 5);
        _journal.Add(old);
        _journal.Add(recent);
        _journal.Add(new BackupEntry(new DateTime(2026, 10, 30, 10, 0, 0), BackupKinds.Restored, "Acme", old.Target, null, old.Key));

        var list = Create().List();

        Assert.Equal(new[] { "Registry backup", "Recycle Bin item" }, list.Select(i => i.TypeText));
        Assert.False(list[0].IsRestored);
        Assert.True(list[1].IsRestored);
        Assert.False(list[1].CanRestore);
    }

    [Fact]
    public async Task A_registry_backup_is_imported_with_reg_exe_and_marked_restored()
    {
        _files.Existing.Add(@"C:\b\1.reg");
        _journal.Add(Entry(BackupKinds.RegistryExport, @"HKCU\Software\A", @"C:\b\1.reg"));
        var item = Create().List().Single();

        var result = await Create().RestoreAsync(item, CancellationToken.None);

        Assert.True(result.Success);
        var call = Assert.Single(_console.Calls);
        Assert.EndsWith("reg.exe", call.File);
        Assert.Equal(new[] { "import", @"C:\b\1.reg" }, call.Args);
        Assert.True(Create().List().Single().IsRestored);
    }

    [Fact]
    public async Task A_missing_backup_file_fails_without_running_anything()
    {
        _journal.Add(Entry(BackupKinds.RegistryExport, @"HKCU\Software\A", @"C:\gone.reg"));

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_console.Calls);
        Assert.False(Create().List().Single().IsRestored);
    }

    [Fact]
    public async Task A_failed_import_is_reported_and_not_marked_restored()
    {
        _files.Existing.Add(@"C:\b\1.reg");
        _journal.Add(Entry(BackupKinds.RegistryExport, @"HKCU\Software\A", @"C:\b\1.reg"));
        _console.Result = new ScriptResult(1, "ERROR: Access is denied.");

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Message);
        Assert.False(Create().List().Single().IsRestored);
    }

    [Fact]
    public async Task A_recycled_item_is_brought_back_through_the_shell_with_the_path_in_the_environment()
    {
        _journal.Add(Entry(BackupKinds.RecycledItem, @"C:\Program Files\Acme"));
        _shell.Handler = (_, _) => new ScriptResult(0, "OK");

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.True(result.Success);
        var call = Assert.Single(_shell.Calls);
        Assert.Equal(@"C:\Program Files\Acme", call.Environment["RIG_ORIGINAL_PATH"]);
        Assert.DoesNotContain("Acme", call.Script);
    }

    [Fact]
    public async Task A_recycled_item_that_left_the_bin_gives_the_shells_reason()
    {
        _journal.Add(Entry(BackupKinds.RecycledItem, @"C:\x"));
        _shell.Handler = (_, _) => new ScriptResult(2, "FAIL: The item is no longer in the Recycle Bin.");

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no longer", result.Message);
    }

    [Fact]
    public async Task A_startup_change_is_put_back_to_its_old_state()
    {
        _journal.Add(Entry(BackupKinds.StartupChange, "RUN|CurrentUser|Run|Discord", detail: "enabled"));

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("restore:RUN|CurrentUser|Run|Discord:True", Assert.Single(_startup.Calls));
    }

    [Fact]
    public async Task A_restore_point_opens_System_Restore_and_stays_restorable()
    {
        _journal.Add(Entry(BackupKinds.RestorePoint, "RigUninstaller: before uninstalling Acme"));

        var result = await Create().RestoreAsync(Create().List().Single(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.EndsWith("rstrui.exe", Assert.Single(_external.Calls));
        Assert.Contains("before uninstalling Acme", result.Message);
        Assert.False(Create().List().Single().IsRestored);
    }
}

public class StoreAppServiceTests
{
    private readonly FakeShell _shell = new();
    private readonly FakeLog _log = new();

    private StoreAppService Create() => new(_shell, _log);

    private static StoreApp App(string name = "Contoso.Notes", bool framework = false, bool nonRemovable = false, string kind = "Store") =>
        new(name, name + "_1.0.0.0_x64__abc", "CN=Contoso, O=Contoso Ltd", "1.0.0.0", framework, nonRemovable, kind);

    private const string TwoApps =
        "[{\"Name\":\"Contoso.Notes\",\"FullName\":\"Contoso.Notes_1.0_x64__abc\",\"Publisher\":\"CN=Contoso\",\"Version\":\"1.0\",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":\"Store\"}," +
        "{\"Name\":\"Microsoft.VCLibs.140\",\"FullName\":\"Microsoft.VCLibs.140_14_x64__x\",\"Publisher\":\"CN=Microsoft\",\"Version\":\"14\",\"IsFramework\":true,\"NonRemovable\":false,\"SignatureKind\":\"Store\"}]";

    [Fact]
    public void Json_arrays_and_single_objects_are_both_read()
    {
        var many = StoreAppService.Parse(TwoApps);
        var one = StoreAppService.Parse("{\"Name\":\"A.B\",\"FullName\":\"A.B_1\",\"Publisher\":\"CN=X\",\"Version\":\"1\",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":\"Store\"}");

        Assert.Equal(new[] { "Contoso.Notes", "Microsoft.VCLibs.140" }, many.Select(a => a.Name));
        Assert.Single(one);
        Assert.Empty(StoreAppService.Parse(""));
        Assert.Empty(StoreAppService.Parse("not json"));
    }

    [Fact]
    public void Frameworks_system_packages_and_windows_parts_cannot_be_removed()
    {
        Assert.True(App().CanRemove);
        Assert.Equal("A framework other apps depend on.", App(framework: true).BlockReason);
        Assert.NotNull(App(nonRemovable: true).BlockReason);
        Assert.NotNull(App(kind: "System").BlockReason);
        Assert.NotNull(App("Microsoft.WindowsStore").BlockReason);
        Assert.NotNull(App("Microsoft.DesktopAppInstaller").BlockReason);
        Assert.NotNull(App("MicrosoftWindows.Client.Core").BlockReason);
        Assert.Null(App("Microsoft.WindowsCalculator").BlockReason);
    }

    [Fact]
    public void The_publisher_shows_its_common_name()
    {
        Assert.Equal("Contoso", App().PublisherName);
        Assert.Equal("plain", new StoreApp("n", "f", "plain", "1", false, false, "Store").PublisherName);
    }

    [Fact]
    public async Task Listing_runs_the_script_and_parses_its_output()
    {
        _shell.Handler = (_, _) => new ScriptResult(0, TwoApps);

        var apps = await Create().ListAsync(CancellationToken.None);

        Assert.Equal(2, apps.Count);
    }

    [Fact]
    public async Task A_blocked_package_is_refused_without_running_anything()
    {
        var result = await Create().RemoveAsync(App(framework: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_shell.Calls);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR"));
    }

    [Fact]
    public async Task An_odd_package_name_is_refused()
    {
        var odd = App() with { FullName = "x; Remove-Item C:\\" };

        var result = await Create().RemoveAsync(odd, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_shell.Calls);
    }

    [Fact]
    public async Task Removal_passes_the_package_in_the_environment_and_reports_success_or_the_reason()
    {
        _shell.Handler = (_, _) => new ScriptResult(0, "OK");
        var ok = await Create().RemoveAsync(App(), CancellationToken.None);

        _shell.Handler = (_, _) => new ScriptResult(2, "FAIL: Deployment failed.");
        var failed = await Create().RemoveAsync(App(), CancellationToken.None);

        Assert.True(ok.Success);
        Assert.Equal("Contoso.Notes_1.0.0.0_x64__abc", _shell.Calls[0].Environment["RIG_PACKAGE"]);
        Assert.DoesNotContain("Contoso", _shell.Calls[0].Script);
        Assert.False(failed.Success);
        Assert.Contains("Deployment failed", failed.Message);
    }
}

public class DiskUsageTests
{
    private sealed class FakeSizer : IFolderSizer
    {
        public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long? GetSize(string folder, CancellationToken cancellationToken) => Sizes.TryGetValue(folder, out var size) ? size : null;
    }

    private static InstalledProgram P(string name, long? kb = null, string? location = null) => new()
    {
        Name = name,
        EstimatedSizeKb = kb,
        InstallLocation = location,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    [Fact]
    public void Programs_are_ranked_by_size_and_limited_to_the_count()
    {
        var programs = Enumerable.Range(1, 20).Select(i => P("P" + i, kb: i * 100)).ToList();

        var top = DiskUsage.Top(programs, 15, new FakeSizer(), new SafetyGuard(VmTestBase.TestRoots), new FakeFiles(), CancellationToken.None);

        Assert.Equal(15, top.Count);
        Assert.Equal("P20", top[0].Name);
        Assert.Equal("P6", top[^1].Name);
        Assert.All(top, e => Assert.False(e.IsEstimated));
    }

    [Fact]
    public void A_program_without_a_reported_size_is_measured_from_its_folder_and_marked_estimated()
    {
        var files = new FakeFiles();
        files.Existing.Add(@"C:\Program Files\Big");
        var sizer = new FakeSizer();
        sizer.Sizes[@"C:\Program Files\Big"] = 5_000_000_000;

        var top = DiskUsage.Top(new[] { P("Big", location: @"C:\Program Files\Big"), P("Unknown"), P("Small", kb: 10) }, 15, sizer, new SafetyGuard(VmTestBase.TestRoots), files, CancellationToken.None);

        Assert.Equal(new[] { "Big", "Small" }, top.Select(e => e.Name));
        Assert.True(top[0].IsEstimated);
    }

    [Fact]
    public void Real_sizer_adds_up_the_files_of_a_folder_and_returns_null_for_a_missing_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zap-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(folder, "sub", "b.bin"), new byte[500]);
        try
        {
            Assert.Equal(1500, new FolderSizer().GetSize(folder, CancellationToken.None));
            Assert.Equal(0, new FolderSizer().GetSize(Path.Combine(folder, "nope"), CancellationToken.None) ?? 0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class InstallTrackerTests
{
    private static InstallSnapshot Snap(string[]? dirs = null, string[]? files = null, string[]? keys = null) => new()
    {
        Directories = new HashSet<string>(dirs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        Files = new HashSet<string>(files ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        Keys = new HashSet<string>(keys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Diff_keeps_only_the_outermost_new_folders_files_and_keys()
    {
        var before = Snap(dirs: new[] { @"C:\Program Files" }, files: new[] { @"C:\Program Files\old.txt" }, keys: new[] { @"HKCU\Software" });
        var after = Snap(
            dirs: new[] { @"C:\Program Files", @"C:\Program Files\Acme", @"C:\Program Files\Acme\bin", @"C:\Program Files\Other" },
            files: new[] { @"C:\Program Files\old.txt", @"C:\Program Files\Acme\acme.exe", @"C:\Program Files\loose.dll" },
            keys: new[] { @"HKCU\Software", @"HKCU\Software\Acme", @"HKCU\Software\Acme\Settings" });

        var diff = SnapshotDiff.Compute(before, after);

        Assert.Equal(
            new[]
            {
                (LeftoverKind.Folder, @"C:\Program Files\Acme"),
                (LeftoverKind.Folder, @"C:\Program Files\Other"),
                (LeftoverKind.File, @"C:\Program Files\loose.dll"),
                (LeftoverKind.RegistryKey, @"HKCU\Software\Acme"),
            },
            diff.Select(e => (e.Kind, e.Path)));
    }

    [Fact]
    public void Nothing_new_gives_an_empty_diff()
    {
        var snapshot = Snap(dirs: new[] { @"C:\a" });

        Assert.Empty(SnapshotDiff.Compute(snapshot, snapshot));
    }

    private sealed class SequenceSource : ISnapshotSource
    {
        private readonly Queue<InstallSnapshot> _snapshots;

        public SequenceSource(params InstallSnapshot[] snapshots) => _snapshots = new Queue<InstallSnapshot>(snapshots);

        public Task<InstallSnapshot> TakeAsync(IProgress<string>? progress, CancellationToken cancellationToken) => Task.FromResult(_snapshots.Dequeue());
    }

    private sealed class MemoryTraceStore : ITraceStore
    {
        public List<InstallTrace> Saved { get; } = new();

        public List<LeftoverItem> Items { get; } = new();

        public void Save(InstallTrace trace) => Saved.Add(trace);

        public IReadOnlyList<LeftoverItem> LoadItems(InstalledProgram program) => Items;
    }

    private static InstalledProgram P(string name) => new() { Name = name, RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name, KeyName = name };

    [Fact]
    public async Task A_tracked_install_is_saved_for_the_program_that_appeared()
    {
        var store = new MemoryTraceStore();
        var tracker = new InstallTracker(new SequenceSource(Snap(), Snap(dirs: new[] { @"C:\Program Files\New" })), store, new FakeLog());

        await tracker.BeginAsync(new[] { P("Old") }, null, CancellationToken.None);
        Assert.True(tracker.IsTracking);
        var result = await tracker.FinishAsync(new[] { P("Old"), P("New") }, null, CancellationToken.None);

        Assert.False(tracker.IsTracking);
        Assert.Equal("New", Assert.Single(result.Linked).Name);
        var trace = Assert.Single(store.Saved);
        Assert.Equal(@"HKLM\SOFTWARE\x\New", trace.ProgramKey);
        Assert.Equal(@"C:\Program Files\New", Assert.Single(trace.Entries).Path);
    }

    [Fact]
    public async Task When_no_new_program_appeared_nothing_is_saved()
    {
        var store = new MemoryTraceStore();
        var tracker = new InstallTracker(new SequenceSource(Snap(), Snap(dirs: new[] { @"C:\x" })), store, new FakeLog());

        await tracker.BeginAsync(new[] { P("Old") }, null, CancellationToken.None);
        var result = await tracker.FinishAsync(new[] { P("Old") }, null, CancellationToken.None);

        Assert.Empty(result.Linked);
        Assert.Empty(store.Saved);
        Assert.Contains("No new program", result.Message);
    }

    [Fact]
    public async Task Finishing_without_starting_is_an_error_and_cancel_resets()
    {
        var tracker = new InstallTracker(new SequenceSource(Snap()), new MemoryTraceStore(), new FakeLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.FinishAsync(Array.Empty<InstalledProgram>(), null, CancellationToken.None));

        await tracker.BeginAsync(Array.Empty<InstalledProgram>(), null, CancellationToken.None);
        tracker.Cancel();
        Assert.False(tracker.IsTracking);
    }

    // ---- trace store ----

    private sealed class FakeRegistryProbe : IRegistryProbe
    {
        public Dictionary<string, List<string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetSubKeyNames(string keyPath) => Keys.TryGetValue(keyPath, out var list) ? list : new List<string>();
    }

    [Fact]
    public void The_trace_store_returns_only_what_still_exists_and_is_safe_as_High_confidence_items()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zap-trace-" + Guid.NewGuid().ToString("N"));
        var files = new FakeFiles();
        files.Existing.Add(@"C:\Program Files\Acme");
        files.Existing.Add(@"C:\Windows\System32\evil");
        var registry = new FakeRegistryProbe();
        registry.Keys[@"HKCU\Software"] = new List<string> { "Acme" };
        var store = new TraceStore(folder, files, registry, new SafetyGuard(VmTestBase.TestRoots), new FakeLog());
        var program = P("Acme");
        try
        {
            store.Save(new InstallTrace("Acme", program.RegistryKeyPath, DateTime.Now, new[]
            {
                new TraceEntry(LeftoverKind.Folder, @"C:\Program Files\Acme"),
                new TraceEntry(LeftoverKind.Folder, @"C:\Program Files\Gone"),
                new TraceEntry(LeftoverKind.Folder, @"C:\Windows\System32\evil"),
                new TraceEntry(LeftoverKind.RegistryKey, @"HKCU\Software\Acme"),
                new TraceEntry(LeftoverKind.RegistryKey, @"HKCU\Software\Missing"),
                new TraceEntry(LeftoverKind.RegistryKey, @"HKLM\SYSTEM\CurrentControlSet\x"),
            }));

            var items = store.LoadItems(program);

            Assert.Equal(new[] { @"C:\Program Files\Acme", @"HKCU\Software\Acme" }, items.Select(i => i.Path));
            Assert.All(items, i => Assert.Equal(Confidence.High, i.Confidence));
            Assert.Empty(store.LoadItems(P("Untracked")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_trace_file_gives_no_items_and_a_warning()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zap-trace-" + Guid.NewGuid().ToString("N"));
        var log = new FakeLog();
        var store = new TraceStore(folder, new FakeFiles(), new FakeRegistryProbe(), new SafetyGuard(VmTestBase.TestRoots), log);
        var program = P("Acme");
        try
        {
            store.Save(new InstallTrace("Acme", program.RegistryKeyPath, DateTime.Now, Array.Empty<TraceEntry>()));
            File.WriteAllText(Directory.GetFiles(folder).Single(), "{ broken");

            Assert.Empty(store.LoadItems(program));
            Assert.Contains(log.Lines, l => l.StartsWith("WARN"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_leftover_flow_adds_traced_items_as_High_and_keeps_the_guessed_ones()
    {
        var scanner = new FakeScanner2(new LeftoverItem(LeftoverKind.Folder, @"C:\Program Files\Acme", Confidence.Medium, "guess"));
        var traces = new MemoryTraceStore();
        traces.Items.Add(new LeftoverItem(LeftoverKind.Folder, @"C:\Program Files\Acme", Confidence.High, "Created while you tracked this install."));
        traces.Items.Add(new LeftoverItem(LeftoverKind.File, @"C:\ProgramData\Acme\x.dat", Confidence.High, "Created while you tracked this install."));
        var dialogs = new FakeDialogs { LeftoverResponse = null };
        var coordinator = new LeftoverCoordinator(scanner, new FakeLeftoverCleaner(), new FakeRestore(), dialogs, new FakeLog(), null, traces);

        coordinator.RunAsync(P("Acme"), Array.Empty<InstalledProgram>(), false, CancellationToken.None).GetAwaiter().GetResult();

        var shown = dialogs.LastReviewItems!;
        Assert.Equal(2, shown.Count);
        Assert.All(shown, i => Assert.Equal(Confidence.High, i.Confidence));
    }

    private sealed class FakeScanner2 : ILeftoverScanner
    {
        private readonly IReadOnlyList<LeftoverItem> _items;

        public FakeScanner2(params LeftoverItem[] items) => _items = items;

        public IReadOnlyList<LeftoverItem> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms) => _items;
    }
}
