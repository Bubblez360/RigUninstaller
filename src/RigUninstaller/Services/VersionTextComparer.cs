using System.Numerics;
using System.Text.RegularExpressions;

namespace RigUninstaller.Services;

/// <summary>Compares version texts by their numbers, so "2.0" sorts before "10.0".</summary>
public sealed class VersionTextComparer : IComparer<string?>
{
    private static readonly Regex Numbers = new(@"\d+", RegexOptions.Compiled);

    public static VersionTextComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        var a = Parse(x);
        var b = Parse(y);

        if (a.Length == 0 || b.Length == 0)
        {
            // Text without digits: a version with numbers counts as higher; two plain texts compare as text.
            return a.Length == b.Length
                ? string.Compare(x, y, StringComparison.OrdinalIgnoreCase)
                : a.Length.CompareTo(b.Length);
        }

        var common = Math.Min(a.Length, b.Length);
        for (var i = 0; i < common; i++)
        {
            var result = a[i].CompareTo(b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        // "1.0" and "1.0.0" are the same; extra non-zero parts make a version higher.
        if (a.Length > common && a.Skip(common).Any(n => !n.IsZero))
        {
            return 1;
        }

        if (b.Length > common && b.Skip(common).Any(n => !n.IsZero))
        {
            return -1;
        }

        return 0;
    }

    private static BigInteger[] Parse(string? text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<BigInteger>()
            : Numbers.Matches(text).Select(m => BigInteger.Parse(m.Value)).ToArray();
}
