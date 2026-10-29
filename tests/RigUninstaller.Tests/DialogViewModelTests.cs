using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class UninstallConfirmViewModelTests
{
    private static InstalledProgram Program() => new()
    {
        Name = "Acme Editor",
        Publisher = "Acme Corp",
        Version = "2.5",
        RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
        KeyName = "Acme",
    };

    private static readonly UninstallPlan Normal = new(@"C:\A\unins.exe", "/S", false, false);
    private static readonly UninstallPlan Quiet = new(@"C:\A\unins.exe", "/VERYSILENT", false, true);

    [Fact]
    public void Defaults_are_normal_uninstall_with_a_restore_point()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(Normal, Quiet, null));

        Assert.False(vm.Quiet);
        Assert.True(vm.CreateRestorePoint);
        Assert.True(vm.CanToggleQuiet);
        Assert.Equal(Normal.DisplayCommand, vm.CommandPreview);
        Assert.Equal("Uninstall Acme Editor?", vm.Heading);
        Assert.Contains("Acme Corp", vm.Details);
        Assert.Contains("2.5", vm.Details);
    }

    [Fact]
    public void Ticking_quiet_switches_the_command_preview()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(Normal, Quiet, null));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Quiet = true;

        Assert.Equal(Quiet.DisplayCommand, vm.CommandPreview);
        Assert.Contains("CommandPreview", changed);
    }

    [Fact]
    public void Without_a_quiet_plan_the_option_is_disabled_and_explained()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(Normal, null, null));

        Assert.False(vm.CanToggleQuiet);
        Assert.True(vm.QuietUnavailable);
        Assert.False(vm.Quiet);
    }

    [Fact]
    public void With_only_a_quiet_plan_quiet_is_forced_on()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(null, Quiet, null));

        Assert.True(vm.Quiet);
        Assert.False(vm.CanToggleQuiet);
        Assert.Equal(Quiet.DisplayCommand, vm.CommandPreview);
    }

    [Fact]
    public void Choice_reflects_the_checkboxes()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(Normal, Quiet, null))
        {
            Quiet = true,
            CreateRestorePoint = false,
        };

        Assert.Equal(new UninstallChoice(true, false), vm.Choice);
    }

    [Fact]
    public void Confirm_and_cancel_commands_raise_their_events()
    {
        var vm = new UninstallConfirmViewModel(Program(), new UninstallPlans(Normal, null, null));
        var accepted = 0;
        var cancelled = 0;
        vm.Accepted += () => accepted++;
        vm.Cancelled += () => cancelled++;

        vm.ConfirmCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.Equal(1, accepted);
        Assert.Equal(1, cancelled);
    }
}

public class PromptViewModelTests
{
    [Fact]
    public void ChooseCommand_reports_the_key_of_the_clicked_button()
    {
        var vm = new PromptViewModel(
            "Title",
            "Heading",
            "Body",
            new[] { "a.exe (id 1)" },
            new[] { new PromptButton("Retry", "retry", IsPrimary: true), new PromptButton("Cancel", "cancel", IsCancel: true) });
        string? chosen = null;
        vm.Chosen += key => chosen = key;

        vm.ChooseCommand.Execute("retry");

        Assert.Equal("retry", chosen);
        Assert.True(vm.HasItems);
    }

    [Fact]
    public void A_prompt_without_items_hides_the_list()
    {
        var vm = new PromptViewModel("T", "H", "B", Array.Empty<string>(), new[] { new PromptButton("OK", "ok") });

        Assert.False(vm.HasItems);
    }

    [Fact]
    public void The_primary_button_is_marked_for_styling()
    {
        Assert.Equal("primary", new PromptButton("Go", "go", IsPrimary: true).StyleTag);
        Assert.Null(new PromptButton("No", "no").StyleTag);
    }
}
