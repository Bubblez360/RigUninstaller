using System.Diagnostics;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>Backs the "View log" window: shows the tail of the action log.</summary>
public sealed class LogViewModel : ViewModelBase
{
    private const int MaxLines = 1000;

    private readonly IActionLog _log;
    private string _text = string.Empty;

    public LogViewModel(IActionLog log)
    {
        _log = log;
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        Refresh();
    }

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string LogFilePath => _log.LogFilePath;

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    private void Refresh()
    {
        var tail = _log.ReadTail(MaxLines);
        Text = tail.Length == 0 ? "(log is empty)" : tail;
    }

    private void OpenFolder()
    {
        var folder = Path.GetDirectoryName(_log.LogFilePath);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
