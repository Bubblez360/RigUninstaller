using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class DriveEntryTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Used_space_and_fraction_come_from_total_and_free()
    {
        var drive = new DriveEntry(@"F:\", "Games", "NTFS", 500 * Gb, 125 * Gb);

        Assert.Equal(375 * Gb, drive.UsedBytes);
        Assert.Equal(0.75, drive.UsedFraction, 3);
        Assert.Equal("F:", drive.Name);
    }

    [Fact]
    public void An_empty_or_odd_drive_never_gives_a_fraction_outside_zero_to_one()
    {
        Assert.Equal(0, new DriveEntry(@"E:\", "", "NTFS", 0, 0).UsedFraction);
        Assert.Equal(0, new DriveEntry(@"E:\", "", "NTFS", 100, 150).UsedFraction);
    }

    [Fact]
    public void The_drive_card_says_almost_full_under_ten_percent_free_and_shows_label_and_sizes()
    {
        var low = new DriveViewModel(new DriveEntry(@"C:\", "Windows", "NTFS", 100 * Gb, 5 * Gb));
        var fine = new DriveViewModel(new DriveEntry(@"F:\", "", "NTFS", 100 * Gb, 50 * Gb));

        Assert.True(low.IsLow);
        Assert.False(fine.IsLow);
        Assert.Equal("C:  Windows", low.Title);
        Assert.Equal("F:", fine.Title);
        Assert.Equal("5 GB free  ·  almost full", low.FreeText);
        Assert.Equal("50 GB free", fine.FreeText);
        Assert.Equal("95 GB used of 100 GB", low.UsedText);
    }

    [Fact]
    public void The_real_drive_source_lists_at_least_the_system_drive_with_sane_numbers()
    {
        var drives = new DriveSource().GetDrives();

        var system = Assert.Single(drives, d => string.Equals(d.RootPath, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
    }
}

public class FolderBreakdownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zap-break-" + Guid.NewGuid().ToString("N"));

    public FolderBreakdownTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "big", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "small"));
        File.WriteAllBytes(Path.Combine(_root, "big", "a.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "big", "deep", "b.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(_root, "small", "c.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "loose.bin"), new byte[1000]);
    }

    public void Dispose()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) Directory.Delete(directory, false);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Folders_include_everything_below_them_and_files_are_listed_biggest_first()
    {
        var entries = await new FolderBreakdown().ScanAsync(_root, null, CancellationToken.None);

        Assert.Equal(new[] { "big", "loose.bin", "small" }, entries.Select(e => e.Name));
        Assert.Equal(new[] { 5000L, 1000L, 100L }, entries.Select(e => e.Bytes));
        Assert.Equal(new[] { true, false, true }, entries.Select(e => e.IsFolder));
        Assert.Equal(Path.Combine(_root, "big"), entries[0].FullPath);
    }

    [Fact]
    public async Task Each_entry_is_reported_as_it_finishes()
    {
        var reported = new List<FolderEntry>();

        await new FolderBreakdown().ScanAsync(_root, new Immediate(reported), CancellationToken.None);

        Assert.Equal(3, reported.Count);
    }

    private sealed class Immediate : IProgress<FolderEntry>
    {
        private readonly List<FolderEntry> _list;

        public Immediate(List<FolderEntry> list) => _list = list;

        public void Report(FolderEntry value)
        {
            lock (_list) _list.Add(value);
        }
    }

    [Fact]
    public async Task A_junction_is_not_followed_so_nothing_is_counted_twice()
    {
        var target = Path.Combine(_root, "big");
        var link = Path.Combine(_root, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
        {
            mklink!.WaitForExit();
        }

        var entries = await new FolderBreakdown().ScanAsync(_root, null, CancellationToken.None);

        Assert.DoesNotContain(entries, e => e.Name == "link");
        Assert.Equal(6100L, entries.Sum(e => e.Bytes));
    }

    [Fact]
    public async Task A_missing_folder_gives_an_empty_result_and_cancel_is_honoured()
    {
        var empty = await new FolderBreakdown().ScanAsync(Path.Combine(_root, "nope"), null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Empty(empty);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FolderBreakdown().ScanAsync(_root, null, cts.Token));
    }
}

public class DiskUsageDriveViewModelTests
{
    private sealed class FakeDrives : IDriveSource
    {
        public List<DriveEntry> Drives { get; } = new()
        {
            new DriveEntry(@"C:\", "Windows", "NTFS", 1000, 50),
            new DriveEntry(@"F:\", "Games", "NTFS", 2000, 1500),
        };

        public IReadOnlyList<DriveEntry> GetDrives() => Drives;
    }

    private sealed class FakeBreakdown : IFolderBreakdown
    {
        public Dictionary<string, List<FolderEntry>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Scanned { get; } = new();

        public TaskCompletionSource<bool>? Gate { get; set; }

        public Exception? Throw { get; set; }

        public async Task<IReadOnlyList<FolderEntry>> ScanAsync(string folder, IProgress<FolderEntry>? progress, CancellationToken cancellationToken)
        {
            Scanned.Add(folder);
            if (Throw is not null) throw Throw;
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            return Folders.TryGetValue(folder, out var list) ? list : new List<FolderEntry>();
        }
    }

    private readonly FakeDrives _drives = new();
    private readonly FakeBreakdown _breakdown = new();
    private readonly FakeExternal _external = new();

    private DiskUsageViewModel Create() =>
        new(new FakeSizer(), new SafetyGuard(VmTestBase.TestRoots), new FakeFiles(), new FakeLog(), _drives, _breakdown, _external);

    private sealed class FakeSizer : IFolderSizer
    {
        public long? GetSize(string folder, CancellationToken cancellationToken) => null;
    }

    private static FolderEntry Folder(string name, long bytes, string parent = @"F:\") => new(name, Path.Combine(parent, name), bytes, true);

    [Fact]
    public void Loading_lists_every_drive()
    {
        var vm = Create();

        vm.LoadDrives();

        Assert.Equal(new[] { "C:", "F:" }, vm.Drives.Select(d => d.Name));
        Assert.True(vm.Drives[0].IsLow);
    }

    [Fact]
    public async Task Picking_a_drive_shows_its_biggest_folders_first_with_a_summary()
    {
        _breakdown.Folders[@"F:\"] = new List<FolderEntry> { Folder("Small", 10), Folder("Games", 900), new FolderEntry("big.iso", @"F:\big.iso", 400, false) };
        var vm = Create();
        vm.LoadDrives();

        await vm.ScanDriveCommand.ExecuteAsync(vm.Drives[1]);

        Assert.Equal(@"F:\", vm.CurrentPath);
        Assert.Equal(new[] { "Games", "big.iso", "Small" }, vm.FolderRows.Select(r => r.Name));
        Assert.All(vm.FolderRows, r => Assert.Equal(900, r.Maximum));
        Assert.Contains("3 items", vm.FolderStatus);
        Assert.False(vm.IsScanningFolders);
    }

    [Fact]
    public async Task Clicking_a_folder_looks_inside_it_and_Up_goes_back_but_files_cannot_be_opened()
    {
        _breakdown.Folders[@"F:\"] = new List<FolderEntry> { Folder("Games", 900), new FolderEntry("big.iso", @"F:\big.iso", 400, false) };
        _breakdown.Folders[@"F:\Games"] = new List<FolderEntry> { Folder("Dota", 800, @"F:\Games") };
        var vm = Create();
        vm.LoadDrives();
        await vm.ScanDriveCommand.ExecuteAsync(vm.Drives[1]);
        var games = vm.FolderRows.Single(r => r.Name == "Games");
        var file = vm.FolderRows.Single(r => r.Name == "big.iso");

        Assert.False(vm.OpenFolderCommand.CanExecute(file));
        await vm.OpenFolderCommand.ExecuteAsync(games);

        Assert.Equal(@"F:\Games", vm.CurrentPath);
        Assert.Equal(new[] { "Dota" }, vm.FolderRows.Select(r => r.Name));
        Assert.True(vm.UpCommand.CanExecute(null));

        await vm.UpCommand.ExecuteAsync(null);

        Assert.Equal(@"F:\", vm.CurrentPath);
        Assert.False(vm.UpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_cancels_a_running_measure_and_says_the_sizes_are_partial()
    {
        _breakdown.Gate = new TaskCompletionSource<bool>();
        var vm = Create();
        vm.LoadDrives();

        var run = vm.ScanDriveCommand.ExecuteAsync(vm.Drives[1]);
        Assert.True(vm.IsScanningFolders);
        Assert.True(vm.StopScanCommand.CanExecute(null));

        vm.StopScanCommand.Execute(null);
        await run;

        Assert.False(vm.IsScanningFolders);
        Assert.Contains("Stopped", vm.FolderStatus);
    }

    [Fact]
    public async Task A_scan_error_is_shown_not_thrown()
    {
        _breakdown.Throw = new InvalidOperationException("disk gone");
        var vm = Create();
        vm.LoadDrives();

        await vm.ScanDriveCommand.ExecuteAsync(vm.Drives[0]);

        Assert.Contains("disk gone", vm.FolderStatus);
        Assert.False(vm.IsScanningFolders);
    }

    [Fact]
    public async Task Measure_again_and_Open_in_Explorer_use_the_current_folder()
    {
        _breakdown.Folders[@"F:\"] = new List<FolderEntry> { Folder("Games", 900) };
        var vm = Create();
        vm.LoadDrives();
        await vm.ScanDriveCommand.ExecuteAsync(vm.Drives[1]);

        await vm.RescanFolderCommand.ExecuteAsync(null);
        vm.ExploreCommand.Execute(null);

        Assert.Equal(2, _breakdown.Scanned.Count(p => p == @"F:\"));
        Assert.Equal(@"folder:F:\", Assert.Single(_external.Calls));
    }

    [Fact]
    public void Opening_the_tab_loads_the_drives()
    {
        var vm = Create();

        vm.Activate();

        Assert.Equal(2, vm.Drives.Count);
    }
}
