using Microsoft.Win32;
using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>
/// Tests against the real machine, but only read-only or on throw-away items this test creates itself
/// (a temp folder, and one registry key under HKCU\Software that the test removes again).
/// </summary>
public class LeftoverInfrastructureTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "zap-infra-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "RigUninstallerTest_" + Guid.NewGuid().ToString("N");

    public LeftoverInfrastructureTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            // A junction made by a test must be removed as a link first, or the delete refuses.
            foreach (var directory in Directory.EnumerateDirectories(_temp))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) Directory.Delete(directory, recursive: false);
            }

            Directory.Delete(_temp, recursive: true);
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + _keyName, throwOnMissingSubKey: false);
    }

    // ---- registry tool ----

    [Fact]
    public async Task RegistryTool_exports_a_real_key_then_deletes_it()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\" + _keyName + @"\Child"))
        {
            key.SetValue("Marker", "abc123-marker");
        }

        var tool = new RegistryTool();
        var keyPath = @"HKCU\Software\" + _keyName;
        var file = Path.Combine(_temp, "backup.reg");

        var export = await tool.ExportAsync(keyPath, file, CancellationToken.None);

        Assert.True(export.Success, export.Message);
        Assert.Contains("abc123-marker", File.ReadAllText(file));

        tool.DeleteKeyTree(keyPath);

        Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\" + _keyName));
    }

    [Fact]
    public async Task RegistryTool_export_of_a_missing_key_fails_without_creating_a_file()
    {
        var file = Path.Combine(_temp, "missing.reg");

        var export = await new RegistryTool().ExportAsync(@"HKCU\Software\" + _keyName + "_nope", file, CancellationToken.None);

        Assert.False(export.Success);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void RegistryTool_delete_of_a_missing_key_is_not_an_error()
    {
        new RegistryTool().DeleteKeyTree(@"HKCU\Software\" + _keyName + "_nope");
    }

    // ---- registry probe ----

    [Fact]
    public void RegistryProbe_lists_real_subkeys_and_returns_nothing_for_missing_keys()
    {
        var probe = new RegistryProbe();

        Assert.NotEmpty(probe.GetSubKeyNames(@"HKCU\Software"));
        Assert.NotEmpty(probe.GetSubKeyNames(@"HKLM\SOFTWARE"));
        Assert.Empty(probe.GetSubKeyNames(@"HKCU\Software\" + _keyName + "_nope"));
    }

    // ---- file system probe ----

    [Fact]
    public void FileSystemProbe_lists_folders_and_files_and_knows_what_exists()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "sub"));
        File.WriteAllText(Path.Combine(_temp, "a.txt"), "x");
        var probe = new FileSystemProbe();

        Assert.Equal(new[] { Path.Combine(_temp, "sub") }, probe.GetDirectories(_temp));
        Assert.Equal(new[] { Path.Combine(_temp, "a.txt") }, probe.GetFiles(_temp));
        Assert.True(probe.Exists(Path.Combine(_temp, "sub")));
        Assert.True(probe.Exists(Path.Combine(_temp, "a.txt")));
        Assert.False(probe.Exists(Path.Combine(_temp, "nope")));
        Assert.False(probe.IsReparsePoint(Path.Combine(_temp, "sub")));
    }

    [Fact]
    public void FileSystemProbe_returns_nothing_for_missing_folders()
    {
        var probe = new FileSystemProbe();

        Assert.Empty(probe.GetDirectories(Path.Combine(_temp, "nope")));
        Assert.Empty(probe.GetFiles(Path.Combine(_temp, "nope")));
    }

    [Fact]
    public void FileSystemProbe_recognises_a_junction_as_a_reparse_point()
    {
        var target = Path.Combine(_temp, "target");
        var link = Path.Combine(_temp, "link");
        Directory.CreateDirectory(target);
        // A junction needs no special privilege (a symbolic link would).
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
        {
            mklink!.WaitForExit();
        }

        Assert.True(new FileSystemProbe().IsReparsePoint(link));
    }

    // ---- backup journal ----

    [Fact]
    public void BackupJournal_round_trips_entries_and_survives_a_damaged_line()
    {
        var file = Path.Combine(_temp, "Backups", "journal.jsonl");
        var journal = new BackupJournal(file);
        var entry = new BackupEntry(new DateTime(2026, 10, 30, 9, 15, 0), BackupKinds.RegistryExport, "Acme Editor", @"HKCU\Software\Acme", @"C:\b\1.reg");

        journal.Add(entry);
        File.AppendAllText(file, "{ this is not json" + Environment.NewLine);
        journal.Add(entry with { Kind = BackupKinds.RecycledItem, Target = @"C:\x", BackupFile = null });

        var all = journal.ReadAll();

        Assert.Equal(2, all.Count);
        Assert.Equal(entry, all[0]);
        Assert.Equal(BackupKinds.RecycledItem, all[1].Kind);
        Assert.Null(all[1].BackupFile);
    }

    [Fact]
    public void BackupJournal_with_no_file_is_empty()
    {
        Assert.Empty(new BackupJournal(Path.Combine(_temp, "none.jsonl")).ReadAll());
    }

    // ---- real scan (read-only) ----

    [Fact]
    public void Scanner_with_real_probes_finds_a_real_program_files_folder_by_its_name()
    {
        var guard = new SafetyGuard();
        var folder = Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .FirstOrDefault(d => guard.CheckPath(d).IsAllowed
                                 && !new FileSystemProbe().IsReparsePoint(d)
                                 && Path.GetFileName(d).Length >= 6
                                 && !Path.GetFileName(d).Any(char.IsDigit));
        if (folder is null)
        {
            return; // nothing suitable on this machine
        }

        var scanner = new LeftoverScanner(new FileSystemProbe(), new RegistryProbe(), guard, SafetyGuardRoots.FromEnvironment(), AppContext.BaseDirectory, new FakeLog());
        var program = new InstalledProgram { Name = Path.GetFileName(folder), RegistryKeyPath = @"HKLM\x\y", KeyName = "y" };

        var items = scanner.Scan(program, Array.Empty<InstalledProgram>());

        Assert.Contains(items, i => i.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) && i.Confidence == Confidence.High);
        Assert.All(items, i =>
        {
            var allowed = i.Kind == LeftoverKind.RegistryKey ? guard.CheckRegistryKey(i.Path).IsAllowed : guard.CheckPath(i.Path).IsAllowed;
            Assert.True(allowed, i.Path);
        });
    }

    [Fact]
    public void Scanner_with_real_probes_finds_nothing_for_a_made_up_program()
    {
        var scanner = new LeftoverScanner(new FileSystemProbe(), new RegistryProbe(), new SafetyGuard(), SafetyGuardRoots.FromEnvironment(), AppContext.BaseDirectory, new FakeLog());
        var program = new InstalledProgram { Name = "Qzxv Wplk " + Guid.NewGuid().ToString("N")[..8], RegistryKeyPath = @"HKLM\x\y", KeyName = "y" };

        Assert.Empty(scanner.Scan(program, Array.Empty<InstalledProgram>()));
    }
}
