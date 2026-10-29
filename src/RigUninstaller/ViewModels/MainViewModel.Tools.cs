using System.ComponentModel;
using System.Text;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>Right-click actions, tags, CSV export, winget, Defender and target mode.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyDictionary<string, WingetPackage> _wingetMatches = new Dictionary<string, WingetPackage>();

    private bool _hasWingetData;

    /// <summary>True once "Check winget" ran: only then does the winget column have something to show.</summary>
    public bool HasWingetData
    {
        get => _hasWingetData;
        private set => SetProperty(ref _hasWingetData, value);
    }

    public RelayCommand ToggleKeepCommand { get; private set; } = null!;

    public RelayCommand OpenFolderCommand { get; private set; } = null!;

    public RelayCommand OpenRegistryCommand { get; private set; } = null!;

    public RelayCommand SearchOnlineCommand { get; private set; } = null!;

    public RelayCommand EditBloatwareCommand { get; private set; } = null!;

    public RelayCommand TickBloatCommand { get; private set; } = null!;

    public AsyncRelayCommand DefenderScanCommand { get; private set; } = null!;

    public AsyncRelayCommand ExportCsvCommand { get; private set; } = null!;

    public AsyncRelayCommand WingetCheckCommand { get; private set; } = null!;

    public AsyncRelayCommand WingetUninstallCommand { get; private set; } = null!;

    public AsyncRelayCommand WingetUpdateCommand { get; private set; } = null!;

    /// <summary>Raised after target mode found a program, so the grid can scroll to it.</summary>
    public event Action<ProgramRowViewModel>? RowRevealRequested;

    private void InitializeTools()
    {
        ToggleKeepCommand = new RelayCommand(_ => ToggleKeep(), _ => SelectedRow is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedRow is not null);
        OpenRegistryCommand = new RelayCommand(_ => Safely("open the registry key", () => _services.External.OpenRegistryKey(SelectedRow!.Program.RegistryKeyPath)), _ => SelectedRow is not null);
        SearchOnlineCommand = new RelayCommand(_ => Safely("open the web search", () => _services.External.OpenWebAddress(SearchUrl.For(SelectedRow!.Program))), _ => SelectedRow is not null);
        TickBloatCommand = new RelayCommand(_ => TickBloat(), _ => Rows.Any(r => r.IsBloat && !r.IsKept));
        EditBloatwareCommand = new RelayCommand(_ => Safely("open the bloatware list", () => _services.External.OpenFile(_services.Bloat.FilePath)));
        DefenderScanCommand = new AsyncRelayCommand(_ => DefenderScanAsync(), _ => CanActOnSelection());
        ExportCsvCommand = new AsyncRelayCommand(_ => ExportCsvAsync(), _ => Rows.Count > 0);
        WingetCheckCommand = new AsyncRelayCommand(_ => WingetCheckAsync(), _ => !IsUninstalling && !IsScanning);
        WingetUninstallCommand = new AsyncRelayCommand(_ => WingetActAsync(uninstall: true), _ => CanUseWinget(requireNotKept: true));
        WingetUpdateCommand = new AsyncRelayCommand(_ => WingetActAsync(uninstall: false), _ => CanUseWinget(requireNotKept: false) && SelectedRow?.WingetText.StartsWith("Update", StringComparison.Ordinal) == true);
    }

    /// <summary>Re-checks every command that depends on the selected row or on what is running.</summary>
    private void RaiseSelectionCommands()
    {
        UninstallCommand?.RaiseCanExecuteChanged();
        ForceRemoveCommand?.RaiseCanExecuteChanged();
        ToggleKeepCommand?.RaiseCanExecuteChanged();
        OpenFolderCommand?.RaiseCanExecuteChanged();
        OpenRegistryCommand?.RaiseCanExecuteChanged();
        SearchOnlineCommand?.RaiseCanExecuteChanged();
        DefenderScanCommand?.RaiseCanExecuteChanged();
        WingetCheckCommand?.RaiseCanExecuteChanged();
        WingetUninstallCommand?.RaiseCanExecuteChanged();
        WingetUpdateCommand?.RaiseCanExecuteChanged();
        BatchUninstallCommand?.RaiseCanExecuteChanged();
        ExportCsvCommand?.RaiseCanExecuteChanged();
    }

    private bool CanUseWinget(bool requireNotKept) =>
        CanActOnSelection()
        && SelectedRow is { } row
        && _wingetMatches.ContainsKey(row.Program.RegistryKeyPath)
        && (!requireNotKept || !row.IsKept);

    /// <summary>Fills in signature, last-used date and the broken-entry check, a few programs at a time off the UI thread.</summary>
    private async Task AnalyzeAsync()
    {
        var pending = _rowCache.Values.Where(r => r.Insight is null).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            var results = await Task.Run(() => pending.Select(row => (Row: row, Insight: AnalyzeOne(row.Program))).ToList());
            foreach (var (row, insight) in results)
            {
                row.Insight = insight;
            }

            if (SortColumn == SortColumn.LastUsed)
            {
                RebuildRows();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Program analysis failed", ex);
            return;
        }

        _log.Info($"Program analysis done: {pending.Count(r => r.IsOrphan)} broken entries, {pending.Count(r => r.IsUnsigned)} unsigned uninstallers.");
    }

    private ProgramInsight? AnalyzeOne(InstalledProgram program)
    {
        try
        {
            return _services.Insights.Analyze(program);
        }
        catch (Exception ex)
        {
            // One odd program must not hide the details of all the others.
            _log.Warn($"Could not analyse {program.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Ticks every shown program that matches the bloatware list, so one click can start a batch for them.</summary>
    private void TickBloat()
    {
        foreach (var row in Rows.Where(r => r.IsBloat && !r.IsKept))
        {
            row.IsChecked = true;
        }
    }

    private void ToggleKeep()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var keep = !row.IsKept;
        _services.Keep.SetKept(row.Program, keep);
        row.IsKept = keep;
        if (keep && row.IsChecked)
        {
            row.IsChecked = false;
        }

        RaiseSelectionCommands();
        SetNote(keep ? $"{row.Name} is protected: it cannot be uninstalled until you untick Keep." : $"{row.Name} is no longer protected.");
    }

    private void OpenFolder()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var folder = ProgramFolders.For(row.Program, _services.Guard).FirstOrDefault(_services.Files.Exists);
        if (folder is null)
        {
            _dialogs.ShowMessage("No install folder", $"{row.Name} does not record where it is installed, or the folder is gone.", isError: false);
            return;
        }

        Safely("open the folder", () => _services.External.OpenFolder(folder));
    }

    private async Task DefenderScanAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (!_services.Defender.IsAvailable)
        {
            _dialogs.ShowMessage("Microsoft Defender not found", "Defender's command line tool (MpCmdRun.exe) was not found on this PC.", isError: false);
            return;
        }

        using var cts = new CancellationTokenSource();
        _uninstallCts = cts;
        BeginBusy($"Scanning {row.Name} with Microsoft Defender...", "This can take a minute for big programs.", canStop: true);
        DefenderResult result;
        try
        {
            result = await _services.Defender.ScanProgramAsync(row.Program, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new DefenderResult(DefenderStatus.Failed, "The scan was stopped.");
        }
        finally
        {
            _uninstallCts = null;
            EndBusy();
        }

        _dialogs.ShowMessage(
            result.Status switch
            {
                DefenderStatus.Clean => "No threats found",
                DefenderStatus.ThreatsFound => "Threats found",
                _ => "Scan not done",
            },
            result.Message,
            isError: result.Status == DefenderStatus.ThreatsFound);
    }

    private async Task ExportCsvAsync()
    {
        var path = _dialogs.AskSavePath("Export the program list", "installed-programs.csv", "CSV file (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        var csv = CsvExporter.Build(Rows);
        try
        {
            // The byte order mark makes Excel read accented names correctly.
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"CSV export to {path} failed", ex);
            _dialogs.ShowMessage("Could not save the file", ex.Message, isError: true);
            return;
        }

        _log.Info($"Exported {Rows.Count} programs to {path}");
        SetNote($"Exported {Rows.Count} programs to {path}");
    }

    private async Task WingetCheckAsync()
    {
        if (!_services.Winget.IsAvailable)
        {
            _dialogs.ShowMessage("winget not found", "The Windows Package Manager (winget) is not installed. Install \"App Installer\" from the Microsoft Store to use it.", isError: false);
            return;
        }

        BeginBusy("Asking winget...", "winget compares the installed programs with its catalogue. This can take a few seconds.", canStop: false);
        try
        {
            var packages = await _services.Winget.ListAsync(CancellationToken.None);
            _wingetMatches = WingetMatcher.Match(_programs, packages);
            HasWingetData = true;
            foreach (var row in _rowCache.Values)
            {
                row.WingetText = _wingetMatches.TryGetValue(row.Program.RegistryKeyPath, out var package)
                    ? (package.Available is { } next ? $"Update {package.Version} to {next}" : "winget")
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            _log.Error("winget check failed", ex);
            _dialogs.ShowMessage("winget check failed", ex.Message, isError: true);
            return;
        }
        finally
        {
            EndBusy();
            RaiseSelectionCommands();
        }

        var updates = _rowCache.Values.Count(r => r.WingetText.StartsWith("Update", StringComparison.Ordinal));
        SetNote($"winget knows {_wingetMatches.Count} of your programs, {updates} with an update.");
    }

    private async Task WingetActAsync(bool uninstall)
    {
        if (SelectedRow is not { } row || !_wingetMatches.TryGetValue(row.Program.RegistryKeyPath, out var package))
        {
            return;
        }

        var verb = uninstall ? "Uninstall" : "Update";
        if (!_dialogs.Confirm(
                $"{verb} {row.Name} with winget?",
                $"winget will run \"{verb.ToLowerInvariant()}\" for {package.Id}. It may open its own windows.",
                verb,
                "Cancel",
                yesIsSafe: !uninstall))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _uninstallCts = cts;
        BeginBusy($"{verb} {row.Name} with winget...", "Waiting for winget to finish.", canStop: true);

        var result = uninstall
            ? await _services.Winget.UninstallAsync(package.Id, cts.Token)
            : await _services.Winget.UpgradeAsync(package.Id, cts.Token);

        var program = row.Program;
        var removed = false;
        LeftoverFlowResult? leftover = null;
        if (result.Started)
        {
            await ScanAsync();
            removed = uninstall && WasRemoved(program);
            if (removed)
            {
                leftover = await FindLeftoversAsync(program, false, cts.Token);
            }
        }

        _uninstallCts = null;
        EndBusy();

        if (!result.Started)
        {
            _dialogs.ShowMessage($"winget could not {verb.ToLowerInvariant()} {program.Name}", result.Error ?? "It did not start.", isError: true);
            return;
        }

        if (leftover is { Failures.Count: > 0 })
        {
            ShowLeftoverFailures(program, leftover.Failures);
        }

        SetNote(uninstall
            ? (removed ? $"{program.Name} was removed with winget. {leftover?.Summary}".Trim() : $"winget finished (exit code {result.ExitCode}), but {program.Name} is still listed.")
            : $"winget finished updating {program.Name} (exit code {result.ExitCode}).");
    }

    /// <summary>Shown after a plain click on the crosshair button: the next click picks the window.</summary>
    public void AnnounceTargetMode() =>
        SetNote("Target mode: now click the window you want to find. Press Esc to cancel.");

    /// <summary>
    /// Target mode: the user drops the crosshair on any window. The program that owns it is found and selected.
    /// </summary>
    public void LocateWindowUnderCursor()
    {
        var exe = _services.WindowProbe.GetProcessPathAtCursor();
        if (exe is null)
        {
            SetNote("Target mode: that window is not a program I can identify (it may be this app, or Windows).");
            return;
        }

        var program = ProgramLocator.Find(exe, _programs, _services.Guard);
        if (program is null)
        {
            SetNote($"Target mode: {Path.GetFileName(exe)} does not belong to an installed program in the list.");
            _log.Info($"Target mode: no installed program for {exe}");
            return;
        }

        if (SearchText.Length > 0 && !_rows.Any(r => r.Program == program))
        {
            SearchText = string.Empty;
        }

        var row = _rowCache[program];
        SelectedRow = row;
        RowRevealRequested?.Invoke(row);
        SetNote($"Target mode: {Path.GetFileName(exe)} belongs to {program.Name}.");
        _log.Info($"Target mode: {exe} -> {program.Name}");
    }

    /// <summary>Runs an external action; a failure becomes a message instead of a crash.</summary>
    private void Safely(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            _log.Error($"Could not {what}", ex);
            _dialogs.ShowMessage($"Could not {what}", ex.Message, isError: true);
        }
    }
}
