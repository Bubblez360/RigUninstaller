using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Ready-made views of the program list, picked from the "Quick filter" menu.</summary>
public enum QuickFilter
{
    All,
    RecentlyInstalled,
    NotUsedLately,
    Large,
    Risky,
    Broken,
    Bloatware,
}

/// <summary>What the filters need to know about one program. Built by the view model from its row.</summary>
public readonly record struct QuickFilterFacts(InstalledProgram Program, DateTime? LastUsed, bool IsRisky, bool IsBroken, bool IsBloat);

/// <summary>Rules and wording for <see cref="QuickFilter"/>. Pure functions, no UI types.</summary>
public static class QuickFilters
{
    public const int RecentDays = 30;
    public const int UnusedDays = 180;
    public const long LargeBytes = 500L * 1024 * 1024;

    /// <summary>
    /// True when the program belongs in the filtered list. A missing value never matches: a program with no
    /// install date is not "recent", and one with no last-used date is not called unused.
    /// </summary>
    public static bool Matches(QuickFilter filter, QuickFilterFacts facts, DateTime now) => filter switch
    {
        QuickFilter.All => true,
        QuickFilter.RecentlyInstalled => facts.Program.InstallDate is { } installed && installed >= now.Date.AddDays(-RecentDays),
        QuickFilter.NotUsedLately => facts.LastUsed is { } used && used < now.AddDays(-UnusedDays),
        QuickFilter.Large => facts.Program.SizeBytes is { } size && size >= LargeBytes,
        QuickFilter.Risky => facts.IsRisky,
        QuickFilter.Broken => facts.IsBroken,
        QuickFilter.Bloatware => facts.IsBloat,
        _ => true,
    };

    /// <summary>Short name for the menu, the button and the status line.</summary>
    public static string Label(QuickFilter filter) => filter switch
    {
        QuickFilter.RecentlyInstalled => "Installed in the last 30 days",
        QuickFilter.NotUsedLately => "Not used for 6+ months",
        QuickFilter.Large => "Large (500 MB or more)",
        QuickFilter.Risky => "Risky to remove",
        QuickFilter.Broken => "Broken entries",
        QuickFilter.Bloatware => "On the bloatware list",
        _ => "All programs",
    };

    /// <summary>One plain sentence: the menu tooltip, and the text shown when nothing matches.</summary>
    public static string Hint(QuickFilter filter) => filter switch
    {
        QuickFilter.RecentlyInstalled => "Programs installed in the last 30 days. Handy for undoing a recent install.",
        QuickFilter.NotUsedLately => "Programs Windows has not seen you run for 6 months. Programs with no known last-used date are left out.",
        QuickFilter.Large => "Programs that report 500 MB or more. The quickest way to free disk space.",
        QuickFilter.Risky => "Drivers, runtimes and security software. Think twice before removing these.",
        QuickFilter.Broken => "Entries whose uninstaller or install folder is already gone.",
        QuickFilter.Bloatware => "Programs that match your bloatware list (More tools > Edit bloatware list).",
        _ => "Show every installed program.",
    };
}
