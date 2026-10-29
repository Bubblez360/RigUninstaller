using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>Main window logic: scan, search, sort, selection, uninstall and the status bar summary.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private const string WaitingDetail = "Waiting for the uninstaller to finish. If a window opened, follow it there.";
    private const string LeftoverDetail = "Checking Program Files, AppData, ProgramData, the Start Menu and the registry. Nothing is deleted without your choice.";

    private readonly AppServices _services;
    private readonly IThemeService _theme;
    private readonly IActionLog _log;
    private readonly IProgramScanner _scanner;
    private readonly IIconProvider _icons;
    private readonly IUninstallCoordinator _coordinator;
    private readonly ILeftoverCoordinator _leftovers;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<InstalledProgram> _programs = Array.Empty<InstalledProgram>();
    private Dictionary<InstalledProgram, ProgramRowViewModel> _rowCache = new();
    private IReadOnlyList<ProgramRowViewModel> _rows = Array.Empty<ProgramRowViewModel>();
    private ProgramRowViewModel? _selectedRow;
    private string _searchText = string.Empty;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortDescending;
    private string _statusText = "Ready.";
    private string? _scanError;
    private string? _note;
    private bool _isScanning;
    private bool _isUninstalling;
    private string _busyText = string.Empty;
    private string _busyDetail = string.Empty;
    private bool _canStopWaiting;
    private int _selectedTabIndex;
    private CancellationTokenSource? _uninstallCts;
    private UserSettings _settings;

    public MainViewModel(AppServices services, bool isElevated)
    {
        _services = services;
        _theme = services.Theme;
        _log = services.Log;
        _scanner = services.Scanner;
        _icons = services.Icons;
        _coordinator = services.Uninstall;
        _leftovers = services.Leftovers;
        _dialogs = services.Dialogs;
        IsElevated = isElevated;

        // Last run's sort order comes back; an unknown or missing value keeps the default (name, A to Z).
        _settings = services.Settings?.Load() ?? new UserSettings();
        if (Enum.TryParse<SortColumn>(_settings.SortColumn, ignoreCase: true, out var savedColumn) && Enum.IsDefined(savedColumn))
        {
            _sortColumn = savedColumn;
            _sortDescending = _settings.SortDescending;
        }

        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ViewLogCommand = new RelayCommand(_ => services.Windows.ShowLog());
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsUninstalling);
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => SearchText.Length > 0);
        SortCommand = new RelayCommand(SortBy);
        UninstallCommand = new AsyncRelayCommand(_ => UninstallAsync(), _ => CanActOnSelection() && SelectedRow is { IsKept: false });
        StopWaitingCommand = new RelayCommand(_ => _uninstallCts?.Cancel(), _ => IsUninstalling && CanStopWaiting);
        InitializeTools();
        InitializeBatch();
        InitializeTracker();
    }

    public bool IsElevated { get; }

    public string ElevationText => IsElevated ? "Administrator" : "NOT administrator";

    public string ThemeButtonText => _theme.IsDark ? "Light mode" : "Dark mode";

    /// <summary>Every program found by the last scan, unfiltered.</summary>
    public IReadOnlyList<InstalledProgram> Programs
    {
        get => _programs;
        private set => SetProperty(ref _programs, value);
    }

    /// <summary>What the grid shows: Programs after search and sort.</summary>
    public IReadOnlyList<ProgramRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public ProgramRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                RaiseSelectionCommands();
                OnPropertyChanged(nameof(UninstallButtonText));
                OnPropertyChanged(nameof(SelectionSummary));
                OnPropertyChanged(nameof(SelectionDetail));
                OnPropertyChanged(nameof(SelectionRisk));
                OnPropertyChanged(nameof(HasSelectionRisk));
                OnPropertyChanged(nameof(SelectionRiskIsDanger));
            }
        }
    }

    private const int ButtonNameLength = 22;

    /// <summary>The main button names what it will act on, so there is no doubt before the confirmation.</summary>
    public string UninstallButtonText => SelectedRow is { } row
        ? "Uninstall " + (row.Name.Length > ButtonNameLength ? row.Name[..ButtonNameLength].TrimEnd() + "..." : row.Name)
        : "Uninstall";

    public string SelectionSummary => SelectedRow?.Name ?? "Nothing selected";

    public string SelectionDetail => SelectedRow is { } row
        ? string.Join("  ·  ", new[] { row.Version, row.Publisher, row.Program.InstallLocation }.Where(text => !string.IsNullOrWhiteSpace(text)))
        : "Select a program to uninstall it. Tick boxes for a batch. Right-click for more.";

    /// <summary>A warning line under the selection when removing it can lag or crash the PC; empty otherwise.</summary>
    public string SelectionRisk => SelectedRow?.Risk is { } risk ? $"{risk.Label}: {risk.Reason}" : string.Empty;

    public bool HasSelectionRisk => SelectedRow?.HasRisk == true;

    public bool SelectionRiskIsDanger => SelectedRow?.IsDanger == true;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RebuildRows();
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SortColumn SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RaiseListStateChanged();
                RaiseSelectionCommands();
            }
        }
    }

    /// <summary>True while any long action runs; the window shows a card that blocks the list.</summary>
    public bool IsUninstalling
    {
        get => _isUninstalling;
        private set
        {
            if (SetProperty(ref _isUninstalling, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                StopWaitingCommand.RaiseCanExecuteChanged();
                RaiseSelectionCommands();
            }
        }
    }

    /// <summary>Headline of the "working..." card shown while an uninstall runs.</summary>
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    /// <summary>The smaller line under <see cref="BusyText"/>.</summary>
    public string BusyDetail
    {
        get => _busyDetail;
        private set => SetProperty(ref _busyDetail, value);
    }

    /// <summary>True only while an uninstaller is running: that is the one wait the user may give up on.</summary>
    public bool CanStopWaiting
    {
        get => _canStopWaiting;
        private set
        {
            if (SetProperty(ref _canStopWaiting, value))
            {
                StopWaitingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when programs exist but the search matches none of them.</summary>
    public bool HasNoResults => !IsScanning && _programs.Count > 0 && _rows.Count == 0;

    public string NoResultsText => $"No programs match \"{SearchText.Trim()}\".";

    /// <summary>True during the first scan, while there is nothing to show yet.</summary>
    public bool ShowScanningHint => IsScanning && _programs.Count == 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>0 Programs, 1 Disk usage, 2 Store apps, 3 Startup, 4 Cleanup, 5 Undo center. Opening a tab loads it.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(IsProgramsTab));
                ActivateTab(value);
            }
        }
    }

    /// <summary>The search box and the Uninstall button only make sense on the Programs tab.</summary>
    public bool IsProgramsTab => _selectedTabIndex == 0;

    public StartupViewModel? Startup => _services.Startup;

    public UndoViewModel? Undo => _services.Undo;

    public StoreAppsViewModel? Store => _services.Store;

    public DiskUsageViewModel? DiskUsage => _services.DiskUsage;

    public CacheCleanupViewModel? CacheCleanup => _services.CacheCleanup;

    public OrphanScanViewModel? OrphanScan => _services.OrphanScan;

    public RelayCommand ToggleThemeCommand { get; }

    public RelayCommand ViewLogCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Parameter is a <see cref="Models.SortColumn"/> name. Same column flips direction, a new one starts A-Z.</summary>
    public RelayCommand SortCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    /// <summary>Stops waiting for a running uninstaller. The uninstaller itself keeps running.</summary>
    public RelayCommand StopWaitingCommand { get; }

    private bool CanActOnSelection() => SelectedRow is not null && !IsScanning && !IsUninstalling;

    private void ActivateTab(int index)
    {
        ITabViewModel? tab = index switch
        {
            1 => _services.DiskUsage,
            2 => _services.Store,
            3 => _services.Startup,
            4 => _services.CacheCleanup,
            5 => _services.Undo,
            _ => null,
        };
        tab?.Activate();
    }

    private async Task ScanAsync()
    {
        IsScanning = true;
        _scanError = null;
        _note = null;
        StatusText = "Scanning installed programs...";
        try
        {
            _services.Bloat.Reload();

            // Registry reading runs off the UI thread so the window stays responsive.
            var result = await Task.Run(_scanner.Scan);
            SetPrograms(result.Programs);
        }
        catch (Exception ex)
        {
            // Shown in the status bar and logged; the app keeps running.
            _log.Error("Program scan failed", ex);
            _scanError = ex.Message;
            StatusText = $"Scan failed: {ex.Message}";
            IsScanning = false;
            return;
        }

        IsScanning = false;
        UpdateSummary();
        _services.DiskUsage?.SetPrograms(_programs);
        _services.OrphanScan?.SetPrograms(_programs);
        await LoadIconsAsync();
        await AnalyzeAsync();
    }

    private async Task UninstallAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var program = row.Program;
        if (row.IsKept)
        {
            _dialogs.ShowMessage($"{program.Name} is marked Keep", "It is protected from removal. Untick Keep in the right-click menu first if you really want to uninstall it.", isError: false);
            return;
        }

        // The uninstaller file is already gone, so there is nothing to run: offer to clean up the leftover entry instead.
        if (row.OrphanReason == ProgramInsightService.MissingUninstallerReason)
        {
            await OfferEntryCleanupAsync(row, row.OrphanReason);
            return;
        }

        // Drivers, runtimes and security software: say what can go wrong first. The user can still go on.
        if (row.Risk is { } risk && !_dialogs.Confirm(
                risk.Level == RiskLevel.Danger ? $"{program.Name} is risky to remove" : $"{program.Name}: be careful",
                risk.Reason + "\n\nA System Restore point is offered in the next step, so you can go back if something breaks.",
                "Continue anyway",
                "Cancel",
                yesIsSafe: false))
        {
            _log.Info($"{program.Name}: the user cancelled after the risk warning ({risk.Label}).");
            return;
        }

        using var cts = new CancellationTokenSource();
        _uninstallCts = cts;
        BeginBusy($"Uninstalling {program.Name}...", WaitingDetail, canStop: true);

        UninstallOutcome outcome;
        try
        {
            outcome = await _coordinator.RunInteractiveAsync(program, cts.Token);
        }
        catch (Exception ex)
        {
            // Last line of defence: show the problem instead of crashing the app.
            _log.Error($"Uninstall of {program.Name} failed unexpectedly", ex);
            outcome = new UninstallOutcome(UninstallStatus.Failed, ex.Message);
        }

        var removed = false;
        LeftoverFlowResult? leftover = null;
        if (outcome.Started)
        {
            // The registry is the truth: the program is gone when its Uninstall key is gone.
            await ScanAsync();
            removed = WasRemoved(program);
            _log.Info($"{program.Name}: {(removed ? "removed from the program list" : "still listed after the uninstaller ended")}.");

            if (removed && outcome.Status is UninstallStatus.Completed or UninstallStatus.StoppedWaiting)
            {
                leftover = await FindLeftoversAsync(program, outcome.RestorePointCreated, cts.Token);
            }
        }

        _uninstallCts = null;
        EndBusy();

        // Not analysed yet when the button was pressed, and the file turned out to be missing: same offer.
        if (outcome.Status == UninstallStatus.Failed && outcome.Message.StartsWith(ProcessRunner.MissingFilePrefix, StringComparison.Ordinal))
        {
            await OfferEntryCleanupAsync(row, ProgramInsightService.MissingUninstallerReason);
            return;
        }

        ReportOutcome(program, outcome, removed, leftover);
    }

    /// <summary>
    /// An entry whose uninstaller (and usually the whole program) is gone cannot be uninstalled. Say so plainly and
    /// offer the Force remove flow, which backs the registry key up and asks for its own confirmation.
    /// </summary>
    private async Task OfferEntryCleanupAsync(ProgramRowViewModel row, string reason)
    {
        _log.Info($"{row.Name}: {reason} Offering to clean up the leftover entry.");
        var accepted = _dialogs.Confirm(
            $"{row.Name} cannot be uninstalled",
            $"{reason} The program's files are already gone, so this is only a leftover entry in the list.\n\n"
            + "\"Clean up entry\" removes it. A backup of its registry key is saved first, and you can restore it from the Undo center.",
            "Clean up entry",
            "Cancel",
            yesIsSafe: true);
        if (accepted)
        {
            await ForceRemoveAsync();
        }
    }

    private bool WasRemoved(InstalledProgram program) => _programs.All(p => p.RegistryKeyPath != program.RegistryKeyPath);

    private void BeginBusy(string text, string detail, bool canStop)
    {
        IsUninstalling = true;
        BusyText = text;
        BusyDetail = detail;
        CanStopWaiting = canStop;
    }

    private void EndBusy()
    {
        CanStopWaiting = false;
        IsUninstalling = false;
        BusyText = string.Empty;
        BusyDetail = string.Empty;
    }

    /// <summary>Runs the leftover flow; a crash there is shown and logged but never undoes the removal.</summary>
    private async Task<LeftoverFlowResult?> FindLeftoversAsync(InstalledProgram program, bool restorePointAlreadyMade, CancellationToken cancellationToken)
    {
        CanStopWaiting = false;
        BusyText = $"Looking for leftovers of {program.Name}...";
        BusyDetail = LeftoverDetail;
        try
        {
            var others = _programs.Where(p => p.RegistryKeyPath != program.RegistryKeyPath).ToList();
            return await _leftovers.RunAsync(program, others, restorePointAlreadyMade, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Error($"Leftover scan for {program.Name} failed", ex);
            _dialogs.ShowMessage($"Could not check {program.Name} for leftovers", ex.Message, isError: true);
            return null;
        }
    }

    private void ReportOutcome(InstalledProgram program, UninstallOutcome outcome, bool removed, LeftoverFlowResult? leftover)
    {
        if (leftover is { Failures.Count: > 0 })
        {
            ShowLeftoverFailures(program, leftover.Failures);
        }

        var leftoverText = leftover is null ? string.Empty : $" {leftover.Summary}";
        switch (outcome.Status)
        {
            case UninstallStatus.Failed:
                _dialogs.ShowMessage($"Could not uninstall {program.Name}", outcome.Message, isError: true);
                break;

            case UninstallStatus.Cancelled:
                SetNote("Uninstall cancelled.");
                break;

            case UninstallStatus.StoppedWaiting:
                SetNote(removed
                    ? $"{program.Name} was removed.{leftoverText}"
                    : $"Stopped waiting for {program.Name}. Use Rescan when its uninstaller is done.");
                break;

            default:
                if (removed)
                {
                    SetNote((outcome.RebootRequired
                        ? $"{program.Name} was removed. Restart Windows to finish."
                        : $"{program.Name} was removed.") + leftoverText);
                }
                else
                {
                    _dialogs.ShowMessage(
                        $"{program.Name} is still installed",
                        $"{outcome.Message} The program is still in the list, so the uninstall may have been cancelled or may need a restart.",
                        isError: false);
                }

                break;
        }
    }

    private const int MaxFailuresShown = 8;

    private void ShowLeftoverFailures(InstalledProgram program, IReadOnlyList<CleanupFailure> failures)
    {
        var lines = failures.Take(MaxFailuresShown).Select(f => $"{f.Item.Path}\n    {f.Error}").ToList();
        if (failures.Count > MaxFailuresShown)
        {
            lines.Add($"...and {failures.Count - MaxFailuresShown} more (see the log).");
        }

        _dialogs.ShowMessage(
            $"Some leftovers of {program.Name} could not be removed",
            "Everything else was cleaned. These were left as they are:\n\n" + string.Join("\n\n", lines),
            isError: true);
    }

    private void SetNote(string note)
    {
        _note = note;
        UpdateSummary();
    }

    private void SetPrograms(IReadOnlyList<InstalledProgram> programs)
    {
        // Records compare by value, so a program that did not change keeps its row (and its icon and insight).
        var previous = _rowCache;
        _rowCache = programs.ToDictionary(p => p, p => previous.TryGetValue(p, out var row) ? row : new ProgramRowViewModel(p, UpdateCheckedCount));
        foreach (var row in _rowCache.Values)
        {
            row.IsKept = _services.Keep.IsKept(row.Program);
            row.IsBloat = _services.Bloat.IsBloat(row.Program);
        }

        Programs = programs;
        RebuildRows();
        UpdateCheckedCount();
    }

    private void RebuildRows()
    {
        var visible = ProgramListQuery.Apply(_programs, SearchText, SortColumn, SortDescending, p => _rowCache.TryGetValue(p, out var row) ? row.Insight?.LastUsed.When : null);
        var rows = visible.Select(p => _rowCache[p]).ToList();
        Rows = rows;

        if (SelectedRow is not null && !rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }

        RaiseListStateChanged();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (IsScanning || _scanError is not null)
        {
            return;
        }

        var total = _programs.Count;
        var noun = total == 1 ? "program" : "programs";
        var size = SizeFormatter.Format(_rows.Sum(r => r.Program.SizeBytes ?? 0));

        string summary;
        if (SearchText.Trim().Length == 0)
        {
            summary = $"{total} {noun}  |  {size} total";
        }
        else
        {
            // No size for an empty result: "0 B shown" would only be noise.
            summary = _rows.Count == 0
                ? $"0 of {total} {noun}"
                : $"{_rows.Count} of {total} {noun}  |  {size} shown";
        }

        StatusText = _note is null ? summary : $"{summary}  |  {_note}";
    }

    private void SortBy(object? columnName)
    {
        if (columnName is not string text
            || !Enum.TryParse<SortColumn>(text, ignoreCase: true, out var column)
            || !Enum.IsDefined(column))
        {
            return;
        }

        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        RebuildRows();
        SaveSettings(s => s with { SortColumn = SortColumn.ToString(), SortDescending = SortDescending });
    }

    /// <summary>The settings read at start-up (the window uses them to come back where it was).</summary>
    public UserSettings SavedSettings => _settings;

    /// <summary>Remembers where the window was when it closed.</summary>
    public void SaveWindow(System.Windows.Rect bounds, bool maximized) =>
        SaveSettings(s => s with
        {
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = maximized,
        });

    private void SaveSettings(Func<UserSettings, UserSettings> change)
    {
        _settings = change(_settings);
        _services.Settings?.Save(_settings);
    }

    private async Task LoadIconsAsync()
    {
        var pending = _rowCache.Values.Where(r => !r.HasIcon).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            // Icon files are read off the UI thread; the frozen images are assigned back on it.
            var icons = await Task.Run(() => pending.Select(r => _icons.GetIcon(r.Program)).ToList());
            for (var i = 0; i < pending.Count; i++)
            {
                if (icons[i] is { } icon)
                {
                    pending[i].Icon = icon;
                }
            }
        }
        catch (Exception ex)
        {
            // Missing icons are cosmetic: log it and keep the letter tiles.
            _log.Error("Icon loading failed", ex);
            return;
        }

        _log.Info($"Icons loaded: {_rowCache.Values.Count(r => r.HasIcon)} of {_rowCache.Count}");
    }

    private void RaiseListStateChanged()
    {
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(ShowScanningHint));
    }

    private void ToggleTheme()
    {
        _theme.Toggle();
        OnPropertyChanged(nameof(ThemeButtonText));
        _log.Info($"Theme changed to {(_theme.IsDark ? "dark" : "light")}");
        SaveSettings(s => s with { Dark = _theme.IsDark });
    }
}
