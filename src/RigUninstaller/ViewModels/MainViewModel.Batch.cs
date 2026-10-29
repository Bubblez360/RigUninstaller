using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>One line of the batch progress list.</summary>
public sealed class BatchItemViewModel : ViewModelBase
{
    private BatchItemStatus _status = BatchItemStatus.Waiting;
    private string _message = string.Empty;

    public BatchItemViewModel(InstalledProgram program)
    {
        Program = program;
    }

    public InstalledProgram Program { get; }

    public string Name => Program.Name;

    public BatchItemStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => _status switch
    {
        BatchItemStatus.Waiting => "Waiting",
        BatchItemStatus.Running => "Running",
        BatchItemStatus.Done => "Done",
        BatchItemStatus.Failed => "Failed",
        BatchItemStatus.Skipped => "Skipped",
        _ => "Not run",
    };

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public void Update(BatchItemStatus status, string message)
    {
        Status = status;
        Message = message;
    }
}

/// <summary>Batch uninstall and force remove.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<BatchItemViewModel> _batchItems = Array.Empty<BatchItemViewModel>();
    private int _checkedCount;

    public AsyncRelayCommand BatchUninstallCommand { get; private set; } = null!;

    public AsyncRelayCommand ForceRemoveCommand { get; private set; } = null!;

    public RelayCommand ClearChecksCommand { get; private set; } = null!;

    /// <summary>The programs of a running batch with their state.</summary>
    public IReadOnlyList<BatchItemViewModel> BatchItems
    {
        get => _batchItems;
        private set
        {
            if (SetProperty(ref _batchItems, value))
            {
                OnPropertyChanged(nameof(ShowBatchItems));
            }
        }
    }

    public bool ShowBatchItems => _batchItems.Count > 0;

    /// <summary>How many programs are ticked for a batch uninstall.</summary>
    public int CheckedCount
    {
        get => _checkedCount;
        private set
        {
            if (SetProperty(ref _checkedCount, value))
            {
                OnPropertyChanged(nameof(HasChecked));
                OnPropertyChanged(nameof(BatchButtonText));
            }
        }
    }

    public bool HasChecked => _checkedCount > 0;

    public string BatchButtonText => $"Uninstall {_checkedCount} ticked";

    private void InitializeBatch()
    {
        BatchUninstallCommand = new AsyncRelayCommand(_ => BatchUninstallAsync(), _ => HasChecked && !IsScanning && !IsUninstalling);
        ForceRemoveCommand = new AsyncRelayCommand(_ => ForceRemoveAsync(), _ => CanActOnSelection() && SelectedRow is { IsKept: false });
        ClearChecksCommand = new RelayCommand(_ => ClearChecks(), _ => HasChecked);
    }

    private void UpdateCheckedCount()
    {
        CheckedCount = _rowCache.Values.Count(r => r.IsChecked);
        BatchUninstallCommand?.RaiseCanExecuteChanged();
        ClearChecksCommand?.RaiseCanExecuteChanged();
    }

    private void ClearChecks()
    {
        foreach (var row in _rowCache.Values.Where(r => r.IsChecked))
        {
            row.IsChecked = false;
        }
    }

    private async Task BatchUninstallAsync()
    {
        var ticked = _rowCache.Values.Where(r => r.IsChecked).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var protectedRows = ticked.Where(r => r.IsKept).ToList();
        var programs = ticked.Where(r => !r.IsKept).Select(r => r.Program).ToList();
        if (programs.Count == 0)
        {
            _dialogs.ShowMessage("Nothing to uninstall", "Every ticked program is marked Keep.", isError: false);
            return;
        }

        // Risky ones (drivers, runtimes, security software) are named before the batch can start.
        var risky = ticked.Where(r => !r.IsKept && r.HasRisk).ToList();
        if (risky.Count > 0 && !_dialogs.Confirm(
                risky.Count == 1 ? "1 ticked program is risky to remove" : $"{risky.Count} ticked programs are risky to remove",
                string.Join("\n\n", risky.Select(r => $"{r.Name}\n{r.RiskReason}")),
                "Continue anyway",
                "Cancel",
                yesIsSafe: false))
        {
            _log.Info($"Batch uninstall cancelled after the risk warning ({risky.Count} risky program(s)).");
            return;
        }

        var options = _dialogs.ConfirmBatch(programs, _services.Defender.IsAvailable);
        if (options is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _uninstallCts = cts;
        BatchItems = programs.Select(p => new BatchItemViewModel(p)).ToList();
        BeginBusy($"Uninstalling {programs.Count} programs...", "One after another. If a window opens, follow it there.", canStop: true);

        IReadOnlyList<BatchItemResult> results;
        try
        {
            // Reports arrive on this (UI) thread, so a plain callback is enough.
            results = await _services.Batch.RunAsync(programs, options, new DirectProgress<BatchProgress>(ApplyBatchProgress), cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error("Batch uninstall failed unexpectedly", ex);
            _uninstallCts = null;
            BatchItems = Array.Empty<BatchItemViewModel>();
            EndBusy();
            _dialogs.ShowMessage("Batch uninstall failed", ex.Message, isError: true);
            return;
        }

        await ScanAsync();
        var removed = results.Where(r => r.Status == BatchItemStatus.Done && WasRemoved(r.Program)).Select(r => r.Program).ToList();
        foreach (var program in removed)
        {
            // The batch's single restore point covers the leftover cleanup too.
            var leftover = await FindLeftoversAsync(program, options.CreateRestorePoint, cts.Token);
            if (leftover is { Failures.Count: > 0 })
            {
                ShowLeftoverFailures(program, leftover.Failures);
            }
        }

        _uninstallCts = null;
        BatchItems = Array.Empty<BatchItemViewModel>();
        EndBusy();
        ReportBatch(results, removed.Count, protectedRows.Count);
    }

    private void ApplyBatchProgress(BatchProgress progress)
    {
        BatchItems.FirstOrDefault(i => i.Program == progress.Program)?.Update(progress.Status, progress.Message);
        BusyText = $"Uninstalling {progress.Index + 1} of {progress.Total}: {progress.Program.Name}";
    }

    private void ReportBatch(IReadOnlyList<BatchItemResult> results, int removed, int keptSkipped)
    {
        var failed = results.Where(r => r.Status == BatchItemStatus.Failed).ToList();
        var notRun = results.Count(r => r.Status is BatchItemStatus.Skipped or BatchItemStatus.Cancelled);
        SetNote($"Batch: {removed} removed, {failed.Count} failed, {notRun + keptSkipped} skipped.");

        if (failed.Count > 0)
        {
            _dialogs.ShowMessage(
                "Some programs could not be uninstalled",
                string.Join("\n\n", failed.Select(f => $"{f.Program.Name}\n    {f.Message}"
                    + (f.Message.StartsWith(ProcessRunner.MissingFilePrefix, StringComparison.Ordinal)
                        ? "\n    Its files are already gone. Select it and use Force remove (right-click) to clean up the leftover entry."
                        : string.Empty))),
                isError: true);
        }

        foreach (var row in _rowCache.Values.Where(r => r.IsChecked && results.Any(x => x.Program == r.Program && x.Status == BatchItemStatus.Done)))
        {
            row.IsChecked = false;
        }
    }

    private async Task ForceRemoveAsync()
    {
        if (SelectedRow is not { IsKept: false } row)
        {
            return;
        }

        var program = row.Program;
        using var cts = new CancellationTokenSource();
        _uninstallCts = cts;
        BeginBusy($"Force removing {program.Name}...", "Removing the entry and the install folder. Nothing else is touched.", canStop: false);

        ForceRemoveOutcome outcome;
        try
        {
            var others = _programs.Where(p => p.RegistryKeyPath != program.RegistryKeyPath).ToList();
            outcome = await _services.ForceRemove.RunAsync(program, others, false, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error($"Force remove of {program.Name} failed unexpectedly", ex);
            outcome = new ForceRemoveOutcome(ForceRemoveStatus.Failed, ex.Message);
        }

        LeftoverFlowResult? leftover = null;
        if (outcome.Status == ForceRemoveStatus.Done)
        {
            await ScanAsync();
            if (WasRemoved(program))
            {
                leftover = await FindLeftoversAsync(program, outcome.RestorePointCreated, cts.Token);
            }
        }

        _uninstallCts = null;
        EndBusy();

        switch (outcome.Status)
        {
            case ForceRemoveStatus.Done:
                if (leftover is { Failures.Count: > 0 })
                {
                    ShowLeftoverFailures(program, leftover.Failures);
                }

                SetNote($"{program.Name} was force removed.{(leftover is null ? string.Empty : " " + leftover.Summary)}");
                break;
            case ForceRemoveStatus.Cancelled:
                SetNote("Force remove cancelled.");
                break;
            default:
                _dialogs.ShowMessage($"Could not force remove {program.Name}", outcome.Message, isError: true);
                break;
        }
    }

    /// <summary>Progress that runs its callback at once on the calling thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public DirectProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }
}
