using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class RiskAdvisorTests
{
    private static InstalledProgram P(string name) => new() { Name = name, RegistryKeyPath = @"HKLM\x\" + name, KeyName = name };

    [Theory]
    [InlineData("NVIDIA Graphics Driver 610.74")]
    [InlineData("AMD Software: Adrenalin Edition")]
    [InlineData("Intel(R) Graphics Driver")]
    [InlineData("AMD Chipset Software")]
    [InlineData("Intel(R) Management Engine Components")]
    [InlineData("Intel(R) Rapid Storage Technology")]
    [InlineData("NVIDIA HD Audio Driver 1.4.5.7")]
    [InlineData("Realtek High Definition Audio Driver")]
    [InlineData("Realtek Ethernet Controller Driver")]
    [InlineData("Windows Driver Package - Contoso (Contoso.sys) Net")]
    [InlineData("WireGuard")]
    [InlineData("Microsoft Update Health Tools")]
    public void Drivers_and_system_parts_are_marked_risky(string name)
    {
        var risk = RiskAdvisor.Assess(P(name));

        Assert.NotNull(risk);
        Assert.Equal(RiskLevel.Danger, risk!.Level);
        Assert.Equal("Risky", risk.Label);
        Assert.NotEmpty(risk.Reason);
    }

    [Theory]
    [InlineData("Microsoft .NET Runtime - 8.0.11 (x64)")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.11 (x64)")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135")]
    [InlineData("Microsoft Edge WebView2 Runtime")]
    [InlineData("Microsoft XNA Framework Redistributable 4.0 Refresh")]
    [InlineData("NVIDIA PhysX System Software 9.23.1019")]
    [InlineData("Java 8 Update 361")]
    [InlineData("Malwarebytes version 4.6")]
    [InlineData("ASUS Framework Service")]
    [InlineData("Armoury Crate Service")]
    [InlineData("Logitech G HUB")]
    [InlineData("Python 3.11.9 (64-bit)")]
    public void Runtimes_security_and_hardware_tools_get_a_caution(string name)
    {
        var risk = RiskAdvisor.Assess(P(name));

        Assert.NotNull(risk);
        Assert.Equal(RiskLevel.Caution, risk!.Level);
        Assert.Equal("Caution", risk.Label);
    }

    [Theory]
    [InlineData("Discord")]
    [InlineData("Steam")]
    [InlineData("7-Zip 24.08 (x64 edition)")]
    [InlineData("Overwolf")]
    [InlineData("Dota 2")]
    [InlineData("NVIDIA App 11.0.9.251")]
    [InlineData("Node.js")]
    [InlineData("OBS Studio")]
    public void Ordinary_programs_get_no_warning(string name)
    {
        Assert.Null(RiskAdvisor.Assess(P(name)));
    }

    [Fact]
    public void A_driver_is_judged_by_the_serious_rule_even_when_a_milder_one_also_fits()
    {
        // "Intel ... Software" fits the mild hardware-helper rule, but "Chipset" is a driver and wins.
        Assert.Equal(RiskLevel.Danger, RiskAdvisor.Assess(P("Intel(R) Chipset Device Software"))!.Level);
    }
}

public class RiskWarningFlowTests : VmTestBase
{
    [Fact]
    public async Task A_risky_row_carries_its_tag_and_the_selection_shows_the_warning()
    {
        var vm = await ScannedAsync(Program("Discord"), Program("NVIDIA Graphics Driver 610.74"));
        var driver = vm.Rows.Single(r => r.Name.StartsWith("NVIDIA"));

        Assert.True(driver.HasRisk);
        Assert.True(driver.IsDanger);
        Assert.Contains("Risky", driver.Tags);
        Assert.False(vm.Rows.Single(r => r.Name == "Discord").HasRisk);

        vm.SelectedRow = driver;
        Assert.True(vm.HasSelectionRisk);
        Assert.True(vm.SelectionRiskIsDanger);
        Assert.StartsWith("Risky:", vm.SelectionRisk);

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Discord");
        Assert.False(vm.HasSelectionRisk);
        Assert.Equal(string.Empty, vm.SelectionRisk);
    }

    [Fact]
    public async Task Uninstalling_a_risky_program_asks_first_and_cancelling_stops_it()
    {
        var vm = await ScannedAsync(Program("NVIDIA Graphics Driver 610.74"));
        vm.SelectedRow = vm.Rows[0];
        _dialogs.ConfirmAnswer = false;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("risky to remove", Assert.Single(_dialogs.Confirmations));
        Assert.Empty(_coordinator.Requested);
    }

    [Fact]
    public async Task Continuing_after_the_warning_runs_the_normal_uninstall()
    {
        var vm = await ScannedAsync(Program("Microsoft Visual C++ 2015-2022 Redistributable (x64)"));
        vm.SelectedRow = vm.Rows[0];
        _dialogs.ConfirmAnswer = true;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("be careful", Assert.Single(_dialogs.Confirmations));
        Assert.Single(_coordinator.Requested);
    }

    [Fact]
    public async Task An_ordinary_program_gets_no_extra_warning()
    {
        var vm = await ScannedAsync(Program("Discord"));
        vm.SelectedRow = vm.Rows[0];

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Confirmations);
        Assert.Single(_coordinator.Requested);
    }

    [Fact]
    public async Task A_batch_with_a_risky_program_names_it_and_can_be_cancelled()
    {
        var vm = await ScannedAsync(Program("Discord"), Program("Realtek High Definition Audio Driver"));
        foreach (var row in vm.Rows)
        {
            row.IsChecked = true;
        }

        _dialogs.ConfirmAnswer = false;
        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Contains("1 ticked program is risky", Assert.Single(_dialogs.Confirmations));
        Assert.Null(_dialogs.LastBatchPrograms); // never reached the batch confirmation
    }

    [Fact]
    public async Task A_batch_without_risky_programs_is_not_interrupted()
    {
        var vm = await ScannedAsync(Program("Discord"), Program("Steam"));
        foreach (var row in vm.Rows)
        {
            row.IsChecked = true;
        }

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Confirmations);
        Assert.NotNull(_dialogs.LastBatchPrograms);
    }
}
