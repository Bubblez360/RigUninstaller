using System.Globalization;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Turns raw registry values into an <see cref="InstalledProgram"/>, applying the skip rules.</summary>
public static class UninstallEntryParser
{
    /// <summary>The only values the reader needs to fetch from each key.</summary>
    public static readonly string[] RequiredValueNames =
    {
        "DisplayName", "DisplayVersion", "Publisher", "InstallDate", "InstallLocation", "EstimatedSize",
        "DisplayIcon", "UninstallString", "QuietUninstallString", "SystemComponent", "ParentKeyName",
    };

    private static readonly string[] DateFormats = { "yyyyMMdd", "yyyy/MM/dd", "yyyy-MM-dd", "M/d/yyyy" };

    public static ParseResult Parse(RegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var name = GetString(entry, "DisplayName");
        if (name is null)
        {
            return new ParseResult(null, SkipReason.NoName);
        }

        if (IsFlagSet(entry, "SystemComponent"))
        {
            return new ParseResult(null, SkipReason.SystemComponent);
        }

        // Updates and patches point at the program they belong to.
        if (GetString(entry, "ParentKeyName") is not null)
        {
            return new ParseResult(null, SkipReason.Update);
        }

        var program = new InstalledProgram
        {
            Name = name,
            Version = GetString(entry, "DisplayVersion"),
            Publisher = GetString(entry, "Publisher"),
            InstallDate = ParseDate(GetValue(entry, "InstallDate")),
            InstallLocation = GetString(entry, "InstallLocation"),
            EstimatedSizeKb = ParseSizeKb(GetValue(entry, "EstimatedSize")),
            DisplayIcon = GetString(entry, "DisplayIcon"),
            UninstallString = GetString(entry, "UninstallString"),
            QuietUninstallString = GetString(entry, "QuietUninstallString"),
            RegistryKeyPath = entry.KeyPath,
            KeyName = entry.KeyPath[(entry.KeyPath.LastIndexOf('\\') + 1)..],
        };
        return new ParseResult(program, SkipReason.None);
    }

    private static object? GetValue(RegistryEntry entry, string name) =>
        entry.Values.TryGetValue(name, out var value) ? value : null;

    private static string? GetString(RegistryEntry entry, string name)
    {
        if (GetValue(entry, name) is not string text)
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsFlagSet(RegistryEntry entry, string name) => GetValue(entry, name) switch
    {
        int number => number == 1,
        long number => number == 1,
        string text => text.Trim() == "1",
        _ => false,
    };

    private static DateTime? ParseDate(object? value)
    {
        var text = value switch
        {
            string s => s.Trim(),
            int or long => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => null,
        };

        return text is { Length: > 0 }
               && DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static long? ParseSizeKb(object? value)
    {
        long? size = value switch
        {
            // A DWORD above int.MaxValue comes back negative; reinterpret it as unsigned.
            int number => unchecked((uint)number),
            uint number => number,
            long number => number,
            string text when long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };

        // Zero means "not recorded", same as missing.
        return size is > 0 ? size : null;
    }
}
