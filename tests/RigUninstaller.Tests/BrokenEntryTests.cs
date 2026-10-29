using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>Entries whose uninstaller is gone (like a removed or moved program) are cleaned up, not just reported as an error.</summary>
public class BrokenEntryTests : VmTestBase
{
    private const string Missing = @"The uninstaller file was not found: D:\Gone\uninstall.exe";

    private static ProgramInsight Orphan(string reason) => new(
        new SignatureInfo(SignatureState.Unsigned, null, "none"),
        new LastUsedInfo(null, "none"),
        reason);

    [Fact]
    public async Task A_broken_entry_offers_clean_up_instead_of_trying_to_run_its_missing_uninstaller()
    {
        var vm = await ScannedAsync(Program("SimpleTransfer Desktop"));
        vm.SelectedRow = vm.Rows[0];
        vm.SelectedRow.Insight = Orphan(ProgramInsightService.MissingUninstallerReason);
        _dialogs.ConfirmAnswer = true;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("cannot be uninstalled", Assert.Single(_dialogs.Confirmations));
        Assert.Empty(_coordinator.Requested); // the missing file was never started
        Assert.Equal("SimpleTransfer Desktop", Assert.Single(_forceRemove.Calls).Name);
    }

    [Fact]
    public async Task Declining_the_offer_changes_nothing()
    {
        var vm = await ScannedAsync(Program("SimpleTransfer Desktop"));
        vm.SelectedRow = vm.Rows[0];
        vm.SelectedRow.Insight = Orphan(ProgramInsightService.MissingUninstallerReason);
        _dialogs.ConfirmAnswer = false;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(_coordinator.Requested);
        Assert.Empty(_forceRemove.Calls);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task A_missing_install_folder_alone_does_not_stop_the_normal_uninstall()
    {
        // The uninstaller itself may live elsewhere (Windows Installer cache), so it is still tried.
        var vm = await ScannedAsync(Program("Some App"));
        vm.SelectedRow = vm.Rows[0];
        vm.SelectedRow.Insight = Orphan("The install folder is missing.");

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Single(_coordinator.Requested);
        Assert.Empty(_forceRemove.Calls);
    }

    [Fact]
    public async Task When_the_file_turns_out_to_be_missing_after_the_try_the_same_offer_appears()
    {
        // The background analysis had not finished, so the row was not tagged yet.
        var vm = await ScannedAsync(Program("StreamElements SE.Live"));
        vm.SelectedRow = vm.Rows[0];
        _coordinator.Handler = (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.Failed, Missing));
        _dialogs.ConfirmAnswer = true;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Single(_coordinator.Requested);
        Assert.Contains("cannot be uninstalled", Assert.Single(_dialogs.Confirmations));
        Assert.Equal("StreamElements SE.Live", Assert.Single(_forceRemove.Calls).Name);
        Assert.Empty(_dialogs.Messages); // no bare error dialog
    }

    [Fact]
    public async Task Other_failures_still_show_the_error_and_do_not_offer_clean_up()
    {
        var vm = await ScannedAsync(Program("Some App"));
        vm.SelectedRow = vm.Rows[0];
        _coordinator.Handler = (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.Failed, "Windows permission for the uninstaller was refused."));

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Confirmations);
        Assert.Empty(_forceRemove.Calls);
        Assert.True(Assert.Single(_dialogs.Messages).IsError);
    }

    [Fact]
    public async Task A_batch_failure_for_a_missing_file_points_to_force_remove()
    {
        var vm = await ScannedAsync(Program("StreamElements SE.Live"));
        vm.Rows[0].IsChecked = true;
        _batch.Handler = (programs, _, _) =>
        {
            IReadOnlyList<BatchItemResult> results = programs.Select(p => new BatchItemResult(p, BatchItemStatus.Failed, Missing, null)).ToList();
            return Task.FromResult(results);
        };

        await vm.BatchUninstallCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("Force remove", message.Message);
    }
}
