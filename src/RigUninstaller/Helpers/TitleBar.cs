using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RigUninstaller.Services;

namespace RigUninstaller.Helpers;

/// <summary>
/// Makes a window's title bar follow the app theme (dark title bar in dark mode). Set
/// <c>helpers:TitleBar.FollowTheme="True"</c>; the shared window style does it for every window.
/// </summary>
public static class TitleBar
{
    // 20 on Windows 10 2004 and later, 19 on the first builds that had it.
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeOld = 19;

    public static readonly DependencyProperty FollowThemeProperty = DependencyProperty.RegisterAttached(
        "FollowTheme", typeof(bool), typeof(TitleBar), new PropertyMetadata(false, OnFollowThemeChanged));

    public static bool GetFollowTheme(DependencyObject element) => (bool)element.GetValue(FollowThemeProperty);

    public static void SetFollowTheme(DependencyObject element, bool value) => element.SetValue(FollowThemeProperty, value);

    private static void OnFollowThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window || e.NewValue is not true)
        {
            return;
        }

        void Repaint(bool dark) => window.Dispatcher.Invoke(() => Apply(window, dark));

        window.SourceInitialized += (_, _) => Apply(window, ThemeService.CurrentIsDark);
        window.Loaded += (_, _) => ThemeService.ThemeChanged += Repaint;
        window.Closed += (_, _) => ThemeService.ThemeChanged -= Repaint;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window, ThemeService.CurrentIsDark);
        }
    }

    private static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;

        // Older Windows versions do not know the setting and simply ignore it, so a failed call is not an error.
        if (NativeMethods.DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(handle, ImmersiveDarkModeOld, ref value, sizeof(int));
        }
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
