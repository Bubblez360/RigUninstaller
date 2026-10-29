using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class LeftoversViewModelTests
{
    private static InstalledProgram Program() => new()
    {
        Name = "Acme Editor",
        RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
        KeyName = "Acme",
    };

    private static readonly LeftoverItem HighFolder = new(LeftoverKind.Folder, @"C:\Program Files\Acme Editor", Confidence.High, "same name");
    private static readonly LeftoverItem MediumFile = new(LeftoverKind.File, @"C:\ProgramData\Acme\x.lnk", Confidence.Medium, "similar name");
    private static readonly LeftoverItem HighKey = new(LeftoverKind.RegistryKey, @"HKCU\Software\AcmeEditor", Confidence.High, "same name");
    private static readonly LeftoverItem LowFolder = new(LeftoverKind.Folder, @"C:\Program Files\Acme", Confidence.Low, "publisher");

    private static LeftoversViewModel Create(bool restoreAlready = false) =>
        new(Program(), new[] { HighFolder, MediumFile, HighKey, LowFolder }, restoreAlready);

    private static string[] Shown(LeftoversViewModel vm) => vm.VisibleRows.Select(r => r.Item.Path).ToArray();

    [Fact]
    public void Low_confidence_items_are_hidden_by_default_and_counted()
    {
        var vm = Create();

        Assert.False(vm.ShowLow);
        Assert.Equal(3, vm.VisibleRows.Count);
        Assert.DoesNotContain(LowFolder.Path, Shown(vm));
        Assert.Equal(1, vm.HiddenLowCount);
    }

    [Fact]
    public void Nothing_is_ticked_at_the_start()
    {
        var vm = Create();

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void Showing_low_confidence_adds_those_rows_and_clears_the_hidden_count()
    {
        var vm = Create();

        vm.ShowLow = true;

        Assert.Equal(4, vm.VisibleRows.Count);
        Assert.Equal(0, vm.HiddenLowCount);
    }

    [Fact]
    public void Rows_are_ordered_high_first_and_labelled_in_words()
    {
        var vm = Create();
        vm.ShowLow = true;

        Assert.Equal(new[] { "High", "High", "Medium", "Low" }, vm.VisibleRows.Select(r => r.ConfidenceText));
        Assert.Equal("Folder", vm.VisibleRows.First(r => r.Item == HighFolder).TypeText);
        Assert.Equal("File", vm.VisibleRows.First(r => r.Item == MediumFile).TypeText);
        Assert.Equal("Registry key", vm.VisibleRows.First(r => r.Item == HighKey).TypeText);
    }

    [Fact]
    public void Ticking_a_row_updates_the_count_and_enables_the_button()
    {
        var vm = Create();

        vm.VisibleRows[0].IsSelected = true;

        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        Assert.Contains("1", vm.ConfirmText);
    }

    [Fact]
    public void Tick_high_ticks_only_the_shown_high_items()
    {
        var vm = Create();
        vm.ShowLow = true;

        vm.TickHighCommand.Execute(null);

        Assert.Equal(2, vm.SelectedCount);
        Assert.All(vm.VisibleRows.Where(r => r.IsSelected), r => Assert.Equal(Confidence.High, r.Item.Confidence));
    }

    [Fact]
    public void Tick_all_shown_and_untick_all_work_on_the_visible_rows()
    {
        var vm = Create();

        vm.TickAllCommand.Execute(null);
        Assert.Equal(3, vm.SelectedCount);

        vm.UntickAllCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public void Hiding_low_confidence_again_unticks_the_rows_that_disappear()
    {
        var vm = Create();
        vm.ShowLow = true;
        vm.VisibleRows.Single(r => r.Item == LowFolder).IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);

        vm.ShowLow = false;

        Assert.Equal(0, vm.SelectedCount);
        Assert.Empty(vm.Choice.Items);
    }

    [Fact]
    public void Choice_contains_only_the_ticked_visible_items()
    {
        var vm = Create();
        vm.VisibleRows.First(r => r.Item == HighFolder).IsSelected = true;
        vm.VisibleRows.First(r => r.Item == HighKey).IsSelected = true;

        var choice = vm.Choice;

        Assert.Equal(2, choice.Items.Count);
        Assert.Contains(HighFolder, choice.Items);
        Assert.Contains(HighKey, choice.Items);
    }

    [Fact]
    public void Restore_point_option_is_on_by_default()
    {
        var vm = Create();

        Assert.True(vm.ShowRestorePointOption);
        Assert.True(vm.CreateRestorePoint);
        Assert.True(vm.Choice.CreateRestorePoint);
    }

    [Fact]
    public void When_the_uninstall_already_made_a_restore_point_the_option_is_hidden_and_off()
    {
        var vm = Create(restoreAlready: true);

        Assert.False(vm.ShowRestorePointOption);
        Assert.False(vm.CreateRestorePoint);
        Assert.False(vm.Choice.CreateRestorePoint);
    }

    [Fact]
    public void Confirm_and_close_commands_raise_their_events()
    {
        var vm = Create();
        vm.VisibleRows[0].IsSelected = true;
        var accepted = 0;
        var closed = 0;
        vm.Accepted += () => accepted++;
        vm.Closed += () => closed++;

        vm.ConfirmCommand.Execute(null);
        vm.CloseCommand.Execute(null);

        Assert.Equal(1, accepted);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Heading_and_registry_note_are_shown()
    {
        var vm = Create();

        Assert.Contains("Acme Editor", vm.Heading);
        Assert.True(vm.HasRegistryItems);
    }
}
