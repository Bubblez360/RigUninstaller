using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Tests;

internal sealed class FakeInsights : IProgramInsightService
{
    public Func<InstalledProgram, ProgramInsight> Handler { get; set; } = _ => new ProgramInsight(
        new SignatureInfo(SignatureState.Signed, "Acme Corp", "ok"),
        new LastUsedInfo(null, string.Empty),
        null);

    public ProgramInsight Analyze(InstalledProgram program) => Handler(program);
}

internal sealed class FakeKeep : IKeepStore
{
    public HashSet<string> Kept { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsKept(InstalledProgram program) => Kept.Contains(program.RegistryKeyPath);

    public void SetKept(InstalledProgram program, bool kept)
    {
        if (kept) Kept.Add(program.RegistryKeyPath); else Kept.Remove(program.RegistryKeyPath);
    }
}

internal sealed class FakeBloat : IBloatwareList
{
    public string FilePath => @"C:\fake\bloatware.json";

    public List<string> Names { get; } = new();

    public int Reloads { get; private set; }

    public bool IsBloat(InstalledProgram program) => Names.Any(n => program.Name.Contains(n, StringComparison.OrdinalIgnoreCase));

    public void Reload() => Reloads++;
}

internal sealed class FakeWinget : IWingetService
{
    public bool IsAvailable { get; set; } = true;

    public List<WingetPackage> Packages { get; } = new();

    public List<string> Calls { get; } = new();

    public ProcessRunResult RunResult { get; set; } = new(true, 0, false, null);

    public Task<IReadOnlyList<WingetPackage>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WingetPackage>>(Packages);

    public Task<ProcessRunResult> UninstallAsync(string id, CancellationToken cancellationToken)
    {
        Calls.Add("uninstall:" + id);
        return Task.FromResult(RunResult);
    }

    public Task<ProcessRunResult> UpgradeAsync(string id, CancellationToken cancellationToken)
    {
        Calls.Add("upgrade:" + id);
        return Task.FromResult(RunResult);
    }
}

internal sealed class FakeDefender : IDefenderScanner
{
    public bool IsAvailable { get; set; } = true;

    public DefenderResult Result { get; set; } = new(DefenderStatus.Clean, "No threats.");

    public int Scans { get; private set; }

    public Task<DefenderResult> ScanProgramAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        Scans++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeExternal : IExternalActions
{
    public List<string> Calls { get; } = new();

    public void OpenFolder(string folder) => Calls.Add("folder:" + folder);

    public void OpenRegistryKey(string fullKeyPath) => Calls.Add("regedit:" + fullKeyPath);

    public void OpenWebAddress(Uri address) => Calls.Add("web:" + address);

    public void OpenFile(string path) => Calls.Add("file:" + path);
}

internal sealed class FakeWindowProbe : IWindowProbe
{
    public string? Path { get; set; }

    public string? GetProcessPathAtCursor() => Path;
}

internal sealed class FakeFiles : IFileSystemProbe
{
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetDirectories(string folder) => Array.Empty<string>();

    public IReadOnlyList<string> GetFiles(string folder) => Array.Empty<string>();

    public bool IsReparsePoint(string path) => false;

    public bool Exists(string path) => Existing.Contains(path);
}

internal sealed class FakeForceRemove : IForceRemoveCoordinator
{
    public Func<InstalledProgram, Task<ForceRemoveOutcome>> Handler { get; set; } =
        _ => Task.FromResult(new ForceRemoveOutcome(ForceRemoveStatus.Done, "done"));

    public List<InstalledProgram> Calls { get; } = new();

    public Task<ForceRemoveOutcome> RunAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms, bool restorePointAlreadyMade, CancellationToken cancellationToken)
    {
        Calls.Add(program);
        return Handler(program);
    }
}

internal sealed class FakeBatch : IBatchCoordinator
{
    public Func<IReadOnlyList<InstalledProgram>, BatchOptions, IProgress<BatchProgress>, Task<IReadOnlyList<BatchItemResult>>>? Handler { get; set; }

    public List<IReadOnlyList<InstalledProgram>> Calls { get; } = new();

    public Task<IReadOnlyList<BatchItemResult>> RunAsync(IReadOnlyList<InstalledProgram> programs, BatchOptions options, IProgress<BatchProgress> progress, CancellationToken cancellationToken)
    {
        Calls.Add(programs);
        if (Handler is not null)
        {
            return Handler(programs, options, progress);
        }

        IReadOnlyList<BatchItemResult> results = programs
            .Select(p => new BatchItemResult(p, BatchItemStatus.Done, "ok", null))
            .ToList();
        return Task.FromResult(results);
    }
}

internal sealed class FakeTracker : IInstallTracker
{
    public bool IsTracking { get; private set; }

    public TrackResult FinishResult { get; set; } = new(Array.Empty<InstalledProgram>(), 0, "nothing");

    public int Begins { get; private set; }

    public Task BeginAsync(IReadOnlyList<InstalledProgram> programsBefore, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Begins++;
        IsTracking = true;
        return Task.CompletedTask;
    }

    public Task<TrackResult> FinishAsync(IReadOnlyList<InstalledProgram> programsNow, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        IsTracking = false;
        return Task.FromResult(FinishResult);
    }

    public void Cancel() => IsTracking = false;
}

/// <summary>Shared fixture: every service of the main window as a fake, and a factory for the view model.</summary>
public abstract class VmTestBase
{
    internal readonly FakeScanner _scanner = new();
    internal readonly FakeTheme _theme = new();
    internal readonly FakeWindows _windows = new();
    internal readonly FakeLog _log = new();
    internal readonly FakeIconProvider _icons = new();
    internal readonly FakeCoordinator _coordinator = new();
    internal readonly FakeLeftoverCoordinator _leftovers = new();
    internal readonly FakeDialogs _dialogs = new();
    internal readonly FakeInsights _insights = new();
    internal readonly FakeKeep _keep = new();
    internal readonly FakeBloat _bloat = new();
    internal readonly FakeWinget _winget = new();
    internal readonly FakeDefender _defender = new();
    internal readonly FakeExternal _external = new();
    internal readonly FakeWindowProbe _windowProbe = new();
    internal readonly FakeFiles _files = new();
    internal readonly FakeForceRemove _forceRemove = new();
    internal readonly FakeBatch _batch = new();
    internal readonly FakeTracker _tracker = new();

    internal MainViewModel Create(StartupViewModel? startup = null, UndoViewModel? undo = null, StoreAppsViewModel? store = null, DiskUsageViewModel? disk = null) =>
        new(
            new AppServices
            {
                Theme = _theme,
                Windows = _windows,
                Log = _log,
                Scanner = _scanner,
                Icons = _icons,
                Uninstall = _coordinator,
                Leftovers = _leftovers,
                Dialogs = _dialogs,
                Insights = _insights,
                Keep = _keep,
                Bloat = _bloat,
                Winget = _winget,
                Defender = _defender,
                External = _external,
                WindowProbe = _windowProbe,
                Files = _files,
                Guard = new SafetyGuard(TestRoots),
                ForceRemove = _forceRemove,
                Batch = _batch,
                Tracker = _tracker,
                Startup = startup,
                Undo = undo,
                Store = store,
                DiskUsage = disk,
            },
            isElevated: true);

    internal static readonly SafetyGuardRoots TestRoots = new(
        @"C:\", @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
        @"C:\Users\Tester", @"C:\Users\Tester\AppData\Roaming", @"C:\Users\Tester\AppData\Local");

    internal static InstalledProgram Program(
        string name,
        string? publisher = null,
        long? sizeKb = null,
        DateTime? date = null,
        string? version = null,
        string? location = null) =>
        new()
        {
            Name = name,
            Publisher = publisher,
            EstimatedSizeKb = sizeKb,
            InstallDate = date,
            Version = version,
            InstallLocation = location,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
            KeyName = name,
        };

    internal async Task<MainViewModel> ScannedAsync(params InstalledProgram[] programs)
    {
        _scanner.Result = new ScanResult(programs, programs.Length, 0, 0, 0, 0);
        var vm = Create();
        await vm.ScanCommand.ExecuteAsync(null);
        return vm;
    }

    internal static string[] RowNames(MainViewModel vm) => vm.Rows.Select(r => r.Name).ToArray();
}
