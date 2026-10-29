using System.Globalization;

namespace RigUninstaller.Services;

/// <summary>Where to take an icon from: a file and the icon index inside it.</summary>
public sealed record IconLocation(string Path, int Index);

/// <summary>Understands the DisplayIcon registry value: <c>"C:\app.exe",0</c>, <c>C:\app.exe,-101</c>, <c>C:\app.ico</c>.</summary>
public static class DisplayIconParser
{
    public static IconLocation? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(raw.Trim());
        var path = text;
        var index = 0;

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                path = text[1..];
            }
            else
            {
                path = text[1..close];
                var rest = text[(close + 1)..].Trim();
                if (rest.StartsWith(',') && TryParseIndex(rest[1..], out var quotedIndex))
                {
                    index = quotedIndex;
                }
            }
        }
        else
        {
            // Only a comma followed by a number counts as an index, so folders with commas still work.
            var comma = text.LastIndexOf(',');
            if (comma >= 0 && TryParseIndex(text[(comma + 1)..], out var parsedIndex))
            {
                path = text[..comma];
                index = parsedIndex;
            }
        }

        path = path.Trim().Trim('"').Trim();
        return path.Length == 0 ? null : new IconLocation(path, index);
    }

    private static bool TryParseIndex(string text, out int index) =>
        int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);
}
