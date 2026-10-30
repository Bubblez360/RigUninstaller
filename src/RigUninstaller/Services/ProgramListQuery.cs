using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Search filter and sort for the program list. Pure functions, no UI types.</summary>
public static class ProgramListQuery
{
    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// True when every word of the query appears in the name, publisher or version. A word like "F:" is a drive:
    /// it keeps only the programs installed on that drive.
    /// </summary>
    public static bool Matches(InstalledProgram program, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var haystack = $"{program.Name} {program.Publisher} {program.Version}";
        return query
            .Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)
            .All(word => IsDriveWord(word)
                ? program.Drive.Equals(word, StringComparison.OrdinalIgnoreCase)
                : haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDriveWord(string word) => word.Length == 2 && char.IsAsciiLetter(word[0]) && word[1] == ':';

    /// <summary>
    /// Filters, then sorts. Programs with no value for the sort column (no size, no date...) always
    /// go last, whichever the direction, and equal values fall back to name order.
    /// </summary>
    public static IReadOnlyList<InstalledProgram> Apply(
        IEnumerable<InstalledProgram> programs,
        string? query,
        SortColumn column,
        bool descending,
        Func<InstalledProgram, DateTime?>? lastUsed = null,
        Func<InstalledProgram, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(programs);

        // "Last used" is not part of the registry data, so the caller says where to find it.
        var used = lastUsed ?? (_ => null);
        var filtered = programs.Where(p => Matches(p, query) && (include?.Invoke(p) ?? true)).ToList();
        var comparer = Comparer<InstalledProgram>.Create((a, b) => Compare(a, b, column, descending, used));

        var known = filtered.Where(p => HasValue(p, column, used)).OrderBy(p => p, comparer);
        var unknown = filtered.Where(p => !HasValue(p, column, used)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        return known.Concat(unknown).ToList();
    }

    private static bool HasValue(InstalledProgram p, SortColumn column, Func<InstalledProgram, DateTime?> used) => column switch
    {
        SortColumn.Publisher => p.Publisher is not null,
        SortColumn.Version => p.Version is not null,
        SortColumn.Drive => p.Drive != DiskUsage.UnknownDrive,
        SortColumn.Size => p.SizeBytes is not null,
        SortColumn.InstallDate => p.InstallDate is not null,
        SortColumn.LastUsed => used(p) is not null,
        _ => true,
    };

    private static int Compare(InstalledProgram a, InstalledProgram b, SortColumn column, bool descending, Func<InstalledProgram, DateTime?> used)
    {
        var result = column switch
        {
            SortColumn.Publisher => StringComparer.CurrentCultureIgnoreCase.Compare(a.Publisher, b.Publisher),
            SortColumn.Version => VersionTextComparer.Instance.Compare(a.Version, b.Version),
            SortColumn.Drive => StringComparer.OrdinalIgnoreCase.Compare(a.Drive, b.Drive),
            SortColumn.Size => Nullable.Compare(a.SizeBytes, b.SizeBytes),
            SortColumn.InstallDate => Nullable.Compare(a.InstallDate, b.InstallDate),
            SortColumn.LastUsed => Nullable.Compare(used(a), used(b)),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
        };

        if (descending)
        {
            result = -result;
        }

        // Tiebreaker is always name A-Z so equal rows keep a predictable order.
        return result != 0 ? result : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    }
}
