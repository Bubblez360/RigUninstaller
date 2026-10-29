using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

public sealed class OrphanRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _isChecked;

    public OrphanRowViewModel(OrphanFolder folder, Action onChanged)
    {
        Folder = folder;
        _onChanged = onChanged;
    }

    public OrphanFolder Folder { get; }

    public string Name => Folder.Name;

    public string Location => Folder.Location;

    public string Path => Folder.Path;

    public string Reason => Folder.Reason;

    public long Bytes => Folder.Bytes;

    public string SizeText => SizeFormatter.Format(Folder.Bytes);

    public string LastChangedText => Folder.LastChanged.ToString("yyyy-MM-dd");

    /// <summary>Never ticked for you: these are guesses, so each one is the user's own decision.</summary>
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
}

/// <summary>
/// The "Old leftovers" part of the Cleanup tab: folders in AppData, ProgramData and Program Files that no installed
/// program owns. Scanning only reads. Ticked folders go to the Recycle Bin (so the Undo center can bring them back).
/// </summary>
public sealed class OrphanScanViewModel : ViewModelBase
{
    private readonly IOrphanFolderScanner _scanner;
    private readonly ILeftoverCleaner _cleaner;
    private readonly IFileSystemProbe _files;
    private readonly IExternalActions _external;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private IReadOnlyList<InstalledProgram> _programs = Array.Empty<InstalledProgram>();
    private IReadOnlyList<OrphanRowViewModel> _rows = Array.Empty<OrphanRowViewModel>();
    private string _statusText = "Looks for folders left behind by programs that are no longer installed. Nothing is changed until you tick folders and press the button.";
    private string _resultText = string.Empty;
    private bool _isBusy;
    private bool _hasScanned;
    private CancellationTokenSource? _cts;

    public OrphanScanViewModel(IOrphanFolderScanner scanner, ILeftoverCleaner cleaner, IFileSystemProbe files, IExternalActions external, IDialogService dialogs, IActionLog log)
    {
        _scanner = scanner;
        _cleaner = cleaner;
        _files = files;
        _external = external;
        _dialogs = dialogs;
        _log = log;
        FindCommand = new AsyncRelayCommand(_ => FindAsync(), _ => !IsBusy);
        MoveCommand = new AsyncRelayCommand(_ => MoveAsync(), _ => !IsBusy && Selected.Count > 0);
        OpenCommand = new RelayCommand(row => OpenFolder(row as OrphanRowViewModel), row => row is OrphanRowViewModel);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
    }

    public IReadOnlyList<OrphanRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(IsEmptyAfterScan));
                OnPropertyChanged(nameof(ShowSkeleton));
                OnSelectionChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

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
                FindCommand.RaiseCanExecuteChanged();
                MoveCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    /// <summary>Grey placeholder rows while the first search runs.</summary>
    public bool ShowSkeleton => IsBusy && Rows.Count == 0;

    /// <summary>Scanned, and nothing suspicious was found: say so instead of showing an empty table.</summary>
    public bool IsEmptyAfterScan => _hasScanned && Rows.Count == 0;

    private IReadOnlyList<OrphanRowViewModel> Selected => _rows.Where(r => r.IsChecked).ToList();

    public string SelectedText => Selected.Count == 0 ? "Nothing ticked" : $"{Selected.Count} ticked, {SizeFormatter.Format(Selected.Sum(r => r.Bytes))}";

    public string MoveButtonText => Selected.Count == 0 ? "Move to Recycle Bin" : $"Move {Selected.Count} to Recycle Bin";

    public AsyncRelayCommand FindCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>Called after every program scan: the search compares folders with this list.</summary>
    public void SetPrograms(IReadOnlyList<InstalledProgram> programs) => _programs = programs;

    public async Task FindAsync()
    {
        IsBusy = true;
        ResultText = string.Empty;
        StatusText = "Looking... this reads folder sizes, so it can take a minute.";
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var found = await _scanner.ScanAsync(_programs, cts.Token);
            _hasScanned = true;
            Rows = found.Select(f => new OrphanRowViewModel(f, OnSelectionChanged)).ToList();
            StatusText = found.Count == 0
                ? "Nothing suspicious found: every folder matches an installed program, is shared, or was used recently."
                : $"{found.Count} folders, {SizeFormatter.Format(found.Sum(f => f.Bytes))} in total. These are guesses: open a folder to look first, then tick the ones you are sure about.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
        }
        catch (Exception ex)
        {
            _log.Error("Old leftover scan failed", ex);
            StatusText = $"Could not scan: {ex.Message}";
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmptyAfterScan));
        }
    }

    private async Task MoveAsync()
    {
        var chosen = Selected;
        if (chosen.Count == 0)
        {
            return;
        }

        var list = string.Join("\n", chosen.Take(12).Select(r => $"{r.Path}  ({r.SizeText})"))
                   + (chosen.Count > 12 ? $"\n...and {chosen.Count - 12} more." : string.Empty);
        if (!_dialogs.Confirm(
                $"Move {chosen.Count} folder{(chosen.Count == 1 ? string.Empty : "s")} to the Recycle Bin?",
                list + "\n\nThey go to the Recycle Bin, not away for good. If a program stops working, restore them from the Undo center.",
                "Move to Recycle Bin",
                "Cancel",
                yesIsSafe: true))
        {
            return;
        }

        IsBusy = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var items = chosen.Select(r => new LeftoverItem(LeftoverKind.Folder, r.Path, Confidence.Low, r.Reason)).ToList();
            var result = await _cleaner.CleanAsync(OldLeftoversProgram, items, cts.Token);

            // The folders that are really gone leave the list; anything that could not be moved stays.
            var gone = chosen.Where(r => !_files.Exists(r.Path)).ToList();
            Rows = _rows.Except(gone).ToList();
            ResultText = $"Moved {result.Moved} folder{(result.Moved == 1 ? string.Empty : "s")} ({SizeFormatter.Format(gone.Sum(r => r.Bytes))}) to the Recycle Bin. You can restore them from the Undo center."
                         + (result.Failures.Count > 0 ? $" {result.Failures.Count} could not be moved (in use?)." : string.Empty);
            _log.Info($"Old leftovers: {ResultText}");
            if (result.Failures.Count > 0)
            {
                _dialogs.ShowMessage("Some folders could not be moved", string.Join("\n\n", result.Failures.Take(8).Select(f => $"{f.Item.Path}\n    {f.Error}")), isError: true);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Moving old leftovers failed", ex);
            _dialogs.ShowMessage("Could not move the folders", ex.Message, isError: true);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>The name these moves are filed under in the log and the Undo center.</summary>
    public static InstalledProgram OldLeftoversProgram { get; } = new()
    {
        Name = "Old leftover folders",
        RegistryKeyPath = string.Empty,
        KeyName = string.Empty,
    };

    private void OpenFolder(OrphanRowViewModel? row)
    {
        if (row is not null)
        {
            _external.OpenFolder(row.Path);
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(MoveButtonText));
        MoveCommand.RaiseCanExecuteChanged();
    }
}
