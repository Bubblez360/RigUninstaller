using System.Windows;
using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "RigSettings_" + Guid.NewGuid().ToString("N"));
    private readonly FakeLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private SettingsStore Create() => new(Path.Combine(_folder, "Data", "settings.json"), _log);

    [Fact]
    public void Settings_come_back_exactly_as_they_were_saved()
    {
        var saved = new UserSettings { Dark = true, SortColumn = "Size", SortDescending = true, WindowLeft = 10, WindowTop = 20, WindowWidth = 1000, WindowHeight = 700, WindowMaximized = true };

        Create().Save(saved);

        Assert.Equal(saved, Create().Load());
    }

    [Fact]
    public void No_file_means_defaults_and_no_warning()
    {
        var loaded = Create().Load();

        Assert.Null(loaded.Dark);
        Assert.Null(loaded.SortColumn);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("WARN"));
    }

    [Fact]
    public void A_damaged_file_means_defaults_and_a_warning_not_a_crash()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Data"));
        File.WriteAllText(Path.Combine(_folder, "Data", "settings.json"), "{ this is not json");

        var loaded = Create().Load();

        Assert.Equal(new UserSettings(), loaded);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("saved settings"));
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind_and_replaces_the_old_one()
    {
        var store = Create();
        store.Save(new UserSettings { Dark = false });
        store.Save(new UserSettings { Dark = true });

        Assert.True(store.Load().Dark);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "Data"), "*.tmp"));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_a_warning_not_a_crash()
    {
        // A file where the Data folder should be makes the folder impossible to create.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "Data"), "in the way");

        Create().Save(new UserSettings { Dark = true });

        Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("save the settings"));
    }
}

public class WindowPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    private static UserSettings At(double left, double top, double width = 1000, double height = 700) =>
        new() { WindowLeft = left, WindowTop = top, WindowWidth = width, WindowHeight = height };

    [Fact]
    public void A_window_that_is_on_screen_comes_back_as_saved()
    {
        Assert.Equal(new Rect(100, 50, 1000, 700), WindowPlacement.Fit(At(100, 50), Screen, 900, 520));
    }

    [Theory]
    [InlineData(5000, 100)]  // the monitor it was on is gone
    [InlineData(100, 5000)]
    [InlineData(-3000, 100)]
    [InlineData(1900, 100)]  // only 20 px of title bar left on screen
    [InlineData(100, -400)]  // title bar above the screen: cannot be dragged back
    public void A_window_that_would_be_off_screen_is_not_restored(double left, double top)
    {
        Assert.Null(WindowPlacement.Fit(At(left, top), Screen, 900, 520));
    }

    [Fact]
    public void Nothing_saved_or_nonsense_values_give_null()
    {
        Assert.Null(WindowPlacement.Fit(new UserSettings(), Screen, 900, 520));
        Assert.Null(WindowPlacement.Fit(At(double.NaN, 0), Screen, 900, 520));
        Assert.Null(WindowPlacement.Fit(At(0, 0, double.PositiveInfinity), Screen, 900, 520));
    }

    [Fact]
    public void The_size_is_kept_between_the_minimum_and_the_screen()
    {
        var small = WindowPlacement.Fit(At(0, 0, 100, 100), Screen, 900, 520);
        var huge = WindowPlacement.Fit(At(0, 0, 9000, 9000), Screen, 900, 520);

        Assert.Equal(new Size(900, 520), new Size(small!.Value.Width, small.Value.Height));
        Assert.Equal(new Size(1920, 1080), new Size(huge!.Value.Width, huge.Value.Height));
    }

    [Fact]
    public void A_window_on_a_second_monitor_to_the_left_is_fine()
    {
        var both = new Rect(-1920, 0, 3840, 1080);

        Assert.NotNull(WindowPlacement.Fit(At(-1800, 100), both, 900, 520));
    }
}

public class SettingsInViewModelTests : VmTestBase
{
    private sealed class MemorySettings : ISettingsStore
    {
        public UserSettings Current { get; set; } = new();

        public int Saves { get; private set; }

        public UserSettings Load() => Current;

        public void Save(UserSettings settings)
        {
            Current = settings;
            Saves++;
        }
    }

    private MainViewModel CreateWith(MemorySettings settings)
    {
        var services = new AppServices
        {
            Theme = _theme, Windows = _windows, Log = _log, Scanner = _scanner, Icons = _icons, Uninstall = _coordinator,
            Leftovers = _leftovers, Dialogs = _dialogs, Insights = _insights, Keep = _keep, Bloat = _bloat, Winget = _winget,
            Defender = _defender, External = _external, WindowProbe = _windowProbe, Files = _files,
            Guard = new SafetyGuard(TestRoots), ForceRemove = _forceRemove, Batch = _batch, Tracker = _tracker,
            Settings = settings,
        };
        return new MainViewModel(services, isElevated: true);
    }

    [Fact]
    public void The_saved_sort_order_is_used_at_start()
    {
        var vm = CreateWith(new MemorySettings { Current = new UserSettings { SortColumn = "Size", SortDescending = true } });

        Assert.Equal(SortColumn.Size, vm.SortColumn);
        Assert.True(vm.SortDescending);
    }

    [Theory]
    [InlineData("NotAColumn")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_saved_sort_falls_back_to_name_a_to_z(string? saved)
    {
        var vm = CreateWith(new MemorySettings { Current = new UserSettings { SortColumn = saved, SortDescending = true } });

        Assert.Equal(SortColumn.Name, vm.SortColumn);
        Assert.False(vm.SortDescending);
    }

    [Fact]
    public async Task Sorting_by_a_column_is_remembered()
    {
        var store = new MemorySettings();
        _scanner.Result = new ScanResult(new[] { Program("A"), Program("B") }, 2, 0, 0, 0, 0);
        var vm = CreateWith(store);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.SortCommand.Execute("Publisher");
        vm.SortCommand.Execute("Publisher");

        Assert.Equal("Publisher", store.Current.SortColumn);
        Assert.True(store.Current.SortDescending);
    }

    [Fact]
    public void Switching_the_theme_is_remembered()
    {
        var store = new MemorySettings();
        var vm = CreateWith(store);

        vm.ToggleThemeCommand.Execute(null);

        Assert.Equal(_theme.IsDark, store.Current.Dark);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void The_window_position_is_saved_and_other_settings_survive_it()
    {
        var store = new MemorySettings { Current = new UserSettings { Dark = true, SortColumn = "Size" } };
        var vm = CreateWith(store);

        vm.SaveWindow(new Rect(10, 20, 1100, 650), maximized: true);

        Assert.Equal(new UserSettings { Dark = true, SortColumn = "Size", WindowLeft = 10, WindowTop = 20, WindowWidth = 1100, WindowHeight = 650, WindowMaximized = true }, store.Current);
    }

    [Fact]
    public void Without_a_settings_store_everything_still_works()
    {
        var vm = Create();

        vm.ToggleThemeCommand.Execute(null);
        vm.SaveWindow(new Rect(0, 0, 900, 600), maximized: false);

        Assert.Equal(SortColumn.Name, vm.SortColumn);
    }
}
