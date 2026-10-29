using RigUninstaller.Helpers;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>One line of the cache list: what it is, how big it is, and the tick.</summary>
public sealed class CacheRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _isChecked;
    private bool _isMeasured;
    private long _bytes;
    private int _files;

    public CacheRowViewModel(CacheCategory category, Action onChanged)
    {
        Category = category;
        _onChanged = onChanged;
        _isChecked = category.TickedByDefault;
    }

    public CacheCategory Category { get; }

    public string Name => Category.Name;

    public string Description => Category.Description;

    public string Note => Category.Note ?? string.Empty;

    public bool HasNote => Category.Note is not null;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onChanged();
            }
        }
    }

    public long Bytes => _bytes;

    public bool IsMeasured => _isMeasured;

    /// <summary>"Measuring...", "Empty" or the size.</summary>
    public string SizeText => !_isMeasured ? "Measuring..." : _bytes == 0 ? "Empty" : SizeFormatter.Format(_bytes);

    public string FilesText => _isMeasured && _files > 0 ? $"{_files:N0} files" : string.Empty;

    /// <summary>Nothing to clean (or not measured yet): the tick is dimmed and cannot be used.</summary>
    public bool CanTick => _isMeasured && _bytes > 0;

    public void SetResult(CacheScanResult result)
    {
        _bytes = result.Bytes;
        _files = result.Files;
        _isMeasured = true;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(CanTick));
        _onChanged();
    }

    public void ResetMeasure()
    {
        _isMeasured = false;
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(CanTick));
    }
}

/// <summary>
/// The "Temp and cache" part of the Cleanup tab: shows how much each kind of cache holds, and empties the ticked ones
/// after one confirmation. Nothing is touched before that, and nothing outside the catalog can ever be.
/// </summary>
public sealed class CacheCleanupViewModel : ViewModelBase, ITabViewModel
{
    private readonly ICacheCleaner _cleaner;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private IReadOnlyList<CacheRowViewModel> _rows = Array.Empty<CacheRowViewModel>();
    private string _statusText = "Open this tab to see how much space your temporary files and caches use.";
    private string _resultText = string.Empty;
    private bool _isBusy;
    private bool _scanned;
    private CancellationTokenSource? _cts;

    public CacheCleanupViewModel(ICacheCleaner cleaner, IReadOnlyList<CacheCategory> categories, IDialogService dialogs, IActionLog log)
    {
        _cleaner = cleaner;
        _dialogs = dialogs;
        _log = log;
        _rows = categories.Select(c => new CacheRowViewModel(c, OnSelectionChanged)).ToList();
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsBusy && Selected.Count > 0);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
    }

    public IReadOnlyList<CacheRowViewModel> Rows => _rows;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>What the last cleanup did, for example "Freed 2.1 GB. 12 files were in use and left alone."</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => ResultText.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CleanCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The ticked categories that actually hold something.</summary>
    private IReadOnlyList<CacheRowViewModel> Selected => _rows.Where(r => r.IsChecked && r.Bytes > 0).ToList();

    public long SelectedBytes => Selected.Sum(r => r.Bytes);

    public string SelectedText => Selected.Count == 0 ? "Nothing selected" : $"{SizeFormatter.Format(SelectedBytes)} selected";

    public string CleanButtonText => Selected.Count == 0 ? "Clean" : $"Clean {SizeFormatter.Format(SelectedBytes)}";

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand CleanCommand { get; }

    public RelayCommand StopCommand { get; }

    public void Activate()
    {
        if (!_scanned)
        {
            ScanCommand.Execute(null);
        }
    }

    public async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = "Measuring...";
        foreach (var row in _rows)
        {
            row.ResetMeasure();
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var results = await _cleaner.ScanAsync(_rows.Select(r => r.Category).ToList(), cts.Token);
            foreach (var result in results)
            {
                _rows.FirstOrDefault(r => r.Category.Id == result.CategoryId)?.SetResult(result);
            }

            _scanned = true;
            StatusText = $"{SizeFormatter.Format(_rows.Sum(r => r.Bytes))} found in total. Tick what you want to clean.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
        }
        catch (Exception ex)
        {
            _log.Error("Could not measure the caches", ex);
            StatusText = $"Could not measure the caches: {ex.Message}";
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            OnSelectionChanged();
        }
    }

    private async Task CleanAsync()
    {
        var chosen = Selected;
        if (chosen.Count == 0)
        {
            return;
        }

        var list = string.Join("\n", chosen.Select(r => $"{r.Name}: {r.SizeText}"));
        if (!_dialogs.Confirm(
                $"Delete {SizeFormatter.Format(chosen.Sum(r => r.Bytes))} of temporary files?",
                list + "\n\nThey are deleted for good, not sent to the Recycle Bin, because Windows and your programs make them again. "
                + "Files that are in use are skipped. This cannot be undone.",
                "Delete",
                "Cancel",
                yesIsSafe: false))
        {
            return;
        }

        IsBusy = true;
        ResultText = string.Empty;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        _log.Info($"Cache cleanup started: {string.Join(", ", chosen.Select(r => r.Name))}.");
        try
        {
            var results = await _cleaner.CleanAsync(chosen.Select(r => r.Category).ToList(), new Progress<string>(text => StatusText = text), cts.Token);
            ResultText = Describe(results);
            _log.Info($"Cache cleanup finished. {ResultText}");
        }
        catch (OperationCanceledException)
        {
            ResultText = "Stopped. What was already deleted stays deleted.";
            _log.Warn("Cache cleanup stopped by the user.");
        }
        catch (Exception ex)
        {
            _log.Error("Cache cleanup failed", ex);
            _dialogs.ShowMessage("Could not finish the cleanup", ex.Message, isError: true);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }

        await ScanAsync();
    }

    private static string Describe(IReadOnlyList<CacheCleanResult> results)
    {
        var freed = results.Sum(r => r.BytesFreed);
        var skipped = results.Sum(r => r.Skipped);
        var text = $"Freed {SizeFormatter.Format(freed)} ({results.Sum(r => r.Deleted):N0} files).";
        return skipped > 0 ? text + $" {skipped:N0} files were in use and left alone: close the programs and run it again to get them too." : text;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CleanButtonText));
        CleanCommand.RaiseCanExecuteChanged();
    }
}
