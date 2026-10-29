using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RigUninstaller.Models;
using RigUninstaller.ViewModels;
using RigUninstaller.Views;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>The "Find by window" crosshair, driven with synthetic mouse events on the real window.</summary>
public sealed class TargetModeTests : VmTestBase
{
    private MainViewModel MakeViewModel()
    {
        _scanner.Result = new ScanResult(new[] { Program("Notes", location: @"C:\Program Files\Notes") }, 1, 0, 0, 0, 0);
        _windowProbe.Path = @"C:\Program Files\Notes\notes.exe";
        var viewModel = Create();
        viewModel.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return viewModel;
    }

    private static void RaiseMouse(UIElement target, RoutedEvent routedEvent) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = target,
        });

    [Fact]
    public void Pressing_on_the_button_captures_the_mouse_and_releasing_elsewhere_selects_the_program_and_frees_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                foreach (var name in new[] { "Dark", "Controls", "ControlsExtra" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/RigUninstaller;component/Themes/{name}.xaml") });
                }

                var viewModel = MakeViewModel();
                var window = new MainWindow { DataContext = viewModel, ShowInTaskbar = false, Left = -20000, Top = 0, Width = 1100, Height = 600 };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var button = (Button)window.FindName("TargetButton");

                RaiseMouse(button, UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Same(button, Mouse.Captured);
                RaiseMouse(button, UIElement.PreviewMouseLeftButtonUpEvent);

                Assert.Null(Mouse.Captured);
                Assert.Null(Mouse.OverrideCursor);
                Assert.Equal("Notes", viewModel.SelectedRow?.Name);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void The_click_hint_tells_the_user_what_to_do_next()
    {
        var viewModel = MakeViewModel();

        viewModel.AnnounceTargetMode();

        Assert.Contains("click the window", viewModel.StatusText);
        Assert.Contains("Esc", viewModel.StatusText);
    }
}
