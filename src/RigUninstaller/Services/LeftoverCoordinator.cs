using RigUninstaller.Models;

namespace RigUninstaller.Services;

public interface ILeftoverCoordinator
{
    /// <summary>Finds leftovers of a removed program, lets the user choose, and removes what was chosen.</summary>
    Task<LeftoverFlowResult> RunAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> otherPrograms,
        bool restorePointAlreadyMade,
        CancellationToken cancellationToken);
}

/// <summary>
/// The leftover flow: scan, show the checklist, optional restore point, clean. Nothing is deleted
/// unless the user ticked it, and a window is only shown when there is something worth a decision.
/// </summary>
public sealed class LeftoverCoordinator : ILeftoverCoordinator
{
    private readonly ILeftoverScanner _scanner;
    private readonly ILeftoverCleaner _cleaner;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private readonly IBackupJournal? _journal;
    private readonly ITraceStore? _traces;

    public LeftoverCoordinator(
        ILeftoverScanner scanner,
        ILeftoverCleaner cleaner,
        IRestorePointService restorePoints,
        IDialogService dialogs,
        IActionLog log,
        IBackupJournal? journal = null,
        ITraceStore? traces = null)
    {
        _scanner = scanner;
        _cleaner = cleaner;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _log = log;
        _journal = journal;
        _traces = traces;
    }

    public async Task<LeftoverFlowResult> RunAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> otherPrograms,
        bool restorePointAlreadyMade,
        CancellationToken cancellationToken)
    {
        _log.Info($"Searching for leftovers of {program.Name}.");
        var items = await Task.Run(() => MergeWithTrace(program, _scanner.Scan(program, otherPrograms)), cancellationToken);

        if (items.Count == 0)
        {
            return Result(LeftoverFlowStatus.NoneFound, 0, "No leftovers found.");
        }

        // Low-confidence matches alone are not worth interrupting the user for.
        if (items.All(i => i.Confidence == Confidence.Low))
        {
            var noun = items.Count == 1 ? "match" : "matches";
            _log.Info($"Only low-confidence leftovers found ({items.Count}); not shown.");
            return Result(LeftoverFlowStatus.OnlyLowConfidence, items.Count, $"No clear leftovers ({items.Count} low-confidence {noun} not shown).");
        }

        var choice = _dialogs.ReviewLeftovers(program, items, restorePointAlreadyMade);
        if (choice is null || choice.Items.Count == 0)
        {
            _log.Info($"{items.Count} leftover items of {program.Name} left in place by the user's choice.");
            return Result(LeftoverFlowStatus.LeftInPlace, items.Count, $"{items.Count} leftover{Plural(items.Count)} left in place.");
        }

        if (choice.CreateRestorePoint && !restorePointAlreadyMade && !await RestorePointOkAsync(program, cancellationToken))
        {
            return Result(LeftoverFlowStatus.Cancelled, items.Count, "Leftover cleanup cancelled.");
        }

        var cleanup = await _cleaner.CleanAsync(program, choice.Items, cancellationToken);
        var cleaned = cleanup.Moved + cleanup.RegistryRemoved + cleanup.AlreadyGone;
        var failed = cleanup.Failures.Count;
        var summary = $"Cleaned {cleaned} leftover{Plural(cleaned)}" + (failed > 0 ? $", {failed} failed." : ".");
        return new LeftoverFlowResult(LeftoverFlowStatus.Cleaned, items.Count, cleaned, failed, cleanup.Failures, summary);
    }

    /// <summary>True when it is fine to go on: a restore point exists, or the user accepted going without one.</summary>
    private async Task<bool> RestorePointOkAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        var description = $"RigUninstaller: before cleaning leftovers of {program.Name}";
        var restore = await _restorePoints.CreateAsync(description, cancellationToken);
        if (restore.Created)
        {
            _log.Info("Restore point created before leftover cleanup.");
            _journal?.Add(new BackupEntry(DateTime.Now, BackupKinds.RestorePoint, program.Name, description, null));
            return true;
        }

        if (_dialogs.AskContinueWithoutRestorePoint(restore.Message))
        {
            _log.Warn("Cleaning leftovers without a restore point, as chosen by the user.");
            return true;
        }

        _log.Info("Leftover cleanup cancelled: no restore point and the user declined to continue.");
        return false;
    }

    /// <summary>
    /// If the install of this program was tracked, what the install really created is known exactly:
    /// those paths that still exist are added as High, which beats any name guess.
    /// </summary>
    private IReadOnlyList<LeftoverItem> MergeWithTrace(InstalledProgram program, IReadOnlyList<LeftoverItem> guessed)
    {
        var traced = _traces?.LoadItems(program);
        if (traced is null || traced.Count == 0)
        {
            return guessed;
        }

        _log.Info($"Using the install trace of {program.Name}: {traced.Count} items still present.");
        var merged = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in guessed.Concat(traced))
        {
            if (!merged.TryGetValue(item.Path, out var existing) || item.Confidence > existing.Confidence)
            {
                merged[item.Path] = item;
            }
        }

        return merged.Values.OrderByDescending(i => i.Confidence).ThenBy(i => i.Kind).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static LeftoverFlowResult Result(LeftoverFlowStatus status, int found, string summary) =>
        new(status, found, 0, 0, Array.Empty<CleanupFailure>(), summary);

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}
