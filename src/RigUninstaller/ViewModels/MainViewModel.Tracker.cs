using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>The install tracker: snapshot before and after an install, to remove it exactly later.</summary>
public sealed partial class MainViewModel
{
    private bool _isTracking;

    public AsyncRelayCommand TrackInstallCommand { get; private set; } = null!;

    public RelayCommand CancelTrackingCommand { get; private set; } = null!;

    public bool IsTracking
    {
        get => _isTracking;
        private set
        {
            if (SetProperty(ref _isTracking, value))
            {
                OnPropertyChanged(nameof(TrackButtonText));
                CancelTrackingCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string TrackButtonText => IsTracking ? "Finish tracking" : "Track new install";

    private void InitializeTracker()
    {
        TrackInstallCommand = new AsyncRelayCommand(_ => TrackInstallAsync(), _ => !IsUninstalling && !IsScanning);
        CancelTrackingCommand = new RelayCommand(_ => CancelTracking(), _ => IsTracking);
    }

    private async Task TrackInstallAsync()
    {
        var tracker = _services.Tracker;
        using var cts = new CancellationTokenSource();
        var progress = new Progress<string>(text => BusyDetail = text);

        if (!tracker.IsTracking)
        {
            BeginBusy("Taking the first snapshot...", "Looking at Program Files, AppData, ProgramData, the Start Menu and the registry. This can take a minute.", canStop: false);
            try
            {
                await tracker.BeginAsync(_programs, progress, cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error("Install tracking could not start", ex);
                EndBusy();
                _dialogs.ShowMessage("Tracking could not start", ex.Message, isError: true);
                return;
            }

            EndBusy();
            IsTracking = tracker.IsTracking;
            _dialogs.ShowMessage(
                "Tracking is on",
                "Now install your program. When the installer is completely done, come back and click Finish tracking.",
                isError: false);
            return;
        }

        BeginBusy("Taking the second snapshot...", "Comparing with the first one.", canStop: false);
        TrackResult result;
        try
        {
            await ScanAsync();
            result = await tracker.FinishAsync(_programs, progress, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error("Install tracking could not finish", ex);
            EndBusy();
            _dialogs.ShowMessage("Tracking could not finish", ex.Message, isError: true);
            return;
        }

        EndBusy();
        IsTracking = tracker.IsTracking;
        _dialogs.ShowMessage(result.Linked.Count > 0 ? "Install tracked" : "Nothing saved", result.Message, isError: false);
    }

    private void CancelTracking()
    {
        _services.Tracker.Cancel();
        IsTracking = false;
        SetNote("Install tracking cancelled.");
    }
}
