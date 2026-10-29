using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>Everything the main window needs, built once at start-up. Tests fill it with fakes.</summary>
public sealed class AppServices
{
    public required IThemeService Theme { get; init; }

    public required IWindowService Windows { get; init; }

    public required IActionLog Log { get; init; }

    public required IProgramScanner Scanner { get; init; }

    public required IIconProvider Icons { get; init; }

    public required IUninstallCoordinator Uninstall { get; init; }

    public required ILeftoverCoordinator Leftovers { get; init; }

    public required IDialogService Dialogs { get; init; }

    public required IProgramInsightService Insights { get; init; }

    public required IKeepStore Keep { get; init; }

    public required IBloatwareList Bloat { get; init; }

    public required IWingetService Winget { get; init; }

    public required IDefenderScanner Defender { get; init; }

    public required IExternalActions External { get; init; }

    public required IWindowProbe WindowProbe { get; init; }

    public required IFileSystemProbe Files { get; init; }

    public required SafetyGuard Guard { get; init; }

    public required IForceRemoveCoordinator ForceRemove { get; init; }

    public required IBatchCoordinator Batch { get; init; }

    public required IInstallTracker Tracker { get; init; }

    /// <summary>Remembers theme, sort order and window position. Optional: tests leave it out.</summary>
    public ISettingsStore? Settings { get; init; }

    public StartupViewModel? Startup { get; init; }

    public UndoViewModel? Undo { get; init; }

    public StoreAppsViewModel? Store { get; init; }

    public DiskUsageViewModel? DiskUsage { get; init; }

    /// <summary>Cleanup tab, "Temp and cache".</summary>
    public CacheCleanupViewModel? CacheCleanup { get; init; }

    /// <summary>Cleanup tab, "Old leftovers".</summary>
    public OrphanScanViewModel? OrphanScan { get; init; }
}
