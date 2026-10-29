using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class CleanupWiringTests : VmTestBase
{
    private sealed class CountingCacheCleaner : ICacheCleaner
    {
        public int Scans { get; private set; }

        public Task<IReadOnlyList<CacheScanResult>> ScanAsync(IReadOnlyList<CacheCategory> categories, CancellationToken cancellationToken)
        {
            Scans++;
            return Task.FromResult<IReadOnlyList<CacheScanResult>>(categories.Select(c => new CacheScanResult(c.Id, 10, 1)).ToList());
        }

        public Task<IReadOnlyList<CacheCleanResult>> CleanAsync(IReadOnlyList<CacheCategory> categories, IProgress<string>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("opening the tab must never clean anything");
    }

    private sealed class RecordingOrphanScanner : IOrphanFolderScanner
    {
        public IReadOnlyList<InstalledProgram>? Programs { get; private set; }

        public Task<IReadOnlyList<OrphanFolder>> ScanAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken)
        {
            Programs = programs;
            return Task.FromResult<IReadOnlyList<OrphanFolder>>(Array.Empty<OrphanFolder>());
        }
    }

    private (MainViewModel Vm, CountingCacheCleaner Cache, OrphanScanViewModel Orphan, RecordingOrphanScanner OrphanScanner) CreateWithCleanup()
    {
        var cache = new CountingCacheCleaner();
        var orphanScanner = new RecordingOrphanScanner();
        var cacheVm = new CacheCleanupViewModel(cache, CacheCatalog.Build(CacheRoots.FromEnvironment()), _dialogs, _log);
        var orphanVm = new OrphanScanViewModel(orphanScanner, new FakeLeftoverCleaner(), _files, _external, _dialogs, _log);
        var services = new AppServices
        {
            Theme = _theme, Windows = _windows, Log = _log, Scanner = _scanner, Icons = _icons, Uninstall = _coordinator,
            Leftovers = _leftovers, Dialogs = _dialogs, Insights = _insights, Keep = _keep, Bloat = _bloat, Winget = _winget,
            Defender = _defender, External = _external, WindowProbe = _windowProbe, Files = _files,
            Guard = new SafetyGuard(TestRoots), ForceRemove = _forceRemove, Batch = _batch, Tracker = _tracker,
            CacheCleanup = cacheVm, OrphanScan = orphanVm,
        };
        return (new MainViewModel(services, isElevated: true), cache, orphanVm, orphanScanner);
    }

    [Fact]
    public void Opening_the_Cleanup_tab_measures_the_caches_once_and_deletes_nothing()
    {
        var (vm, cache, _, _) = CreateWithCleanup();

        vm.SelectedTabIndex = 4;
        vm.SelectedTabIndex = 0;
        vm.SelectedTabIndex = 4;

        Assert.Equal(1, cache.Scans);
        Assert.True(vm.CacheCleanup!.Rows.All(r => r.IsMeasured));
    }

    [Fact]
    public void The_Cleanup_tab_does_not_show_the_programs_search_and_uninstall_header()
    {
        var (vm, _, _, _) = CreateWithCleanup();

        vm.SelectedTabIndex = 4;

        Assert.False(vm.IsProgramsTab);
    }

    [Fact]
    public async Task Each_program_scan_hands_the_installed_programs_to_the_old_leftover_search()
    {
        var (vm, _, orphan, scanner) = CreateWithCleanup();
        _scanner.Result = new ScanResult(new[] { Program("Discord"), Program("Steam") }, 2, 0, 0, 0, 0);

        await vm.ScanCommand.ExecuteAsync(null);
        await orphan.FindAsync();

        Assert.Equal(new[] { "Discord", "Steam" }, scanner.Programs!.Select(p => p.Name).OrderBy(n => n));
    }
}
