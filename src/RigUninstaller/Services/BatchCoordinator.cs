using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum BatchItemStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record BatchItemResult(InstalledProgram Program, BatchItemStatus Status, string Message, UninstallOutcome? Outcome);

/// <summary>Sent while a batch runs: which item is now in which state.</summary>
public sealed record BatchProgress(int Index, int Total, InstalledProgram Program, BatchItemStatus Status, string Message);

public interface IBatchCoordinator
{
    Task<IReadOnlyList<BatchItemResult>> RunAsync(
        IReadOnlyList<InstalledProgram> programs,
        BatchOptions options,
        IProgress<BatchProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uninstalls several programs one after another. One restore point covers the whole batch. Programs marked Keep
/// are skipped. A program that fails does not stop the others; "stop waiting" stops the rest of the batch.
/// </summary>
public sealed class BatchCoordinator : IBatchCoordinator
{
    private readonly IUninstallCoordinator _uninstall;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly IKeepStore _keep;
    private readonly IActionLog _log;
    private readonly IBackupJournal? _journal;

    public BatchCoordinator(
        IUninstallCoordinator uninstall,
        IRestorePointService restorePoints,
        IDialogService dialogs,
        IKeepStore keep,
        IActionLog log,
        IBackupJournal? journal = null)
    {
        _uninstall = uninstall;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _keep = keep;
        _log = log;
        _journal = journal;
    }

    public async Task<IReadOnlyList<BatchItemResult>> RunAsync(
        IReadOnlyList<InstalledProgram> programs,
        BatchOptions options,
        IProgress<BatchProgress> progress,
        CancellationToken cancellationToken)
    {
        _log.Info($"Batch uninstall of {programs.Count} programs started.");
        var results = new List<BatchItemResult>();
        var total = programs.Count;

        var toRun = programs.Where(p => !_keep.IsKept(p)).ToList();
        if (options.CreateRestorePoint && toRun.Count > 0 && !await RestorePointOkAsync(toRun.Count, cancellationToken))
        {
            foreach (var program in programs)
            {
                results.Add(new BatchItemResult(program, BatchItemStatus.Cancelled, "Cancelled: no restore point.", null));
            }

            return results;
        }

        var stopped = false;
        for (var index = 0; index < programs.Count; index++)
        {
            var program = programs[index];

            if (_keep.IsKept(program))
            {
                results.Add(Report(progress, index, total, program, BatchItemStatus.Skipped, "Marked Keep.", null));
                continue;
            }

            if (stopped || cancellationToken.IsCancellationRequested)
            {
                results.Add(Report(progress, index, total, program, BatchItemStatus.Cancelled, "Not started.", null));
                continue;
            }

            progress.Report(new BatchProgress(index, total, program, BatchItemStatus.Running, "Uninstalling..."));
            var outcome = await RunOneAsync(program, options, cancellationToken);
            var result = Map(index, total, program, outcome, progress);
            results.Add(result);
            stopped = outcome.Status == UninstallStatus.StoppedWaiting;
        }

        _log.Info($"Batch uninstall finished: {results.Count(r => r.Status == BatchItemStatus.Done)} done, " +
                  $"{results.Count(r => r.Status == BatchItemStatus.Failed)} failed, " +
                  $"{results.Count(r => r.Status is BatchItemStatus.Skipped or BatchItemStatus.Cancelled)} skipped or cancelled.");
        return results;
    }

    private async Task<UninstallOutcome> RunOneAsync(InstalledProgram program, BatchOptions options, CancellationToken cancellationToken)
    {
        try
        {
            // The batch makes the restore point itself; each program uses its quiet command when asked and available.
            var plans = UninstallCommandParser.CreatePlans(program);
            var quiet = options.PreferQuiet && plans.Quiet is not null;
            return await _uninstall.RunAsync(program, new UninstallOptions(quiet, false, options.ScanWithDefender), cancellationToken);
        }
        catch (Exception ex)
        {
            // One program going wrong must not stop the rest of the batch.
            _log.Error($"Batch: {program.Name} failed unexpectedly", ex);
            return new UninstallOutcome(UninstallStatus.Failed, ex.Message);
        }
    }

    private BatchItemResult Map(int index, int total, InstalledProgram program, UninstallOutcome outcome, IProgress<BatchProgress> progress)
    {
        var (status, message) = outcome.Status switch
        {
            UninstallStatus.Completed => (BatchItemStatus.Done, outcome.Message),
            UninstallStatus.Failed => (BatchItemStatus.Failed, outcome.Message),
            _ => (BatchItemStatus.Cancelled, outcome.Message),
        };
        return Report(progress, index, total, program, status, message, outcome);
    }

    private static BatchItemResult Report(
        IProgress<BatchProgress> progress,
        int index,
        int total,
        InstalledProgram program,
        BatchItemStatus status,
        string message,
        UninstallOutcome? outcome)
    {
        progress.Report(new BatchProgress(index, total, program, status, message));
        return new BatchItemResult(program, status, message, outcome);
    }

    private async Task<bool> RestorePointOkAsync(int count, CancellationToken cancellationToken)
    {
        var description = $"RigUninstaller: before uninstalling {count} programs";
        var restore = await _restorePoints.CreateAsync(description, cancellationToken);
        if (restore.Created)
        {
            _log.Info("Restore point created before the batch.");
            _journal?.Add(new BackupEntry(DateTime.Now, BackupKinds.RestorePoint, "Batch uninstall", description, null));
            return true;
        }

        return _dialogs.AskContinueWithoutRestorePoint(restore.Message);
    }
}
