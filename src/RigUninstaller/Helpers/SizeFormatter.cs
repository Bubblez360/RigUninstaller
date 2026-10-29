using System.Globalization;

namespace RigUninstaller.Helpers;

/// <summary>Turns byte counts into short, readable text (binary units, like Windows Explorer).</summary>
public static class SizeFormatter
{
    public const string Unknown = "—";

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long? bytes, CultureInfo? culture = null)
    {
        if (bytes is null)
        {
            return Unknown;
        }

        culture ??= CultureInfo.CurrentCulture;

        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole numbers for B/KB, one decimal for MB, two for GB and up.
        var format = unit <= 1 ? "0" : unit == 2 ? "0.#" : "0.##";
        return $"{value.ToString(format, culture)} {Units[unit]}";
    }
}
