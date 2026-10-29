using RigUninstaller.Models;

namespace RigUninstaller.ViewModels;

/// <summary>One line of the leftovers checklist.</summary>
public sealed class LeftoverRowViewModel : ViewModelBase
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected;

    public LeftoverRowViewModel(LeftoverItem item, Action onSelectionChanged)
    {
        Item = item;
        _onSelectionChanged = onSelectionChanged;
    }

    public LeftoverItem Item { get; }

    public string Path => Item.Path;

    public string Reason => Item.Reason;

    public string ConfidenceText => Item.Confidence.ToString();

    public string TypeText => Item.Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "File",
        _ => "Registry key",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }
}

/// <summary>Backs the leftovers window. Nothing is ticked at the start; the user decides what goes.</summary>
public sealed class LeftoversViewModel : ViewModelBase
{
    private readonly IReadOnlyList<LeftoverRowViewModel> _allRows;
    private readonly bool _restorePointAlreadyMade;
    private IReadOnlyList<LeftoverRowViewModel> _visibleRows;
    private bool _showLow;
    private bool _createRestorePoint;
    private int _selectedCount;

    public LeftoversViewModel(InstalledProgram program, IReadOnlyList<LeftoverItem> items, bool restorePointAlreadyMade)
    {
        Program = program;
        _restorePointAlreadyMade = restorePointAlreadyMade;
        _createRestorePoint = !restorePointAlreadyMade;

        // Most certain first; OrderByDescending keeps the scan's order inside each group.
        _allRows = items
            .OrderByDescending(i => i.Confidence)
            .Select(i => new LeftoverRowViewModel(i, OnSelectionChanged))
            .ToList();
        _visibleRows = _allRows.Where(IsShownByDefault).ToList();
        HasRegistryItems = items.Any(i => i.Kind == LeftoverKind.RegistryKey);

        TickHighCommand = new RelayCommand(_ => Tick(row => row.Item.Confidence == Confidence.High));
        TickAllCommand = new RelayCommand(_ => Tick(_ => true));
        UntickAllCommand = new RelayCommand(_ => Tick(_ => true, select: false));
        ConfirmCommand = new RelayCommand(_ => Accepted?.Invoke(), _ => SelectedCount > 0);
        CloseCommand = new RelayCommand(_ => Closed?.Invoke());
    }

    public event Action? Accepted;

    public event Action? Closed;

    public InstalledProgram Program { get; }

    public string Heading => $"Leftovers of {Program.Name}";

    public bool HasRegistryItems { get; }

    public IReadOnlyList<LeftoverRowViewModel> VisibleRows
    {
        get => _visibleRows;
        private set => SetProperty(ref _visibleRows, value);
    }

    public bool ShowLow
    {
        get => _showLow;
        set
        {
            if (!SetProperty(ref _showLow, value))
            {
                return;
            }

            if (!value)
            {
                // Hidden rows must never stay ticked: they would be removed without being seen.
                foreach (var row in _allRows.Where(r => r.Item.Confidence == Confidence.Low))
                {
                    row.IsSelected = false;
                }
            }

            VisibleRows = _allRows.Where(IsShownByDefault).ToList();
            OnPropertyChanged(nameof(HiddenLowCount));
            RefreshSelection();
        }
    }

    public int HiddenLowCount => ShowLow ? 0 : _allRows.Count(r => r.Item.Confidence == Confidence.Low);

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    public string ConfirmText => SelectedCount == 0
        ? "Remove selected items"
        : $"Remove {SelectedCount} selected item{(SelectedCount == 1 ? string.Empty : "s")}";

    public bool ShowRestorePointOption => !_restorePointAlreadyMade;

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => SetProperty(ref _createRestorePoint, value);
    }

    /// <summary>The visible, ticked items.</summary>
    public LeftoverChoice Choice => new(
        VisibleRows.Where(r => r.IsSelected).Select(r => r.Item).ToList(),
        ShowRestorePointOption && CreateRestorePoint);

    public RelayCommand TickHighCommand { get; }

    public RelayCommand TickAllCommand { get; }

    public RelayCommand UntickAllCommand { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CloseCommand { get; }

    private bool IsShownByDefault(LeftoverRowViewModel row) => ShowLow || row.Item.Confidence != Confidence.Low;

    private void Tick(Func<LeftoverRowViewModel, bool> which, bool select = true)
    {
        foreach (var row in VisibleRows.Where(which))
        {
            row.IsSelected = select;
        }
    }

    private void OnSelectionChanged() => RefreshSelection();

    private void RefreshSelection()
    {
        SelectedCount = VisibleRows.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(ConfirmText));
        ConfirmCommand.RaiseCanExecuteChanged();
    }
}
