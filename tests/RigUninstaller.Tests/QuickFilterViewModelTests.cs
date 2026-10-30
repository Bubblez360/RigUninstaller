using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>The Quick filter menu as the main window uses it.</summary>
public class QuickFilterViewModelTests : VmTestBase
{
    private static ProgramInsight Used(DateTime? when, string? orphan = null) => new(
        new SignatureInfo(SignatureState.Signed, "Acme Corp", "ok"),
        new LastUsedInfo(when, "Windows Prefetch data"),
        orphan);

    [Fact]
    public async Task Starts_on_all_programs()
    {
        var vm = await ScannedAsync(Program("Alpha"), Program("Beta"));

        Assert.Equal(QuickFilter.All, vm.QuickFilter);
        Assert.False(vm.IsQuickFilterActive);
        Assert.Equal(new[] { "Alpha", "Beta" }, RowNames(vm));
    }

    [Fact]
    public async Task Large_keeps_only_big_programs_and_All_brings_the_rest_back()
    {
        var vm = await ScannedAsync(Program("Big", sizeKb: 2_000_000), Program("Small", sizeKb: 1_000), Program("Unknown"));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Large);

        Assert.Equal(new[] { "Big" }, RowNames(vm));
        Assert.True(vm.IsQuickFilterActive);

        vm.SetQuickFilterCommand.Execute(QuickFilter.All);

        Assert.Equal(new[] { "Big", "Small", "Unknown" }, RowNames(vm));
        Assert.False(vm.IsQuickFilterActive);
    }

    [Fact]
    public async Task Recently_installed_uses_the_install_date()
    {
        var vm = await ScannedAsync(
            Program("Fresh", date: DateTime.Today.AddDays(-3)),
            Program("Old", date: DateTime.Today.AddDays(-400)),
            Program("Undated"));

        vm.SetQuickFilterCommand.Execute(QuickFilter.RecentlyInstalled);

        Assert.Equal(new[] { "Fresh" }, RowNames(vm));
    }

    [Fact]
    public async Task Risky_and_bloatware_use_the_row_tags()
    {
        _bloat.Names.Add("junk");
        var vm = await ScannedAsync(Program("NVIDIA Graphics Driver 551.23"), Program("Junk Tool"), Program("Plain"));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Risky);
        Assert.Equal(new[] { "NVIDIA Graphics Driver 551.23" }, RowNames(vm));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Bloatware);
        Assert.Equal(new[] { "Junk Tool" }, RowNames(vm));
    }

    [Fact]
    public async Task Not_used_lately_and_broken_fill_in_when_the_background_check_finishes()
    {
        _insights.Handler = p => p.Name switch
        {
            "Dusty" => Used(DateTime.Today.AddDays(-400)),
            "Ghost" => Used(null, orphan: "The uninstaller file is missing."),
            _ => Used(DateTime.Today.AddDays(-2)),
        };
        var vm = await ScannedAsync(Program("Dusty"), Program("Ghost"), Program("Daily"));

        vm.SetQuickFilterCommand.Execute(QuickFilter.NotUsedLately);
        Assert.Equal(new[] { "Dusty" }, RowNames(vm));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Broken);
        Assert.Equal(new[] { "Ghost" }, RowNames(vm));
    }

    [Fact]
    public async Task The_filter_and_the_search_box_work_together()
    {
        var vm = await ScannedAsync(Program("Big Editor", sizeKb: 2_000_000), Program("Big Player", sizeKb: 2_000_000), Program("Tiny Editor", sizeKb: 10));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Large);
        vm.SearchText = "editor";

        Assert.Equal(new[] { "Big Editor" }, RowNames(vm));
    }

    [Fact]
    public async Task The_status_line_and_the_empty_list_name_the_filter()
    {
        var vm = await ScannedAsync(Program("Big", sizeKb: 2_000_000), Program("Small", sizeKb: 1_000));

        vm.SetQuickFilterCommand.Execute(QuickFilter.Large);
        Assert.Contains("1 of 2 programs", vm.StatusText);
        Assert.Contains(QuickFilters.Label(QuickFilter.Large), vm.StatusText);

        vm.SetQuickFilterCommand.Execute(QuickFilter.Broken);
        Assert.True(vm.HasNoResults);
        Assert.Contains(QuickFilters.Label(QuickFilter.Broken), vm.NoResultsText);
        Assert.DoesNotContain("match \"\"", vm.NoResultsText);
        Assert.Equal(QuickFilters.Hint(QuickFilter.Broken), vm.NoResultsAdvice);

        vm.SetQuickFilterCommand.Execute(QuickFilter.All);
        vm.SearchText = "zzz";
        Assert.Contains("spelling", vm.NoResultsAdvice);
    }

    [Fact]
    public async Task The_filter_button_says_what_is_on()
    {
        var vm = await ScannedAsync(Program("Alpha"));

        Assert.Equal("Quick filter", vm.QuickFilterButtonText);

        vm.SetQuickFilterCommand.Execute(QuickFilter.Risky);

        Assert.Equal("Filter: " + QuickFilters.Label(QuickFilter.Risky), vm.QuickFilterButtonText);
    }

    [Fact]
    public async Task A_rescan_keeps_the_filter()
    {
        var vm = await ScannedAsync(Program("Big", sizeKb: 2_000_000), Program("Small", sizeKb: 1_000));
        vm.SetQuickFilterCommand.Execute(QuickFilter.Large);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(QuickFilter.Large, vm.QuickFilter);
        Assert.Equal(new[] { "Big" }, RowNames(vm));
    }

    [Fact]
    public async Task An_unknown_parameter_changes_nothing()
    {
        var vm = await ScannedAsync(Program("Alpha"));

        vm.SetQuickFilterCommand.Execute("nonsense");
        vm.SetQuickFilterCommand.Execute((QuickFilter)999);

        Assert.Equal(QuickFilter.All, vm.QuickFilter);
    }
}
