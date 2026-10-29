using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using RigUninstaller.Views;

namespace RigUninstaller;

public partial class App : Application
{
    private IActionLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var log = new FileActionLog(AppPaths.LogDirectory);
        _log = log;
        DispatcherUnhandledException += OnUnhandledException;

        var isElevated = IsRunningAsAdministrator();
        log.Info($"App started. Administrator: {isElevated}");

        // What the user chose last time wins; with nothing saved, the app follows Windows.
        var settingsStore = new SettingsStore(Path.Combine(AppPaths.DataDirectory, "settings.json"), log);
        var theme = new ThemeService();
        theme.Apply(settingsStore.Load().Dark ?? ThemeService.DetectSystemDark());

        var roots = SafetyGuardRoots.FromEnvironment();
        var guard = new SafetyGuard(roots);
        var files = new FileSystemProbe();
        var registryProbe = new RegistryProbe();
        var powerShell = new PowerShellRunner();
        var console = new ConsoleRunner();
        var processRunner = new ProcessRunner();
        var external = new ExternalActions();
        var journal = new BackupJournal(Path.Combine(AppPaths.BackupDirectory, "journal.jsonl"));

        var restorePoints = new RestorePointService(powerShell, log);
        var defender = new DefenderScanner(console, guard, files, log, DefenderScanner.FindTool());
        var dialogs = new DialogService(defender);
        var keep = new KeepStore(Path.Combine(AppPaths.DataDirectory, "keep.json"), log);
        var bloat = new BloatwareList(Path.Combine(AppPaths.DataDirectory, "bloatware.json"), log);
        var traces = new TraceStore(Path.Combine(AppPaths.DataDirectory, "Traces"), files, registryProbe, guard, log);

        var uninstall = new UninstallCoordinator(
            processRunner,
            new RunningProcessFinder(new ProcessSnapshot(), guard),
            restorePoints,
            dialogs,
            log,
            defender,
            journal);
        var cleaner = new LeftoverCleaner(new RecycleBin(), new RegistryTool(), journal, files, guard, log, AppPaths.BackupDirectory);
        var leftoverScanner = new LeftoverScanner(files, registryProbe, guard, roots, AppContext.BaseDirectory, log);
        var leftovers = new LeftoverCoordinator(leftoverScanner, cleaner, restorePoints, dialogs, log, journal, traces);
        var cacheRoots = CacheRoots.FromEnvironment();
        var startupManager = new StartupManager(new StartupRegistry(), powerShell, journal, log);

        var services = new AppServices
        {
            Theme = theme,
            Windows = new WindowService(log),
            Log = log,
            Scanner = new ProgramScanner(new RegistryEntryReader(log), log),
            Icons = new IconProvider(),
            Uninstall = uninstall,
            Leftovers = leftovers,
            Dialogs = dialogs,
            Insights = new ProgramInsightService(new SignatureChecker(), new LastUsedFinder(files, new FileTimes()), files),
            Keep = keep,
            Bloat = bloat,
            Winget = new WingetService(console, processRunner, log, WingetService.DefaultPath),
            Defender = defender,
            External = external,
            WindowProbe = new WindowProbe(),
            Files = files,
            Guard = guard,
            ForceRemove = new ForceRemoveCoordinator(cleaner, restorePoints, dialogs, keep, files, guard, log, journal),
            Batch = new BatchCoordinator(uninstall, restorePoints, dialogs, keep, log, journal),
            Tracker = new InstallTracker(new SnapshotSource(roots, guard), traces, log),
            Settings = settingsStore,
            Startup = new StartupViewModel(startupManager, dialogs, log, new CommandIconProvider()),
            Undo = new UndoViewModel(new UndoService(journal, console, powerShell, files, external, startupManager, log), dialogs),
            Store = new StoreAppsViewModel(new StoreAppService(powerShell, log), restorePoints, dialogs, log, new StoreIconProvider()),
            DiskUsage = new DiskUsageViewModel(new FolderSizer(), guard, files, log, new DriveSource(), new FolderBreakdown(), external),
            CacheCleanup = new CacheCleanupViewModel(
                new CacheCleaner(new CacheGuard(cacheRoots), log), CacheCatalog.Build(cacheRoots), dialogs, log),
            OrphanScan = new OrphanScanViewModel(
                new OrphanFolderScanner(
                    OrphanFolderScanner.DefaultRoots(), guard, new FolderSizer(), () => DateTime.UtcNow,
                    () => System.Diagnostics.Process.GetProcesses().Select(p => p.ProcessName).ToList(),
                    new OrphanScanOptions(), log),
                cleaner, files, external, dialogs, log),
        };

        var viewModel = new MainViewModel(services, isElevated);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        viewModel.ScanCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("App closed.");
        base.OnExit(e);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled error", e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the log file.",
            "RigUninstaller",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
