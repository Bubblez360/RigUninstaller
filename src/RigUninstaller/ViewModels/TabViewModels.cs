using System.Windows.Media;
using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>A tab that loads its content when the user first opens it.</summary>
public interface ITabViewModel
{
    void Activate();
}

// ---------------------------------------------------------------- startup

public sealed class StartupItemViewModel
{
    private readonly ImageSource? _icon;

    public StartupItemViewModel(StartupItem item, ImageSource? icon = null)
    {
        Item = item;
        _icon = icon;
    }

    public StartupItem Item { get; }

    /// <summary>The program's own icon, or the grey "no icon" picture.</summary>
    public ImageSource? Icon => _icon ?? NoIcon.Image;

    public string Name => Item.Name;

    public string Command => Item.Command;

    public string Location => Item.Location;

    public bool IsEnabled => Item.IsEnabled;

    public string StateText => Item.IsEnabled ? "On" : "Off";

    public string ActionText => Item.IsEnabled ? "Turn off" : "Turn on";
}

/// <summary>The Startup tab: items that start with Windows, which can be turned off and on (never deleted).</summary>
public sealed class StartupViewModel : ViewModelBase, ITabViewModel
{
    private readonly IStartupManager _manager;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private readonly ICommandIconProvider? _icons;
    private IReadOnlyList<StartupItemViewModel> _items = Array.Empty<StartupItemViewModel>();
    private string _statusText = "Open this tab to list the startup items.";
    private bool _loaded;
    private bool _isLoading;

    public StartupViewModel(IStartupManager manager, IDialogService dialogs, IActionLog log, ICommandIconProvider? icons = null)
    {
        _manager = manager;
        _dialogs = dialogs;
        _log = log;
        _icons = icons;
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        ToggleCommand = new AsyncRelayCommand(row => ToggleAsync(row as StartupItemViewModel));
        AddCommand = new AsyncRelayCommand(_ => AddAsync());
    }

    public IReadOnlyList<StartupItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    /// <summary>Grey placeholder rows: only while the first read has nothing to show yet.</summary>
    public bool ShowSkeleton => IsLoading && Items.Count == 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ToggleCommand { get; }

    /// <summary>"Add app": pick a program and make it start with Windows.</summary>
    public AsyncRelayCommand AddCommand { get; }

    public void Activate()
    {
        if (!_loaded)
        {
            RefreshCommand.Execute(null);
        }
    }

    /// <summary>True while the list is being read.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    public async Task LoadAsync()
    {
        StatusText = "Reading startup items...";
        IsLoading = true;
        try
        {
            var items = await _manager.ListAsync(CancellationToken.None);
            // Icons come from the program files, so they are read off the UI thread.
            Items = await Task.Run(() => items.Select(i => new StartupItemViewModel(i, _icons?.GetIcon(i.Command))).ToList());
            _loaded = true;
            StatusText = $"{items.Count} startup items, {items.Count(i => i.IsEnabled)} turned on. Turning one off keeps it in the list, so you can turn it on again.";
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the startup items", ex);
            StatusText = $"Could not read the startup items: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AddAsync()
    {
        var path = _dialogs.AskOpenPath(
            "Pick the app that should start with Windows",
            "Programs and shortcuts (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"Start {Path.GetFileNameWithoutExtension(path)} with Windows?",
                $"{path}\n\n"
                + (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? "This is a shortcut: the program it points to (with its arguments) is what starts with Windows.\n\n"
                    : string.Empty)
                + "It is added for your Windows account only. It is not deleted by this app: you can turn it off in this list at any time.",
                "Add to startup",
                "Cancel",
                yesIsSafe: true))
        {
            return;
        }

        var result = await _manager.AddAsync(path, CancellationToken.None);
        _dialogs.ShowMessage(result.Success ? "Added to startup" : "Could not add it", result.Message, isError: !result.Success);
        await LoadAsync();
    }

    private async Task ToggleAsync(StartupItemViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var change = await _manager.SetEnabledAsync(row.Item, !row.Item.IsEnabled, CancellationToken.None);
        if (!change.Success)
        {
            _dialogs.ShowMessage($"Could not change {row.Name}", change.Message, isError: true);
        }

        await LoadAsync();
    }
}

// ---------------------------------------------------------------- undo center

public sealed class UndoItemViewModel
{
    public UndoItemViewModel(UndoItem item)
    {
        Item = item;
    }

    public UndoItem Item { get; }

    public string DateText => Item.Entry.Time.ToString("yyyy-MM-dd HH:mm");

    public string TypeText => Item.TypeText;

    public string Program => Item.Entry.Program;

    public string Description => Item.Description;

    public bool CanRestore => Item.CanRestore;

    public string StateText => Item.IsRestored ? "Restored" : string.Empty;
}

/// <summary>The Undo center tab: every backup the app made, with a Restore button.</summary>
public sealed class UndoViewModel : ViewModelBase, ITabViewModel
{
    private readonly IUndoService _undo;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<UndoItemViewModel> _items = Array.Empty<UndoItemViewModel>();

    public UndoViewModel(IUndoService undo, IDialogService dialogs)
    {
        _undo = undo;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(_ => Refresh());
        RestoreCommand = new AsyncRelayCommand(row => RestoreAsync(row as UndoItemViewModel), row => row is UndoItemViewModel { CanRestore: true });
    }

    public IReadOnlyList<UndoItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => Items.Count == 0;

    public RelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RestoreCommand { get; }

    public void Activate() => Refresh();

    public void Refresh() => Items = _undo.List().Select(i => new UndoItemViewModel(i)).ToList();

    private async Task RestoreAsync(UndoItemViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Confirm($"Restore this {row.TypeText.ToLowerInvariant()}?", $"{row.Program}\n{row.Description}", "Restore", "Cancel", yesIsSafe: true))
        {
            return;
        }

        var result = await _undo.RestoreAsync(row.Item, CancellationToken.None);
        _dialogs.ShowMessage(result.Success ? "Restored" : "Could not restore", result.Message, isError: !result.Success);
        Refresh();
    }
}

// ---------------------------------------------------------------- store apps

public sealed class StoreAppViewModel
{
    private readonly ImageSource? _icon;

    public StoreAppViewModel(StoreApp app, ImageSource? icon = null)
    {
        App = app;
        _icon = icon;
    }

    public StoreApp App { get; }

    /// <summary>The app's tile logo, or the grey "no icon" picture.</summary>
    public ImageSource? Icon => _icon ?? NoIcon.Image;

    public string Name => App.DisplayName;

    public string Publisher => App.ReadablePublisher ?? SizeFormatter.Unknown;

    public string Version => App.Version;

    public bool CanRemove => App.CanRemove;

    public string StateText => App.BlockReason ?? string.Empty;
}

/// <summary>The Store apps tab: Get-AppxPackage / Remove-AppxPackage with the usual safety net.</summary>
public sealed class StoreAppsViewModel : ViewModelBase, ITabViewModel
{
    private readonly IStoreAppService _service;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private readonly IStoreIconProvider? _icons;
    private IReadOnlyList<StoreApp> _all = Array.Empty<StoreApp>();
    private IReadOnlyDictionary<string, ImageSource?> _iconByPackage = new Dictionary<string, ImageSource?>();
    private IReadOnlyList<StoreAppViewModel> _items = Array.Empty<StoreAppViewModel>();
    private string _searchText = string.Empty;
    private bool _showSystem;
    private string _statusText = "Open this tab to list the Store apps.";
    private bool _loaded;
    private bool _isLoading;

    public StoreAppsViewModel(IStoreAppService service, IRestorePointService restorePoints, IDialogService dialogs, IActionLog log, IStoreIconProvider? icons = null)
    {
        _service = service;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _log = log;
        _icons = icons;
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => SearchText.Length > 0);
        RemoveCommand = new AsyncRelayCommand(row => RemoveAsync(row as StoreAppViewModel), row => row is StoreAppViewModel { CanRemove: true });
    }

    public IReadOnlyList<StoreAppViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    /// <summary>Grey placeholder rows: only while the first read has nothing to show yet.</summary>
    public bool ShowSkeleton => IsLoading && Items.Count == 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Also lists frameworks and Windows' own packages. They are shown greyed out and cannot be removed.</summary>
    public bool ShowSystem
    {
        get => _showSystem;
        set
        {
            if (SetProperty(ref _showSystem, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public void Activate()
    {
        if (!_loaded)
        {
            RefreshCommand.Execute(null);
        }
    }

    /// <summary>True while the packages are being read.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    public async Task LoadAsync()
    {
        StatusText = "Reading Store apps...";
        IsLoading = true;
        try
        {
            _all = await _service.ListAsync(CancellationToken.None);
            _iconByPackage = await Task.Run(LoadIcons);
            _loaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the Store apps", ex);
            StatusText = $"Could not read the Store apps: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Reads every package's logo in parallel (they are small pictures on the local disk).</summary>
    private IReadOnlyDictionary<string, ImageSource?> LoadIcons() =>
        _icons is null
            ? new Dictionary<string, ImageSource?>()
            : _all.AsParallel()
                .Select(app => (app.FullName, Icon: _icons.GetIcon(app)))
                .ToDictionary(pair => pair.FullName, pair => pair.Icon);

    private void ApplyFilter()
    {
        var shown = _all
            .Where(a => ShowSystem || a.CanRemove)
            .Where(a => SearchText.Trim().Length == 0
                        || a.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
                        || a.PublisherName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(a => new StoreAppViewModel(a, _iconByPackage.GetValueOrDefault(a.FullName)))
            .ToList();
        Items = shown;
        StatusText = $"{shown.Count} of {_all.Count} packages shown. Removed Store apps can be installed again from the Microsoft Store.";
    }

    private async Task RemoveAsync(StoreAppViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"Remove {row.Name}?",
                "It is removed for your account with Remove-AppxPackage. You can install it again from the Microsoft Store. A System Restore point is offered first.",
                "Remove",
                "Cancel",
                yesIsSafe: false))
        {
            return;
        }

        var restore = await _restorePoints.CreateAsync($"RigUninstaller: before removing {row.Name}", CancellationToken.None);
        if (!restore.Created && !_dialogs.AskContinueWithoutRestorePoint(restore.Message))
        {
            return;
        }

        var result = await _service.RemoveAsync(row.App, CancellationToken.None);
        _dialogs.ShowMessage(result.Success ? "Removed" : "Could not remove", result.Message, isError: !result.Success);
        await LoadAsync();
    }
}
