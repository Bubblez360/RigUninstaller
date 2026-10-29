using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace RigUninstaller.Services;

/// <summary>Finds the icon of a startup item from its command line. Null when there is none.</summary>
public interface ICommandIconProvider
{
    ImageSource? GetIcon(string command);
}

/// <summary>Pulls the program file out of a command line such as <c>"C:\App\app.exe" --start</c>.</summary>
public static class CommandPathParser
{
    public static string? ExtractFilePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(command.Trim());

        // Network paths are never touched: File.Exists on a dead share waits out a long timeout (and sends credentials).
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("\"\\\\", StringComparison.Ordinal))
        {
            return null;
        }

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? Resolve(text[1..end]) : null;
        }

        if (File.Exists(text))
        {
            return text;
        }

        // An unquoted path with spaces followed by arguments: grow the candidate until a file exists.
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = string.Empty;
        foreach (var part in parts)
        {
            candidate = candidate.Length == 0 ? part : candidate + " " + part;
            if (Resolve(candidate) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // Bare names like "cmd.exe" live in the system folder.
    private static string? Resolve(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        var inSystem = Path.Combine(Environment.SystemDirectory, path);
        return path.Length > 0 && !path.Contains('\\') && File.Exists(inSystem) ? inSystem : null;
    }
}

public sealed class CommandIconProvider : ICommandIconProvider
{
    private static readonly string[] IconFileTypes = { ".exe", ".dll", ".ico" };

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(string command)
    {
        var path = CommandPathParser.ExtractFilePath(command);
        if (path is null || !IsLocalExistingFile(path))
        {
            return null;
        }

        return _cache.GetOrAdd(path, Extract);
    }

    // Network paths are skipped on purpose: a dead share would freeze the scan.
    internal static bool IsLocalExistingFile(string path) =>
        Path.IsPathRooted(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(path);

    private static ImageSource? Extract(string path)
    {
        if (IconFileTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && IconExtractor.FromIconFile(path, 0) is { } own)
        {
            return own;
        }

        return IconExtractor.FromShell(path);
    }
}

/// <summary>Finds the tile logo of a Store app. Null when there is none.</summary>
public interface IStoreIconProvider
{
    ImageSource? GetIcon(StoreApp app);
}

/// <summary>Reads the logo named in the package's AppxManifest.xml and loads the best-sized picture for it.</summary>
public sealed class StoreIconProvider : IStoreIconProvider
{
    private static readonly Regex TargetSize = new(@"targetsize-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] LogoAttributes = { "Square44x44Logo", "Square150x150Logo" };

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(StoreApp app)
    {
        if (app.InstallLocation.Length == 0)
        {
            return null;
        }

        return _cache.GetOrAdd(app.InstallLocation, Load);
    }

    private static ImageSource? Load(string installLocation)
    {
        try
        {
            var logo = FindLogoFile(installLocation);
            return logo is null ? null : LoadPicture(logo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever is wrong with one package's logo (bad XML, broken picture, decoder errors), only that row
            // shows the placeholder picture; it must never stop the whole list from loading.
            return null;
        }
    }

    private static string? FindLogoFile(string installLocation)
    {
        var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = XDocument.Load(manifestPath);
        var visual = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        var relative = LogoAttributes
                           .Select(attribute => visual?.Attribute(attribute)?.Value)
                           .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                       ?? manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var wanted = Path.GetFullPath(Path.Combine(installLocation, relative.Replace('/', '\\')));
        var directory = Path.GetDirectoryName(wanted);
        var package = installLocation.TrimEnd('\\') + "\\";
        if (directory is null || !(directory + "\\").StartsWith(package, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(wanted);
        var extension = Path.GetExtension(wanted);
        return Directory.EnumerateFiles(directory, name + "*" + extension)
            .Select(file => (File: file, Rank: Rank(Path.GetFileName(file), wanted == file)))
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => candidate.File)
            .FirstOrDefault();
    }

    /// <summary>Lower is better. Picks roughly 48 px pictures, then the 200% and 100% scales.</summary>
    public static int Rank(string fileName, bool isExactName)
    {
        // High-contrast and light-theme variants are drawn for other backgrounds.
        if (fileName.Contains("contrast-", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("lightunplated", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var unplatedBonus = fileName.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        if (TargetSize.Match(fileName) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var size))
        {
            return size >= 32 ? 10 + Math.Abs(size - 48) + unplatedBonus : 200 - size;
        }

        if (fileName.Contains("scale-200", StringComparison.OrdinalIgnoreCase)) return 300;
        if (fileName.Contains("scale-100", StringComparison.OrdinalIgnoreCase)) return 310;
        if (fileName.Contains("scale-400", StringComparison.OrdinalIgnoreCase)) return 320;
        return isExactName ? 330 : 340;
    }

    private static BitmapImage LoadPicture(string file)
    {
        // Our own stream, so the file is released even when the picture turns out to be broken.
        using var stream = File.OpenRead(file);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = 48;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
