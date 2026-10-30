using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Views;

/// <summary>
/// View-only behaviour that XAML cannot express: focus handling, keeping the grid's sort arrows in step with the
/// view model, selecting the row under a right click, and building the Quick filter and Open windows menus.
/// All real logic (search, sort, selection, uninstall) lives in MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => FocusSearch()));
        DataContextChanged += OnDataContextChanged;
        ProgramGrid.PreviewMouseRightButtonDown += OnGridRightClick;
        Loaded += (_, _) =>
        {
            FocusSearch();
            SyncSortArrows();
            ShowWingetColumn(ViewModel?.HasWingetData == true);
        };
    }

    // ---- remember the window ----

    /// <summary>Comes back where it was last time, if that place is still on a screen.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (WindowPlacement.Fit(viewModel.SavedSettings, screen, MinWidth, MinHeight) is { } place)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = place.Left;
            Top = place.Top;
            Width = place.Width;
            Height = place.Height;
        }

        if (viewModel.SavedSettings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && WindowState != WindowState.Minimized)
        {
            ViewModel?.SaveWindow(RestoreBounds, WindowState == WindowState.Maximized);
        }
    }

    // ---- custom title bar buttons ----

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>A maximised chrome window overhangs the screen by its resize border, so pull the content back in.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        WindowRoot.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.SetValue(AutomationProperties.NameProperty, maximized ? "Restore" : "Maximize");
    }

    /// <summary>Opens the "More tools" menu right under its button.</summary>
    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        if (ToolsButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ToolsButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private const int OpenWindowTitleLimit = 60;

    /// <summary>Lists the windows open right now; picking one selects its program. Programs not in the list are shown greyed out.</summary>
    private void OnOpenWindowsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || OpenWindowsButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();
        foreach (var choice in viewModel.GetOpenWindowChoices())
        {
            var title = choice.Title.Length > OpenWindowTitleLimit ? choice.Title[..OpenWindowTitleLimit] + "…" : choice.Title;
            var item = new MenuItem
            {
                Header = choice.ProgramName is null ? $"{title}  (not an installed program)" : $"{choice.ProgramName}  ·  {title}",
                IsEnabled = choice.ProgramName is not null,
                ToolTip = choice.ExePath,
            };
            item.Click += (_, _) => viewModel.SelectProgramOfWindow(choice);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No open windows found", IsEnabled = false });
        }

        menu.PlacementTarget = OpenWindowsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // The winget column stays out of the way until "Check winget" has something to put in it.
    private void ShowWingetColumn(bool visible) => WingetColumn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Header clicks go to the view model, which sorts (unknown values always last).</summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        ViewModel?.SortCommand.Execute(e.Column.SortMemberPath);
    }

    /// <summary>A right click selects the row under the mouse first, so the menu acts on it.</summary>
    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    // ---- quick filter ----

    /// <summary>Opens the Quick filter menu under its button. Built here from <see cref="QuickFilters"/>, so the wording lives in one place.</summary>
    private void OnQuickFilterClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = QuickFilterButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        foreach (var filter in Enum.GetValues<QuickFilter>())
        {
            menu.Items.Add(new MenuItem
            {
                Header = QuickFilters.Label(filter),
                ToolTip = QuickFilters.Hint(filter),
                IsChecked = viewModel.QuickFilter == filter,
                Command = viewModel.SetQuickFilterCommand,
                CommandParameter = filter,
            });

            if (filter == QuickFilter.All)
            {
                menu.Items.Add(new Separator());
            }
        }

        menu.IsOpen = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            oldViewModel.RowRevealRequested -= OnRowRevealRequested;
        }

        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            newViewModel.RowRevealRequested += OnRowRevealRequested;
        }
    }

    private void OnRowRevealRequested(ProgramRowViewModel row)
    {
        // The list may have just been rebuilt (the search was cleared), so scroll after it has settled.
        Dispatcher.BeginInvoke(() => ProgramGrid.ScrollIntoView(row), DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasWingetData))
        {
            ShowWingetColumn(ViewModel?.HasWingetData == true);
        }

        if (e.PropertyName is nameof(MainViewModel.SortColumn) or nameof(MainViewModel.SortDescending) or nameof(MainViewModel.Rows))
        {
            // Run after the grid has taken the new rows, since a new ItemsSource can reset the arrows.
            Dispatcher.BeginInvoke(SyncSortArrows, DispatcherPriority.Background);
        }
    }

    private void SyncSortArrows()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var sorted = viewModel.SortColumn.ToString();
        foreach (var column in ProgramGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == sorted
                ? (viewModel.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }
}
