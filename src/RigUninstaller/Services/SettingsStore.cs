using System.Text.Json;
using System.Windows;

namespace RigUninstaller.Services;

/// <summary>What the app remembers between runs. Every value is optional: nothing saved means the normal default.</summary>
public sealed record UserSettings
{
    /// <summary>True dark, false light, null follow Windows.</summary>
    public bool? Dark { get; init; }

    /// <summary>Name of the program-list column the list is sorted by.</summary>
    public string? SortColumn { get; init; }

    public bool SortDescending { get; init; }

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }
}

public interface ISettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

/// <summary>Keeps the settings in <c>Data\settings.json</c>. A missing or damaged file just means defaults.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IActionLog _log;

    public SettingsStore(string path, IActionLog log)
    {
        _path = path;
        _log = log;
    }

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new UserSettings();
            }

            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), Options) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Could not read the saved settings, using the defaults: {ex.Message}");
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write beside the file and swap, so a crash never leaves half a settings file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save one must never get in the user's way.
            _log.Warn($"Could not save the settings: {ex.Message}");
        }
    }
}

/// <summary>Decides whether the window position that was saved last time can be used again.</summary>
public static class WindowPlacement
{
    private const double MinVisibleWidth = 160;
    private const double MinVisibleHeight = 60;

    /// <summary>
    /// The saved window rectangle, or null when none was saved or it would sit (mostly) off every screen,
    /// for example after a monitor was unplugged. The size is limited to the screens' combined area.
    /// </summary>
    public static Rect? Fit(UserSettings settings, Rect virtualScreen, double minWidth, double minHeight)
    {
        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top
            || settings.WindowWidth is not { } width || settings.WindowHeight is not { } height
            || !IsFinite(left, top, width, height))
        {
            return null;
        }

        var fitted = new Rect(
            left,
            top,
            Math.Clamp(width, minWidth, Math.Max(minWidth, virtualScreen.Width)),
            Math.Clamp(height, minHeight, Math.Max(minHeight, virtualScreen.Height)));

        // The title bar must be reachable with the mouse, or the window could not be dragged back.
        var titleBar = new Rect(fitted.Left, fitted.Top, fitted.Width, MinVisibleHeight);
        var visible = Rect.Intersect(titleBar, virtualScreen);
        return !visible.IsEmpty && visible.Width >= MinVisibleWidth && visible.Height >= MinVisibleHeight ? fitted : null;
    }

    private static bool IsFinite(params double[] values) => values.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
}
