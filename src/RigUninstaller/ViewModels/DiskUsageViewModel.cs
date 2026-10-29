using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>A bar of the program chart.</summary>
public sealed class DiskUsageRowViewModel
{
    public DiskUsageRowViewModel(DiskUsageEntry entry, long maxBytes, long totalBytes = 0)
    {
        Name = entry.Name;
        Drive = entry.Drive == DiskUsage.UnknownDrive ? SizeFormatter.Unknown : entry.Drive; // a dash fits the narrow column
        Bytes = entry.Bytes;
        Maximum = Math.Max(1, maxBytes);
        SizeText = (entry.IsEstimated ? "~" : string.Empty) + SizeFormatter.Format(entry.Bytes);
        ShareText = ShareOf(entry.Bytes, totalBytes);
    }

    public string Name { get; }

    /// <summary>The drive the program is on, e.g. "C:".</summary>
    public string Drive { get; }

    /// <summary>Its part of the total shown (all drives, or the one drive picked), e.g. "12%".</summary>
    public string ShareText { get; }

    private static string ShareOf(long bytes, long total)
    {
        if (total <= 0)
        {
            return string.Empty;
        }

        var percent = 100.0 * bytes / total;
        return percent < 1 ? "<1%" : $"{percent:0}%";
    }

    public long Bytes { get; }

    public long Maximum { get; }

    public string SizeText { get; }
}

/// <summary>One button above the program chart: "All drives" or a single drive, with what its programs add up to.</summary>
public sealed class DriveFilterViewModel : ViewModelBase
{
    private bool _isSelected;

    public DriveFilterViewModel(string? drive, long totalBytes, int count)
    {
        Drive = drive;
        TotalBytes = totalBytes;
        Count = count;
    }

    /// <summary>The drive ("C:"), or null for all drives together.</summary>
    public string? Drive { get; }

    public long TotalBytes { get; }

    public int Count { get; }

    public string Label => Drive ?? "All drives";

    public string TotalText => SizeFormatter.Format(TotalBytes);

    public string CountText => Count == 1 ? "1 program" : $"{Count} programs";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(ButtonTag));
            }
        }
    }

    /// <summary>"primary" makes the shared button style draw it as the chosen one.</summary>
    public string ButtonTag => IsSelected ? "primary" : string.Empty;
}

/// <summary>A drive card: how full it is.</summary>
public sealed class DriveViewModel
{
    private const double LowFreeShare = 0.10;

    public DriveViewModel(DriveEntry drive)
    {
        Drive = drive;
    }

    public DriveEntry Drive { get; }

    public string Name => Drive.Name;

    public string Title => string.IsNullOrWhiteSpace(Drive.Label) ? Drive.Name : $"{Drive.Name}  {Drive.Label}";

    public string Format => Drive.Format;

    public double UsedFraction => Drive.UsedFraction;

    public string UsedText => $"{SizeFormatter.Format(Drive.UsedBytes)} used of {SizeFormatter.Format(Drive.TotalBytes)}";

    public string FreeText => $"{SizeFormatter.Format(Drive.FreeBytes)} free" + (IsLow ? "  ·  almost full" : string.Empty);

    /// <summary>Under 10% free: drawn in the warning colour, with the words "Almost full" so colour is not the only signal.</summary>
    public bool IsLow => Drive.TotalBytes > 0 && (double)Drive.FreeBytes / Drive.TotalBytes < LowFreeShare;
}

/// <summary>A folder or file in the folder being browsed.</summary>
public sealed class FolderRowViewModel
{
    public FolderRowViewModel(FolderEntry entry, long maxBytes)
    {
        Entry = entry;
        Maximum = Math.Max(1, maxBytes);
    }

    public FolderEntry Entry { get; }

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsFolder => Entry.IsFolder;

    public long Bytes => Entry.Bytes;

    public long Maximum { get; }

    public string SizeText => SizeFormatter.Format(Entry.Bytes);

    public string KindText => Entry.IsFolder ? "Folder" : "File";
}

/// <summary>
/// The Disk usage tab. "Drives" shows every drive and lets you click down into the folders that use the space.
/// "Programs" is the bar chart of the 15 biggest installed programs. Everything here only reads.
/// </summary>
public sealed class DiskUsageViewModel : ViewModelBase, ITabViewModel
{
    private const int TopCount = 15;

    private readonly IFolderSizer _sizer;
    private readonly SafetyGuard _guard;
    private readonly IFileSystemProbe _fs;
    private readonly IActionLog _log;
    private readonly IDriveSource? _driveSource;
    private readonly IFolderBreakdown? _breakdown;
    private readonly IExternalActions? _external;

    private IReadOnlyList<InstalledProgram> _programs = Array.Empty<InstalledProgram>();
    private IReadOnlyList<DiskUsageEntry> _entries = Array.Empty<DiskUsageEntry>();
    private IReadOnlyList<DriveFilterViewModel> _driveFilters = Array.Empty<DriveFilterViewModel>();
    private string? _selectedDrive;
    private string _heading = "The programs that use the most space";
    private string _totalText = string.Empty;
    private IReadOnlyList<DiskUsageRowViewModel> _rows = Array.Empty<DiskUsageRowViewModel>();
    private IReadOnlyList<DriveViewModel> _drives = Array.Empty<DriveViewModel>();
    private IReadOnlyList<FolderRowViewModel> _folderRows = Array.Empty<FolderRowViewModel>();
    private string _statusText = "Open this tab to see which programs use the most space.";
    private string _folderStatus = "Pick a drive to see which folders use its space.";
    private string? _currentPath;
    private bool _isScanningFolders;
    private bool _dirty = true;
    private bool _isMeasuring;
    private CancellationTokenSource? _folderCts;

    public DiskUsageViewModel(
        IFolderSizer sizer,
        SafetyGuard guard,
        IFileSystemProbe fs,
        IActionLog log,
        IDriveSource? driveSource = null,
        IFolderBreakdown? breakdown = null,
        IExternalActions? external = null)
    {
        _sizer = sizer;
        _guard = guard;
        _fs = fs;
        _log = log;
        _driveSource = driveSource;
        _breakdown = breakdown;
        _external = external;

        RefreshCommand = new AsyncRelayCommand(_ => ComputeAsync());
        SelectDriveFilterCommand = new RelayCommand(filter => SelectDrive((filter as DriveFilterViewModel)?.Drive), filter => filter is DriveFilterViewModel);
        ScanDriveCommand = new AsyncRelayCommand(drive => BrowseAsync((drive as DriveViewModel)?.Drive.RootPath), drive => drive is DriveViewModel);
        OpenFolderCommand = new AsyncRelayCommand(row => BrowseAsync((row as FolderRowViewModel)?.FullPath), row => row is FolderRowViewModel { IsFolder: true });
        UpCommand = new AsyncRelayCommand(_ => BrowseAsync(ParentPath), _ => ParentPath is not null);
        RescanFolderCommand = new AsyncRelayCommand(_ => BrowseAsync(CurrentPath), _ => CurrentPath is not null && !IsScanningFolders);
        StopScanCommand = new RelayCommand(_ => _folderCts?.Cancel(), _ => IsScanningFolders);
        ExploreCommand = new RelayCommand(_ => _external?.OpenFolder(CurrentPath!), _ => CurrentPath is not null && _external is not null);
    }

    // ---- programs chart ----

    public IReadOnlyList<DiskUsageRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>"All drives" plus one button per drive that has programs on it.</summary>
    public IReadOnlyList<DriveFilterViewModel> DriveFilters
    {
        get => _driveFilters;
        private set => SetProperty(ref _driveFilters, value);
    }

    public RelayCommand SelectDriveFilterCommand { get; }

    /// <summary>True while the programs are being measured: the chart shows grey placeholder bars.</summary>
    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set
        {
            if (SetProperty(ref _isMeasuring, value))
            {
                OnPropertyChanged(nameof(ShowSkeleton));
            }
        }
    }

    /// <summary>Grey placeholder bars: only while the first measurement has nothing to show yet, never over real rows.</summary>
    public bool ShowSkeleton => IsMeasuring && Rows.Count == 0;

    /// <summary>Title above the chart, e.g. "Biggest programs on F:".</summary>
    public string Heading
    {
        get => _heading;
        private set => SetProperty(ref _heading, value);
    }

    /// <summary>What the programs of the chosen drive (or all drives) add up to, e.g. "87.4 GB in 63 programs".</summary>
    public string TotalText
    {
        get => _totalText;
        private set => SetProperty(ref _totalText, value);
    }

    // ---- drives and folders ----

    public IReadOnlyList<DriveViewModel> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public IReadOnlyList<FolderRowViewModel> FolderRows
    {
        get => _folderRows;
        private set => SetProperty(ref _folderRows, value);
    }

    public string FolderStatus
    {
        get => _folderStatus;
        private set => SetProperty(ref _folderStatus, value);
    }

    /// <summary>The folder being looked at, or null before a drive was picked.</summary>
    public string? CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(HasCurrentPath));
                UpCommand.RaiseCanExecuteChanged();
                RescanFolderCommand.RaiseCanExecuteChanged();
                ExploreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasCurrentPath => _currentPath is not null;

    public bool IsScanningFolders
    {
        get => _isScanningFolders;
        private set
        {
            if (SetProperty(ref _isScanningFolders, value))
            {
                StopScanCommand.RaiseCanExecuteChanged();
                RescanFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasDrives => _driveSource is not null;

    public AsyncRelayCommand ScanDriveCommand { get; }

    public AsyncRelayCommand OpenFolderCommand { get; }

    public AsyncRelayCommand UpCommand { get; }

    public AsyncRelayCommand RescanFolderCommand { get; }

    public RelayCommand StopScanCommand { get; }

    public RelayCommand ExploreCommand { get; }

    /// <summary>The folder one level up, or null at a drive root.</summary>
    private string? ParentPath
    {
        get
        {
            if (CurrentPath is null)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(CurrentPath.TrimEnd('\\'));
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
    }

    /// <summary>Called after every scan, so the chart is rebuilt the next time the tab is opened.</summary>
    public void SetPrograms(IReadOnlyList<InstalledProgram> programs)
    {
        _programs = programs;
        _dirty = true;
    }

    public void Activate()
    {
        LoadDrives();
        if (_dirty)
        {
            RefreshCommand.Execute(null);
        }
    }

    public void LoadDrives()
    {
        if (_driveSource is null)
        {
            return;
        }

        Drives = _driveSource.GetDrives().Select(d => new DriveViewModel(d)).ToList();
        _log.Info($"Drives: {string.Join(", ", Drives.Select(d => $"{d.Name} {d.FreeText}"))}");
    }

    public async Task ComputeAsync()
    {
        StatusText = "Measuring... programs that report no size are measured from their folder.";
        IsMeasuring = true;
        try
        {
            var programs = _programs;
            _entries = await Task.Run(() => DiskUsage.All(programs, _sizer, _guard, _fs, CancellationToken.None));
            _dirty = false;
            BuildDriveFilters();
            ShowChart();
        }
        catch (Exception ex)
        {
            _log.Error("Could not measure disk usage", ex);
            StatusText = $"Could not measure disk usage: {ex.Message}";
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    /// <summary>Shows one drive's programs, or all of them when <paramref name="drive"/> is null.</summary>
    public void SelectDrive(string? drive)
    {
        _selectedDrive = drive;
        foreach (var filter in DriveFilters)
        {
            filter.IsSelected = filter.Drive == drive;
        }

        ShowChart();
    }

    private void BuildDriveFilters()
    {
        var perDrive = _entries
            .GroupBy(e => e.Drive)
            .OrderBy(g => g.Key is DiskUsage.UnknownDrive or DiskUsage.NetworkDrive ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DriveFilterViewModel(g.Key, g.Sum(e => e.Bytes), g.Count()));

        var filters = new List<DriveFilterViewModel> { new(null, _entries.Sum(e => e.Bytes), _entries.Count) };
        filters.AddRange(perDrive);

        // A drive that no longer has programs falls back to "All drives".
        if (_selectedDrive is not null && filters.All(f => f.Drive != _selectedDrive))
        {
            _selectedDrive = null;
        }

        foreach (var filter in filters)
        {
            filter.IsSelected = filter.Drive == _selectedDrive;
        }

        DriveFilters = filters;
    }

    private void ShowChart()
    {
        var shown = _selectedDrive is null ? _entries : _entries.Where(e => e.Drive == _selectedDrive).ToList();
        var total = shown.Sum(e => e.Bytes);
        var top = shown.Take(TopCount).ToList();
        var max = top.Count == 0 ? 1 : top.Max(e => e.Bytes);
        Rows = top.Select(e => new DiskUsageRowViewModel(e, max, total)).ToList();

        var where = _selectedDrive is null ? "all drives" : _selectedDrive;
        Heading = _selectedDrive is null ? "Biggest programs on all drives" : $"Biggest programs on {_selectedDrive}";
        TotalText = shown.Count == 0 ? string.Empty : $"{SizeFormatter.Format(total)} in {shown.Count} programs on {where}";

        var unmeasured = _programs.Count - _entries.Count;
        StatusText = shown.Count == 0
            ? (_entries.Count == 0 ? "No sizes are known yet." : $"No programs with a known size on {where}.")
            : $"Top {top.Count} of {(_selectedDrive is null ? _programs.Count : shown.Count)} programs. A ~ means the size was measured from the install folder."
              + (unmeasured > 0 && _selectedDrive is null
                  ? (unmeasured == 1 ? " 1 program reports no size and is left out." : $" {unmeasured} programs report no size and are left out.")
                  : string.Empty);
    }

    /// <summary>Measures what is inside a folder (a drive root to begin with) and shows it biggest first.</summary>
    public async Task BrowseAsync(string? path)
    {
        if (path is null || _breakdown is null)
        {
            return;
        }

        _folderCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _folderCts = cts;

        CurrentPath = path;
        IsScanningFolders = true;
        FolderRows = Array.Empty<FolderRowViewModel>();
        FolderStatus = $"Measuring {path} ... the biggest come first as they finish.";
        _log.Info($"Measuring folder sizes in {path}");

        var seen = new List<FolderEntry>();
        var progress = new Progress<FolderEntry>(entry =>
        {
            if (!cts.IsCancellationRequested && CurrentPath == path)
            {
                seen.Add(entry);
                Show(seen);
            }
        });

        try
        {
            var all = await _breakdown.ScanAsync(path, progress, cts.Token);
            if (CurrentPath == path)
            {
                Show(all);
                FolderStatus = all.Count == 0
                    ? "This folder is empty or cannot be read."
                    : $"{all.Count} items, {SizeFormatter.Format(all.Sum(e => e.Bytes))} in {path}. Click a folder to look inside it.";
            }
        }
        catch (OperationCanceledException)
        {
            if (CurrentPath == path)
            {
                FolderStatus = "Stopped. The sizes shown so far are partial.";
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Could not measure {path}", ex);
            FolderStatus = $"Could not measure {path}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_folderCts, cts))
            {
                _folderCts = null;
                IsScanningFolders = false;
            }
        }
    }

    private void Show(IReadOnlyList<FolderEntry> entries)
    {
        var sorted = entries.OrderByDescending(e => e.Bytes).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var max = sorted.Count == 0 ? 1 : sorted.Max(e => e.Bytes);
        FolderRows = sorted.Select(e => new FolderRowViewModel(e, max)).ToList();
    }
}
