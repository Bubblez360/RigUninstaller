using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RigUninstaller.Services;

/// <summary>Turns a file (exe, dll, ico, shortcut...) into a frozen WPF image. Safe to call from background threads.</summary>
internal static class IconExtractor
{
    private const int IconSize = 32;
    private const uint ShgfiIcon = 0x100; // with no size flag this is the large (32x32) icon

    /// <summary>The icon at <paramref name="index"/> of an exe, dll or ico file, or null when it has none.</summary>
    public static ImageSource? FromIconFile(string path, int index)
    {
        var handles = new IntPtr[1];
        try
        {
            var count = NativeMethods.PrivateExtractIcons(path, index, IconSize, IconSize, handles, null, 1, 0);
            return count == 0 || count == uint.MaxValue || handles[0] == IntPtr.Zero ? null : Convert(handles[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException or IOException)
        {
            // A bad icon only means the row shows a placeholder instead.
            return null;
        }
        finally
        {
            Destroy(handles[0]);
        }
    }

    /// <summary>The icon Explorer shows for any file, including shortcuts (.lnk) and scripts.</summary>
    public static ImageSource? FromShell(string path)
    {
        var info = new ShellFileInfo();
        try
        {
            if (NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon) == IntPtr.Zero
                || info.Icon == IntPtr.Zero)
            {
                return null;
            }

            return Convert(info.Icon);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException or IOException)
        {
            return null;
        }
        finally
        {
            Destroy(info.Icon);
        }
    }

    private static ImageSource Convert(IntPtr handle)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze(); // frozen images can be handed to the UI thread
        return source;
    }

    private static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint PrivateExtractIcons(
            string fileName,
            int iconIndex,
            int width,
            int height,
            [Out] IntPtr[] icons,
            [Out] uint[]? iconIds,
            uint count,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint size, uint flags);
    }
}
