using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RigUninstaller.Helpers;

/// <summary>The grey "no icon" picture shown for startup items and Store apps whose icon cannot be found.</summary>
public static class NoIcon
{
    private static readonly Lazy<ImageSource?> Picture = new(Load);

    public static ImageSource? Image => Picture.Value;

    private static ImageSource? Load()
    {
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/RigUninstaller;component/Assets/no-icon.png"));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or InvalidOperationException)
        {
            // Without the picture the cell is simply empty.
            return null;
        }
    }
}
