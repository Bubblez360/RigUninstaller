using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum ForceRemoveStatus
{
    Done,
    Cancelled,

    /// <summary>Not allowed: the program is marked Keep, or its entry is protected.</summary>
    Refused,

    Failed,
}

public sealed record ForceRemoveOutcome(ForceRemoveStatus Status, string Message, CleanupResult? Cleanup = null, bool RestorePointCreated = false);

public interface IForceRemoveCoordinator
{
    Task<ForceRemoveOutcome> RunAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms, bool restorePointAlreadyMade, CancellationToken cancellationToken);
}

/// <summary>
/// For programs whose uninstaller is missing or broken: deletes the program's Uninstall registry entry
/// (backed up first) and its install folder (to the Recycle Bin). It asks with a strong warning first and
/// uses the same safe cleaner as the leftover removal.
/// </summary>
public sealed class ForceRemoveCoordinator : IForceRemoveCoordinator
{
    private readonly ILeftoverCleaner _cleaner;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly IKeepStore _keep;
    private readonly IFileSystemProbe _fs;
    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;
    private readonly IBackupJournal? _journal;

    public ForceRemoveCoordinator(
        ILeftoverCleaner cleaner,
        IRestorePointService restorePoints,
        IDialogService dialogs,
        IKeepStore keep,
        IFileSystemProbe fs,
        SafetyGuard guard,
        IActionLog log,
        IBackupJournal? journal = null)
    {
        _cleaner = cleaner;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _keep = keep;
        _fs = fs;
        _guard = guard;
        _log = log;
        _journal = journal;
    }

    public async Task<ForceRemoveOutcome> RunAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> otherPrograms,
        bool restorePointAlreadyMade,
        CancellationToken cancellationToken)
    {
        _log.Info($"Force remove requested for {program.Name}.");

        if (_keep.IsKept(program))
        {
            _log.Warn($"Force remove of {program.Name} refused: it is marked Keep.");
            return new ForceRemoveOutcome(ForceRemoveStatus.Refused, $"{program.Name} is marked Keep. Untick Keep first if you really want to remove it.");
        }

        var keyCheck = _guard.CheckRegistryKey(program.RegistryKeyPath);
        if (!keyCheck.IsAllowed)
        {
            _log.Error($"Force remove of {program.Name} refused: {keyCheck.Reason}");
            return new ForceRemoveOutcome(ForceRemoveStatus.Refused, $"This registry entry is protected: {keyCheck.Reason}");
        }

        var items = new List<LeftoverItem>
        {
            new(LeftoverKind.RegistryKey, program.RegistryKeyPath, Confidence.High, "The program's entry in the installed-programs list."),
        };
        var note = AddInstallFolder(program, otherPrograms, items);

        var choice = _dialogs.ConfirmForceRemove(program, items, note);
        if (choice is null)
        {
            _log.Info($"Force remove of {program.Name} cancelled by the user.");
            return new ForceRemoveOutcome(ForceRemoveStatus.Cancelled, "Force remove cancelled.");
        }

        var restoreMade = false;
        if (choice.CreateRestorePoint && !restorePointAlreadyMade)
        {
            var restore = await RestorePointAsync(program, cancellationToken);
            if (restore is null)
            {
                return new ForceRemoveOutcome(ForceRemoveStatus.Cancelled, "Force remove cancelled: no restore point could be made.");
            }

            restoreMade = restore.Value;
        }

        var cleanup = await _cleaner.CleanAsync(program, items, cancellationToken);
        var removed = cleanup.RegistryRemoved + cleanup.AlreadyGone > 0;
        _log.Info($"Force remove of {program.Name} finished: {cleanup.Moved} folder(s), {cleanup.RegistryRemoved} registry entr{(cleanup.RegistryRemoved == 1 ? "y" : "ies")}, {cleanup.Failures.Count} failed.");

        return removed
            ? new ForceRemoveOutcome(ForceRemoveStatus.Done, $"{program.Name} was force removed.", cleanup, restoreMade)
            : new ForceRemoveOutcome(ForceRemoveStatus.Failed, string.Join("\n", cleanup.Failures.Select(f => $"{f.Item.Path}: {f.Error}")), cleanup, restoreMade);
    }

    /// <summary>Adds the install folder when it exists, is safe to remove and is not used by another program.</summary>
    private string? AddInstallFolder(InstalledProgram program, IReadOnlyList<InstalledProgram> others, List<LeftoverItem> items)
    {
        var folder = ProgramFolders.Tidy(program.InstallLocation);
        if (folder.Length == 0 || !Path.IsPathRooted(folder) || !_fs.Exists(folder))
        {
            return folder.Length == 0 ? "No install folder is recorded, so only the registry entry is removed." : "The install folder does not exist any more.";
        }

        var check = _guard.CheckPath(folder);
        if (!check.IsAllowed)
        {
            _log.Warn($"Install folder of {program.Name} not included: {check.Reason}");
            return $"The install folder is protected and is left alone ({check.Reason}).";
        }

        var sharedWith = others.FirstOrDefault(o => OverlapsFolder(o, folder));
        if (sharedWith is not null)
        {
            _log.Warn($"Install folder of {program.Name} not included: {sharedWith.Name} also uses it.");
            return $"The install folder is also used by {sharedWith.Name}, so it is left alone.";
        }

        items.Add(new LeftoverItem(LeftoverKind.Folder, folder, Confidence.High, "The program's install folder."));
        return null;
    }

    private static bool OverlapsFolder(InstalledProgram other, string folder)
    {
        var location = ProgramFolders.Tidy(other.InstallLocation);
        return location.Length > 0
               && (location.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || location.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)
                   || folder.StartsWith(location + @"\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True: made. False: none, but the user chose to go on. Null: the user backed out.</summary>
    private async Task<bool?> RestorePointAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        var description = $"RigUninstaller: before force removing {program.Name}";
        var restore = await _restorePoints.CreateAsync(description, cancellationToken);
        if (restore.Created)
        {
            _journal?.Add(new BackupEntry(DateTime.Now, BackupKinds.RestorePoint, program.Name, description, null));
            return true;
        }

        return _dialogs.AskContinueWithoutRestorePoint(restore.Message) ? false : null;
    }
}
