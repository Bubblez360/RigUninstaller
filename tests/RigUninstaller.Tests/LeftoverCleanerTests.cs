using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class LeftoverCleanerTests
{
    private static readonly SafetyGuardRoots Roots = new(
        SystemDrive: @"C:\",
        WindowsDir: @"C:\Windows",
        ProgramFiles: @"C:\Program Files",
        ProgramFilesX86: @"C:\Program Files (x86)",
        ProgramData: @"C:\ProgramData",
        UserProfile: @"C:\Users\Tester",
        AppDataRoaming: @"C:\Users\Tester\AppData\Roaming",
        AppDataLocal: @"C:\Users\Tester\AppData\Local");

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Calls { get; } = new();

        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Move(string path, bool isDirectory)
        {
            if (Failing.Contains(path)) throw new IOException("The file is in use.");
            Calls.Add((isDirectory ? "dir:" : "file:") + path);
        }
    }

    private sealed class FakeRegistryTool : IRegistryTool
    {
        public List<string> Calls { get; } = new();

        public HashSet<string> ExportFails { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DeleteFails { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<RegistryExportResult> ExportAsync(string keyPath, string file, CancellationToken cancellationToken)
        {
            Calls.Add("export:" + keyPath + "->" + file);
            return Task.FromResult(ExportFails.Contains(keyPath)
                ? new RegistryExportResult(false, "reg.exe failed")
                : new RegistryExportResult(true, "ok"));
        }

        public void DeleteKeyTree(string keyPath)
        {
            if (DeleteFails.Contains(keyPath)) throw new UnauthorizedAccessException("Access denied.");
            Calls.Add("delete:" + keyPath);
        }
    }

    private sealed class FakeJournal : IBackupJournal
    {
        public List<BackupEntry> Entries { get; } = new();

        public void Add(BackupEntry entry) => Entries.Add(entry);

        public IReadOnlyList<BackupEntry> ReadAll() => Entries;
    }

    private sealed class FakeFs : IFileSystemProbe
    {
        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetDirectories(string folder) => Array.Empty<string>();

        public IReadOnlyList<string> GetFiles(string folder) => Array.Empty<string>();

        public bool IsReparsePoint(string path) => false;

        public bool Exists(string path) => !Missing.Contains(path);
    }

    private readonly FakeRecycleBin _bin = new();
    private readonly FakeRegistryTool _registry = new();
    private readonly FakeJournal _journal = new();
    private readonly FakeFs _fs = new();
    private readonly FakeLog _log = new();
    private const string BackupRoot = @"C:\RigData\Backups";

    private LeftoverCleaner Cleaner() =>
        new(_bin, _registry, _journal, _fs, new SafetyGuard(Roots), _log, BackupRoot);

    private static InstalledProgram Program() => new()
    {
        Name = "Acme Editor",
        RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
        KeyName = "Acme",
    };

    private static LeftoverItem Folder(string path) => new(LeftoverKind.Folder, path, Confidence.High, "name match");

    private static LeftoverItem File_(string path) => new(LeftoverKind.File, path, Confidence.High, "name match");

    private static LeftoverItem Key(string path) => new(LeftoverKind.RegistryKey, path, Confidence.High, "name match");

    // ---- files and folders ----

    [Fact]
    public async Task Folders_and_files_go_to_the_recycle_bin()
    {
        var result = await Cleaner().CleanAsync(
            Program(),
            new[] { Folder(@"C:\Program Files\Acme Editor"), File_(@"C:\ProgramData\Acme\readme.txt") },
            CancellationToken.None);

        Assert.Equal(new[] { @"dir:C:\Program Files\Acme Editor", @"file:C:\ProgramData\Acme\readme.txt" }, _bin.Calls);
        Assert.Equal(2, result.Moved);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Every_recycled_item_is_logged_and_journaled()
    {
        await Cleaner().CleanAsync(Program(), new[] { Folder(@"C:\Program Files\Acme Editor") }, CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.Contains("Recycle Bin") && l.Contains(@"C:\Program Files\Acme Editor"));
        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(BackupKinds.RecycledItem, entry.Kind);
        Assert.Equal(@"C:\Program Files\Acme Editor", entry.Target);
        Assert.Equal("Acme Editor", entry.Program);
    }

    [Fact]
    public async Task A_path_that_is_already_gone_is_not_an_error()
    {
        _fs.Missing.Add(@"C:\Program Files\Acme Editor");

        var result = await Cleaner().CleanAsync(Program(), new[] { Folder(@"C:\Program Files\Acme Editor") }, CancellationToken.None);

        Assert.Empty(_bin.Calls);
        Assert.Equal(1, result.AlreadyGone);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Protected_paths_are_refused_and_never_touched()
    {
        var result = await Cleaner().CleanAsync(
            Program(),
            new[] { Folder(@"C:\Windows\System32"), Folder(@"C:\Program Files") },
            CancellationToken.None);

        Assert.Empty(_bin.Calls);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Contains("safety", f.Error, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("System32"));
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_other_items()
    {
        _bin.Failing.Add(@"C:\Program Files\Locked");

        var result = await Cleaner().CleanAsync(
            Program(),
            new[] { Folder(@"C:\Program Files\Locked"), Folder(@"C:\Program Files\Fine") },
            CancellationToken.None);

        Assert.Equal(new[] { @"dir:C:\Program Files\Fine" }, _bin.Calls);
        Assert.Equal(1, result.Moved);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(@"C:\Program Files\Locked", failure.Item.Path);
        Assert.Contains("in use", failure.Error);
    }

    // ---- registry ----

    [Fact]
    public async Task Registry_keys_are_backed_up_before_they_are_deleted()
    {
        var result = await Cleaner().CleanAsync(Program(), new[] { Key(@"HKCU\Software\AcmeEditor") }, CancellationToken.None);

        Assert.Equal(2, _registry.Calls.Count);
        Assert.StartsWith(@"export:HKCU\Software\AcmeEditor->" + BackupRoot, _registry.Calls[0]);
        Assert.EndsWith(".reg", _registry.Calls[0]);
        Assert.Equal(@"delete:HKCU\Software\AcmeEditor", _registry.Calls[1]);
        Assert.Equal(1, result.RegistryRemoved);
    }

    [Fact]
    public async Task Registry_backup_and_delete_are_journaled_with_the_backup_file()
    {
        await Cleaner().CleanAsync(Program(), new[] { Key(@"HKCU\Software\AcmeEditor") }, CancellationToken.None);

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(BackupKinds.RegistryExport, entry.Kind);
        Assert.Equal(@"HKCU\Software\AcmeEditor", entry.Target);
        Assert.EndsWith(".reg", entry.BackupFile);
        Assert.Contains(_log.Lines, l => l.Contains("Exported registry key") && l.Contains(@"HKCU\Software\AcmeEditor"));
        Assert.Contains(_log.Lines, l => l.Contains("Deleted registry key") && l.Contains(@"HKCU\Software\AcmeEditor"));
    }

    [Fact]
    public async Task A_key_inside_an_already_removed_key_is_counted_as_gone_without_a_second_backup()
    {
        // The child is listed first on purpose: parents are always handled before their children.
        var result = await Cleaner().CleanAsync(
            Program(),
            new[] { Key(@"HKCU\Software\Acme\Editor"), Key(@"HKCU\Software\Acme") },
            CancellationToken.None);

        Assert.Equal(1, result.RegistryRemoved);
        Assert.Equal(1, result.AlreadyGone);
        Assert.Empty(result.Failures);
        Assert.Equal(2, _registry.Calls.Count);
        Assert.Contains(@"delete:HKCU\Software\Acme", _registry.Calls);
        Assert.DoesNotContain(_registry.Calls, c => c.Contains(@"Acme\Editor"));
    }

    [Fact]
    public async Task A_failed_backup_means_the_key_is_not_deleted()
    {
        _registry.ExportFails.Add(@"HKCU\Software\AcmeEditor");

        var result = await Cleaner().CleanAsync(Program(), new[] { Key(@"HKCU\Software\AcmeEditor") }, CancellationToken.None);

        Assert.DoesNotContain(_registry.Calls, c => c.StartsWith("delete:"));
        Assert.Equal(0, result.RegistryRemoved);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("backup", failure.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task A_failed_delete_is_reported_but_the_backup_stays_journaled()
    {
        _registry.DeleteFails.Add(@"HKCU\Software\AcmeEditor");

        var result = await Cleaner().CleanAsync(Program(), new[] { Key(@"HKCU\Software\AcmeEditor") }, CancellationToken.None);

        Assert.Equal(0, result.RegistryRemoved);
        Assert.Contains("Access denied", Assert.Single(result.Failures).Error);
    }

    [Fact]
    public async Task Protected_registry_keys_are_refused_before_any_backup_or_delete()
    {
        var result = await Cleaner().CleanAsync(
            Program(),
            new[] { Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), Key(@"HKLM\SYSTEM\CurrentControlSet") },
            CancellationToken.None);

        Assert.Empty(_registry.Calls);
        Assert.Equal(2, result.Failures.Count);
    }

    [Fact]
    public async Task Backup_files_of_one_run_share_a_folder_and_have_distinct_names()
    {
        await Cleaner().CleanAsync(
            Program(),
            new[] { Key(@"HKCU\Software\AcmeEditor"), Key(@"HKLM\SOFTWARE\AcmeEditor") },
            CancellationToken.None);

        var files = _journal.Entries.Select(e => e.BackupFile!).ToList();
        Assert.Equal(2, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.All(files, f => Assert.StartsWith(BackupRoot, f));
    }

    [Fact]
    public async Task Program_names_with_odd_characters_make_safe_backup_paths()
    {
        var program = new InstalledProgram { Name = @"Evil: ..\..\Name?*", RegistryKeyPath = @"HKLM\x", KeyName = "x" };

        await Cleaner().CleanAsync(program, new[] { Key(@"HKCU\Software\AcmeEditor") }, CancellationToken.None);

        var file = Assert.Single(_journal.Entries).BackupFile!;
        Assert.StartsWith(BackupRoot, Path.GetFullPath(file));
        Assert.DoesNotContain("..", file[BackupRoot.Length..]);
    }

    // ---- cancel and summary ----

    [Fact]
    public async Task Cancelling_stops_before_the_next_item()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Cleaner().CleanAsync(Program(), new[] { Folder(@"C:\Program Files\Acme Editor") }, cts.Token);

        Assert.Empty(_bin.Calls);
        Assert.True(result.WasCancelled);
    }

    [Fact]
    public async Task Summary_is_written_to_the_log()
    {
        await Cleaner().CleanAsync(
            Program(),
            new[] { Folder(@"C:\Program Files\Acme Editor"), Key(@"HKCU\Software\AcmeEditor") },
            CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.Contains("Leftover cleanup finished") && l.Contains("1 moved") && l.Contains("1 registry"));
    }
}
