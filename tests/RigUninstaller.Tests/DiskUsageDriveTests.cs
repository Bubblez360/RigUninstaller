using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class DriveOfTests
{
    private static InstalledProgram P(string? location = null, string? icon = null, string? uninstall = null) => new()
    {
        Name = "App",
        InstallLocation = location,
        DisplayIcon = icon,
        UninstallString = uninstall,
        RegistryKeyPath = @"HKLM\x\App",
        KeyName = "App",
    };

    [Theory]
    [InlineData(@"C:\Program Files\App", "C:")]
    [InlineData(@"f:\Games\App\", "F:")]
    [InlineData("\"D:\\Tools\\App\"", "D:")]
    public void The_install_folder_decides_the_drive(string location, string drive)
    {
        Assert.Equal(drive, DiskUsage.DriveOf(P(location: location)));
    }

    [Fact]
    public void Without_an_install_folder_the_icon_file_then_the_uninstaller_are_used()
    {
        Assert.Equal("E:", DiskUsage.DriveOf(P(icon: @"E:\App\app.exe,0")));
        Assert.Equal("G:", DiskUsage.DriveOf(P(uninstall: "\"G:\\App\\unins000.exe\" /S")));
    }

    [Fact]
    public void The_long_path_form_is_still_the_local_drive_and_a_bare_root_is_unknown()
    {
        Assert.Equal("C:", DiskUsage.DriveOf(P(location: @"\\?\C:\Program Files\App")));
        Assert.Equal(DiskUsage.UnknownDrive, DiskUsage.DriveOf(P(location: @"\Program Files\App")));
    }

    [Fact]
    public void A_network_path_and_no_path_at_all_have_their_own_labels()
    {
        Assert.Equal(DiskUsage.NetworkDrive, DiskUsage.DriveOf(P(location: @"\\server\share\App")));
        Assert.Equal(DiskUsage.UnknownDrive, DiskUsage.DriveOf(P()));
        Assert.Equal(DiskUsage.UnknownDrive, DiskUsage.DriveOf(P(location: "relative\\path")));
    }
}

public class DiskUsageDriveFilterTests
{
    private sealed class NoSizer : IFolderSizer
    {
        public long? GetSize(string folder, CancellationToken cancellationToken) => null;
    }

    private static InstalledProgram P(string name, long kb, string location) => new()
    {
        Name = name,
        EstimatedSizeKb = kb,
        InstallLocation = location,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    private static DiskUsageViewModel Create(params InstalledProgram[] programs)
    {
        var vm = new DiskUsageViewModel(new NoSizer(), new SafetyGuard(VmTestBase.TestRoots), new FakeFiles(), new FakeLog());
        vm.SetPrograms(programs);
        return vm;
    }

    private static readonly InstalledProgram[] Sample =
    {
        P("Game", 8 * 1024 * 1024, @"F:\Games\Game"),
        P("Editor", 2 * 1024 * 1024, @"C:\Program Files\Editor"),
        P("Tool", 1024 * 1024, @"C:\Program Files\Tool"),
        P("Mystery", 1024, ""),
    };

    [Fact]
    public async Task One_button_per_drive_plus_all_drives_each_with_its_total()
    {
        var vm = Create(Sample);

        await vm.ComputeAsync();

        Assert.Equal(new[] { "All drives", "C:", "F:", "Unknown" }, vm.DriveFilters.Select(f => f.Label));
        var all = vm.DriveFilters[0];
        Assert.Equal(4, all.Count);
        Assert.Equal((11L * 1024 * 1024 + 1024) * 1024, all.TotalBytes); // sizes are in KB: 8 + 2 + 1 GB + 1 MB
        Assert.Equal(2, vm.DriveFilters.Single(f => f.Drive == "C:").Count);
        Assert.Equal(3L * 1024 * 1024 * 1024, vm.DriveFilters.Single(f => f.Drive == "C:").TotalBytes);
        Assert.True(all.IsSelected);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Contains("in 4 programs on all drives", vm.TotalText);
    }

    [Fact]
    public async Task The_placeholder_bars_show_only_while_there_is_nothing_else_to_show()
    {
        var vm = Create(Sample);
        Assert.False(vm.ShowSkeleton);

        await vm.ComputeAsync();
        Assert.False(vm.IsMeasuring);
        Assert.False(vm.ShowSkeleton);
        Assert.NotEmpty(vm.Rows);

        // A refresh over existing rows must not cover them with placeholders.
        var refresh = vm.ComputeAsync();
        Assert.False(vm.ShowSkeleton);
        await refresh;
    }

    [Fact]
    public async Task Picking_a_drive_shows_only_its_programs_with_shares_of_that_drive()
    {
        var vm = Create(Sample);
        await vm.ComputeAsync();

        vm.SelectDriveFilterCommand.Execute(vm.DriveFilters.Single(f => f.Drive == "C:"));

        Assert.Equal(new[] { "Editor", "Tool" }, vm.Rows.Select(r => r.Name));
        Assert.Equal(new[] { "67%", "33%" }, vm.Rows.Select(r => r.ShareText));
        Assert.Equal("Biggest programs on C:", vm.Heading);
        Assert.Contains("in 2 programs on C:", vm.TotalText);
        Assert.True(vm.DriveFilters.Single(f => f.Drive == "C:").IsSelected);
        Assert.False(vm.DriveFilters[0].IsSelected);
        Assert.Equal("primary", vm.DriveFilters.Single(f => f.Drive == "C:").ButtonTag);
    }

    [Fact]
    public async Task All_drives_again_shows_everything_and_the_pick_survives_a_refresh()
    {
        var vm = Create(Sample);
        await vm.ComputeAsync();
        vm.SelectDrive("F:");
        Assert.Equal(new[] { "Game" }, vm.Rows.Select(r => r.Name));

        await vm.ComputeAsync();
        Assert.Equal(new[] { "Game" }, vm.Rows.Select(r => r.Name));

        vm.SelectDrive(null);
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public async Task A_drive_that_disappears_falls_back_to_all_drives()
    {
        var vm = Create(Sample);
        await vm.ComputeAsync();
        vm.SelectDrive("F:");

        vm.SetPrograms(Sample.Where(p => p.Name != "Game").ToArray());
        await vm.ComputeAsync();

        Assert.True(vm.DriveFilters[0].IsSelected);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Theory]
    [InlineData(1, 1000, "<1%")]
    [InlineData(500, 1000, "50%")]
    [InlineData(1000, 1000, "100%")]
    public void Shares_are_whole_percents_and_tiny_ones_say_less_than_one(long bytes, long total, string expected)
    {
        var row = new DiskUsageRowViewModel(new DiskUsageEntry("x", bytes, false, "C:"), total, total);

        Assert.Equal(expected, row.ShareText);
    }
}
