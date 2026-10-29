using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class LeftoverCoordinatorTests
{
    private sealed class FakeScanner : ILeftoverScanner
    {
        public List<LeftoverItem> Items { get; } = new();

        public IReadOnlyList<InstalledProgram>? LastOthers { get; private set; }

        public IReadOnlyList<LeftoverItem> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms)
        {
            LastOthers = otherPrograms;
            return Items;
        }
    }

    private sealed class FakeCleaner : ILeftoverCleaner
    {
        public CleanupResult Result { get; set; } = new(0, 0, 0, Array.Empty<CleanupFailure>(), false);

        public List<IReadOnlyList<LeftoverItem>> Calls { get; } = new();

        public Task<CleanupResult> CleanAsync(InstalledProgram program, IReadOnlyList<LeftoverItem> items, CancellationToken cancellationToken)
        {
            Calls.Add(items);
            return Task.FromResult(Result);
        }
    }

    private readonly FakeScanner _scanner = new();
    private readonly FakeCleaner _cleaner = new();
    private readonly FakeRestore _restore = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeLog _log = new();

    private LeftoverCoordinator Create() => new(_scanner, _cleaner, _restore, _dialogs, _log);

    private static readonly InstalledProgram Removed = new()
    {
        Name = "Acme Editor",
        RegistryKeyPath = @"HKLM\SOFTWARE\x\Acme",
        KeyName = "Acme",
    };

    private static readonly LeftoverItem High = new(LeftoverKind.Folder, @"C:\Program Files\Acme Editor", Confidence.High, "same name");
    private static readonly LeftoverItem Medium = new(LeftoverKind.Folder, @"C:\ProgramData\AcmeEditorLogs", Confidence.Medium, "similar");
    private static readonly LeftoverItem Low = new(LeftoverKind.Folder, @"C:\Program Files\Acme", Confidence.Low, "publisher");

    private Task<LeftoverFlowResult> Run(bool restoreAlready = false, params InstalledProgram[] others) =>
        Create().RunAsync(Removed, others, restoreAlready, CancellationToken.None);

    [Fact]
    public async Task No_items_means_no_dialog()
    {
        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.NoneFound, result.Status);
        Assert.Equal(0, _dialogs.ReviewCalls);
        Assert.Contains("No leftovers", result.Summary);
    }

    [Fact]
    public async Task Only_low_confidence_items_do_not_interrupt_the_user()
    {
        _scanner.Items.Add(Low);

        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.OnlyLowConfidence, result.Status);
        Assert.Equal(0, _dialogs.ReviewCalls);
        Assert.Contains("1 low-confidence", result.Summary);
        Assert.Empty(_cleaner.Calls);
    }

    [Fact]
    public async Task Closing_the_dialog_leaves_everything_in_place()
    {
        _scanner.Items.AddRange(new[] { High, Medium });
        _dialogs.LeftoverResponse = null;

        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.LeftInPlace, result.Status);
        Assert.Equal(2, result.Found);
        Assert.Empty(_cleaner.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("left in place"));
    }

    [Fact]
    public async Task Confirming_with_nothing_ticked_deletes_nothing()
    {
        _scanner.Items.Add(High);
        _dialogs.LeftoverResponse = new LeftoverChoice(Array.Empty<LeftoverItem>(), false);

        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.LeftInPlace, result.Status);
        Assert.Empty(_cleaner.Calls);
    }

    [Fact]
    public async Task The_dialog_gets_all_items_and_the_restore_point_hint()
    {
        _scanner.Items.AddRange(new[] { High, Low });
        _dialogs.LeftoverResponse = null;

        await Run(restoreAlready: true);

        Assert.Equal(new[] { High, Low }, _dialogs.LastReviewItems);
        Assert.True(_dialogs.LastRestoreAlready);
    }

    [Fact]
    public async Task Only_the_chosen_items_are_cleaned()
    {
        _scanner.Items.AddRange(new[] { High, Medium });
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High }, false);
        _cleaner.Result = new CleanupResult(1, 0, 0, Array.Empty<CleanupFailure>(), false);

        var result = await Run();

        Assert.Equal(new[] { High }, Assert.Single(_cleaner.Calls));
        Assert.Equal(LeftoverFlowStatus.Cleaned, result.Status);
        Assert.Equal(1, result.Cleaned);
        Assert.Contains("Cleaned 1 leftover", result.Summary);
    }

    [Fact]
    public async Task A_restore_point_is_made_before_cleaning_when_asked()
    {
        _scanner.Items.Add(High);
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High }, true);

        await Run();

        Assert.Equal(1, _restore.Calls);
        Assert.Contains("Acme Editor", _restore.LastDescription);
        Assert.Single(_cleaner.Calls);
    }

    [Fact]
    public async Task No_second_restore_point_when_the_uninstall_already_made_one()
    {
        _scanner.Items.Add(High);
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High }, true);

        await Run(restoreAlready: true);

        Assert.Equal(0, _restore.Calls);
        Assert.Single(_cleaner.Calls);
    }

    [Fact]
    public async Task Failed_restore_point_and_user_declines_cancels_the_cleanup()
    {
        _scanner.Items.Add(High);
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High }, true);
        _restore.Result = new RestorePointResult(false, "System Restore is off.");
        _dialogs.ContinueWithoutRestore = false;

        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.Cancelled, result.Status);
        Assert.Empty(_cleaner.Calls);
    }

    [Fact]
    public async Task Failed_restore_point_and_user_continues_cleans_anyway()
    {
        _scanner.Items.Add(High);
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High }, true);
        _restore.Result = new RestorePointResult(false, "System Restore is off.");
        _dialogs.ContinueWithoutRestore = true;

        var result = await Run();

        Assert.Equal(LeftoverFlowStatus.Cleaned, result.Status);
        Assert.Single(_cleaner.Calls);
    }

    [Fact]
    public async Task Failures_are_counted_and_mentioned_in_the_summary()
    {
        _scanner.Items.AddRange(new[] { High, Medium });
        _dialogs.LeftoverResponse = new LeftoverChoice(new[] { High, Medium }, false);
        _cleaner.Result = new CleanupResult(
            1,
            0,
            0,
            new[] { new CleanupFailure(Medium, "in use") },
            false);

        var result = await Run();

        Assert.Equal(1, result.Failed);
        Assert.Contains("1 failed", result.Summary);
        Assert.Equal("in use", Assert.Single(result.Failures).Error);
    }

    [Fact]
    public async Task The_other_installed_programs_are_given_to_the_scanner()
    {
        var other = new InstalledProgram { Name = "Beta", RegistryKeyPath = @"HKLM\x\Beta", KeyName = "Beta" };

        await Run(false, other);

        Assert.Equal(new[] { other }, _scanner.LastOthers);
    }
}
