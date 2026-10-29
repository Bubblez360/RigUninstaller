using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class RunningProcessFinderTests
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

    private sealed class FakeSnapshot : IProcessSnapshot
    {
        public List<RunningProcessInfo> Processes { get; } = new();

        public IReadOnlyList<RunningProcessInfo> GetProcesses() => Processes;
    }

    private readonly FakeSnapshot _snapshot = new();

    private RunningProcessFinder Finder() => new(_snapshot, new SafetyGuard(Roots));

    private static InstalledProgram P(string? location = null, string? icon = null, string? uninstall = null) =>
        new()
        {
            Name = "Acme",
            InstallLocation = location,
            DisplayIcon = icon,
            UninstallString = uninstall,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
            KeyName = "Acme",
        };

    private void Run(int id, string name, string path) => _snapshot.Processes.Add(new RunningProcessInfo(id, name, path));

    [Fact]
    public void Finds_processes_running_from_the_install_folder()
    {
        Run(1, "acme", @"C:\Program Files\Acme\acme.exe");
        Run(2, "other", @"C:\Program Files\Other\other.exe");

        var found = Finder().FindRunning(P(location: @"C:\Program Files\Acme"));

        Assert.Equal(new[] { "acme" }, found.Select(p => p.Name));
    }

    [Fact]
    public void Match_is_case_insensitive_and_includes_sub_folders()
    {
        Run(1, "helper", @"C:\PROGRAM FILES\acme\sub\helper.exe");

        var found = Finder().FindRunning(P(location: @"C:\Program Files\Acme"));

        Assert.Single(found);
    }

    [Fact]
    public void A_folder_with_the_same_start_is_not_a_match()
    {
        Run(1, "x", @"C:\Program Files\Acme Other\x.exe");

        var found = Finder().FindRunning(P(location: @"C:\Program Files\Acme"));

        Assert.Empty(found);
    }

    [Fact]
    public void Install_location_with_quotes_and_trailing_slash_still_works()
    {
        Run(1, "acme", @"C:\Program Files\Acme\acme.exe");

        var found = Finder().FindRunning(P(location: @"""C:\Program Files\Acme\"""));

        Assert.Single(found);
    }

    [Fact]
    public void Display_icon_folder_is_used_when_there_is_no_install_location()
    {
        Run(1, "beta", @"C:\Program Files\Beta\beta.exe");

        var found = Finder().FindRunning(P(icon: @"C:\Program Files\Beta\beta.exe,0"));

        Assert.Single(found);
    }

    [Fact]
    public void Uninstaller_folder_is_used_as_a_last_hint()
    {
        Run(1, "gamma", @"D:\Apps\Gamma\gamma.exe");

        var found = Finder().FindRunning(P(uninstall: @"""D:\Apps\Gamma\unins000.exe"" /S"));

        Assert.Single(found);
    }

    [Theory]
    [InlineData(@"C:\Windows\Installer\{ABC}")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\")]
    public void Protected_folders_are_never_used_to_match_processes(string location)
    {
        Run(1, "notepad", @"C:\Windows\notepad.exe");
        Run(2, "anything", @"C:\Program Files\Any\any.exe");

        var found = Finder().FindRunning(P(location: location));

        Assert.Empty(found);
    }

    [Fact]
    public void This_app_is_never_reported()
    {
        Run(Environment.ProcessId, "zap", @"C:\Program Files\Acme\zap.exe");

        var found = Finder().FindRunning(P(location: @"C:\Program Files\Acme"));

        Assert.Empty(found);
    }

    [Fact]
    public void Result_is_sorted_by_name_and_has_no_duplicate_ids()
    {
        Run(5, "zeta", @"C:\Program Files\Acme\z.exe");
        Run(3, "alpha", @"C:\Program Files\Acme\a.exe");
        Run(3, "alpha", @"C:\Program Files\Acme\a.exe");

        var found = Finder().FindRunning(P(location: @"C:\Program Files\Acme", icon: @"C:\Program Files\Acme\a.exe"));

        Assert.Equal(new[] { "alpha", "zeta" }, found.Select(p => p.Name));
    }

    [Fact]
    public void Real_snapshot_lists_this_test_process_with_its_path()
    {
        var processes = new ProcessSnapshot().GetProcesses();

        var self = Assert.Single(processes, p => p.Id == Environment.ProcessId);
        Assert.True(File.Exists(self.Path), self.Path);
    }

    [Fact]
    public void Program_with_no_folder_hints_matches_nothing()
    {
        Run(1, "x", @"C:\Program Files\Acme\x.exe");

        Assert.Empty(Finder().FindRunning(P()));
    }
}
