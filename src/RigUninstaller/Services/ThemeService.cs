using System.Windows;
using Microsoft.Win32;

namespace RigUninstaller.Services;

public interface IThemeService
{
    bool IsDark { get; }

    void Apply(bool dark);

    void Toggle();
}

/// <summary>Swaps the first merged resource dictionary (Light.xaml / Dark.xaml) at runtime.</summary>
public sealed class ThemeService : IThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // "component" form names the assembly, so it also resolves when the app is hosted by something else (tests).
    private static readonly Uri LightUri = new("pack://application:,,,/RigUninstaller;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri = new("pack://application:,,,/RigUninstaller;component/Themes/Dark.xaml", UriKind.Absolute);

    /// <summary>Raised after the theme changes. Windows listen to it to repaint their title bar.</summary>
    public static event Action<bool>? ThemeChanged;

    /// <summary>The theme currently applied (false before the first Apply).</summary>
    public static bool CurrentIsDark { get; private set; }

    public bool IsDark { get; private set; }

    /// <summary>Reads the Windows "app mode" setting. Falls back to light if it cannot be read.</summary>
    public static bool DetectSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Apply(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = dark ? DarkUri : LightUri };

        // Slot 0 is always the theme dictionary; Controls.xaml sits after it.
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = theme;
        }
        else
        {
            dictionaries.Add(theme);
        }

        IsDark = dark;
        CurrentIsDark = dark;
        ThemeChanged?.Invoke(dark);
    }

    public void Toggle() => Apply(!IsDark);
}
