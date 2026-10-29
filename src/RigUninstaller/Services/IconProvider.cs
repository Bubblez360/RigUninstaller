using System.Collections.Concurrent;
using System.Windows.Media;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Finds the icon of an installed program. Returns null when there is none (the UI draws a letter tile).</summary>
public interface IIconProvider
{
    ImageSource? GetIcon(InstalledProgram program);
}

/// <summary>Reads icons from the file named in DisplayIcon. Safe to call from background threads.</summary>
public sealed class IconProvider : IIconProvider
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(InstalledProgram program)
    {
        var location = DisplayIconParser.Parse(program.DisplayIcon);
        if (location is null || !CommandIconProvider.IsLocalExistingFile(location.Path))
        {
            return null;
        }

        return _cache.GetOrAdd($"{location.Path}|{location.Index}", _ => IconExtractor.FromIconFile(location.Path, location.Index));
    }
}
