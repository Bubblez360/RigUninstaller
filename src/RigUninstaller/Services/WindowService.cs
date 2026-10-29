using System.Windows;
using RigUninstaller.ViewModels;
using RigUninstaller.Views;

namespace RigUninstaller.Services;

/// <summary>Lets view models open windows without referencing any window type.</summary>
public interface IWindowService
{
    void ShowLog();
}

public sealed class WindowService : IWindowService
{
    private readonly IActionLog _log;
    private LogWindow? _logWindow;

    public WindowService(IActionLog log)
    {
        _log = log;
    }

    public void ShowLog()
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow
        {
            DataContext = new LogViewModel(_log),
            Owner = Application.Current.MainWindow,
        };
        _logWindow.Show();
    }
}
