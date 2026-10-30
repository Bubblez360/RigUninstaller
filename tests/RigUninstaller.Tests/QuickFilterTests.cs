using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>The rules behind the "Quick filter" menu: which programs each filter keeps.</summary>
public class QuickFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0);

    private static InstalledProgram P(DateTime? installed = null, long? sizeKb = null) => new()
    {
        Name = "Sample",
        InstallDate = installed,
        EstimatedSizeKb = sizeKb,
        RegistryKeyPath = @"HKLM\SOFTWARE\x\Sample",
        KeyName = "Sample",
    };

    private static QuickFilterFacts Facts(
        InstalledProgram program,
        DateTime? lastUsed = null,
        bool risky = false,
        bool broken = false,
        bool bloat = false) => new(program, lastUsed, risky, broken, bloat);

    [Fact]
    public void All_keeps_everything()
    {
        Assert.True(QuickFilters.Matches(QuickFilter.All, Facts(P()), Now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Recently_installed_keeps_the_last_30_days(int daysAgo, bool expected)
    {
        var program = P(installed: Now.Date.AddDays(-daysAgo));

        Assert.Equal(expected, QuickFilters.Matches(QuickFilter.RecentlyInstalled, Facts(program), Now));
    }

    [Fact]
    public void Recently_installed_drops_programs_with_no_install_date()
    {
        Assert.False(QuickFilters.Matches(QuickFilter.RecentlyInstalled, Facts(P(installed: null)), Now));
    }

    [Theory]
    [InlineData(181, true)]
    [InlineData(180, false)]
    [InlineData(10, false)]
    public void Not_used_lately_keeps_programs_last_used_over_180_days_ago(int daysAgo, bool expected)
    {
        var facts = Facts(P(), lastUsed: Now.AddDays(-daysAgo));

        Assert.Equal(expected, QuickFilters.Matches(QuickFilter.NotUsedLately, facts, Now));
    }

    [Fact]
    public void Not_used_lately_never_guesses_when_the_last_use_is_unknown()
    {
        Assert.False(QuickFilters.Matches(QuickFilter.NotUsedLately, Facts(P(), lastUsed: null), Now));
    }

    [Theory]
    [InlineData(512_000L, true)]
    [InlineData(512_001L, true)]
    [InlineData(512_000L - 1, false)]
    public void Large_keeps_programs_of_500_MB_or_more(long sizeKb, bool expected)
    {
        Assert.Equal(expected, QuickFilters.Matches(QuickFilter.Large, Facts(P(sizeKb: sizeKb)), Now));
    }

    [Fact]
    public void Large_drops_programs_that_report_no_size()
    {
        Assert.False(QuickFilters.Matches(QuickFilter.Large, Facts(P(sizeKb: null)), Now));
    }

    [Fact]
    public void Risky_broken_and_bloatware_follow_their_own_flag()
    {
        Assert.True(QuickFilters.Matches(QuickFilter.Risky, Facts(P(), risky: true), Now));
        Assert.False(QuickFilters.Matches(QuickFilter.Risky, Facts(P(), broken: true, bloat: true), Now));

        Assert.True(QuickFilters.Matches(QuickFilter.Broken, Facts(P(), broken: true), Now));
        Assert.False(QuickFilters.Matches(QuickFilter.Broken, Facts(P(), risky: true, bloat: true), Now));

        Assert.True(QuickFilters.Matches(QuickFilter.Bloatware, Facts(P(), bloat: true), Now));
        Assert.False(QuickFilters.Matches(QuickFilter.Bloatware, Facts(P(), risky: true, broken: true), Now));
    }

    [Fact]
    public void Every_filter_has_a_label_and_a_hint()
    {
        foreach (var filter in Enum.GetValues<QuickFilter>())
        {
            Assert.False(string.IsNullOrWhiteSpace(QuickFilters.Label(filter)));
            Assert.False(string.IsNullOrWhiteSpace(QuickFilters.Hint(filter)));
        }
    }
}
