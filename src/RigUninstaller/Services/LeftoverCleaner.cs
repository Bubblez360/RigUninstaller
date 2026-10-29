using RigUninstaller.Models;

namespace RigUninstaller.Services;

public interface ILeftoverCleaner
{
    /// <summary>Removes exactly the given items. Nothing outside the list is ever touched.</summary>
    Task<CleanupResult> CleanAsync(InstalledProgram program, IReadOnlyList<LeftoverItem> items, CancellationToken cancellationToken);
}

/// <summary>
/// Removes the leftovers the user ticked. Files and folders go to the Recycle Bin; registry keys are
/// saved to a .reg file first and only deleted when that backup worked. Every item is checked by the
/// safety guard again right before it is touched, and every action is logged and journaled.
/// </summary>
public sealed class LeftoverCleaner : ILeftoverCleaner
{
    private const int MaxNameLength = 60;

    private readonly IRecycleBin _recycleBin;
    private readonly IRegistryTool _registry;
    private readonly IBackupJournal _journal;
    private readonly IFileSystemProbe _fs;
    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;
    private readonly string _backupRoot;

    public LeftoverCleaner(
        IRecycleBin recycleBin,
        IRegistryTool registry,
        IBackupJournal journal,
        IFileSystemProbe fs,
        SafetyGuard guard,
        IActionLog log,
        string backupRoot)
    {
        _recycleBin = recycleBin;
        _registry = registry;
        _journal = journal;
        _fs = fs;
        _guard = guard;
        _log = log;
        _backupRoot = backupRoot;
    }

    public async Task<CleanupResult> CleanAsync(InstalledProgram program, IReadOnlyList<LeftoverItem> items, CancellationToken cancellationToken)
    {
        var failures = new List<CleanupFailure>();
        int moved = 0, registryRemoved = 0, alreadyGone = 0, registryIndex = 0;
        var cancelled = false;

        // All .reg backups of one run share a folder named after the time and the program.
        var backupFolder = Path.Combine(_backupRoot, "Registry", $"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeName(program.Name)}");

        // Files and folders keep their order; registry keys go last, outer keys before the keys inside them.
        var ordered = items
            .OrderBy(i => i.Kind == LeftoverKind.RegistryKey ? 1 : 0)
            .ThenBy(i => i.Kind == LeftoverKind.RegistryKey ? i.Path.Length : 0)
            .ToList();
        var removedKeys = new List<string>();

        _log.Info($"Cleaning {items.Count} leftover items of {program.Name}.");
        foreach (var item in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            try
            {
                if (item.Kind == LeftoverKind.RegistryKey)
                {
                    if (removedKeys.Any(parent => item.Path.StartsWith(parent + @"\", StringComparison.OrdinalIgnoreCase)))
                    {
                        _log.Info($"Already removed together with the key above it: {item.Path}");
                        alreadyGone++;
                        continue;
                    }

                    var result = await RemoveKeyAsync(program, item, backupFolder, ++registryIndex, cancellationToken);
                    registryRemoved += result.Removed ? 1 : 0;
                    if (result.Removed)
                    {
                        removedKeys.Add(item.Path);
                    }

                    AddFailure(failures, item, result.Error);
                }
                else
                {
                    var result = await RemovePathAsync(program, item, cancellationToken);
                    moved += result.Moved ? 1 : 0;
                    alreadyGone += result.Gone ? 1 : 0;
                    AddFailure(failures, item, result.Error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
        }

        _log.Info(
            $"Leftover cleanup finished: {moved} moved to the Recycle Bin, {registryRemoved} registry keys removed " +
            $"(backed up first), {alreadyGone} already gone, {failures.Count} failed{(cancelled ? ", cancelled early" : string.Empty)}.");
        return new CleanupResult(moved, registryRemoved, alreadyGone, failures, cancelled);
    }

    private async Task<(bool Moved, bool Gone, string? Error)> RemovePathAsync(
        InstalledProgram program,
        LeftoverItem item,
        CancellationToken cancellationToken)
    {
        var check = _guard.CheckPath(item.Path);
        if (!check.IsAllowed)
        {
            return (false, false, Refuse(item, check.Reason));
        }

        if (!_fs.Exists(item.Path))
        {
            _log.Info($"Already gone, nothing to do: {item.Path}");
            return (false, true, null);
        }

        try
        {
            await Task.Run(() => _recycleBin.Move(item.Path, item.Kind == LeftoverKind.Folder), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Files in use, access denied, a cancelled shell dialog...: report it and carry on with the rest.
            _log.Error($"Could not move to the Recycle Bin: {item.Path}", ex);
            return (false, false, ex.Message);
        }

        _log.Info($"Moved to the Recycle Bin: {item.Path}");
        _journal.Add(new BackupEntry(DateTime.Now, BackupKinds.RecycledItem, program.Name, item.Path, null));
        return (true, false, null);
    }

    private async Task<(bool Removed, string? Error)> RemoveKeyAsync(
        InstalledProgram program,
        LeftoverItem item,
        string backupFolder,
        int index,
        CancellationToken cancellationToken)
    {
        var check = _guard.CheckRegistryKey(item.Path);
        if (!check.IsAllowed)
        {
            return (false, Refuse(item, check.Reason));
        }

        var backupFile = Path.Combine(backupFolder, $"{index:000}_{SafeName(item.Path)}.reg");
        var export = await _registry.ExportAsync(item.Path, backupFile, cancellationToken);
        if (!export.Success)
        {
            // No backup, no delete.
            _log.Error($"Registry backup failed, key kept: {item.Path} ({export.Message})");
            return (false, $"Registry backup failed, so the key was kept. {export.Message}");
        }

        _log.Info($"Exported registry key {item.Path} to {backupFile}");
        _journal.Add(new BackupEntry(DateTime.Now, BackupKinds.RegistryExport, program.Name, item.Path, backupFile));

        try
        {
            _registry.DeleteKeyTree(item.Path);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not delete registry key {item.Path}", ex);
            return (false, ex.Message);
        }

        _log.Info($"Deleted registry key {item.Path}");
        return (true, null);
    }

    private string Refuse(LeftoverItem item, string reason)
    {
        _log.Error($"Refused to touch {item.Path}: {reason}");
        return $"Blocked by safety rules: {reason}";
    }

    private static void AddFailure(List<CleanupFailure> failures, LeftoverItem item, string? error)
    {
        if (error is not null)
        {
            failures.Add(new CleanupFailure(item, error));
        }
    }

    /// <summary>Keeps letters, digits, space, dash and underscore, so a name can never become a path.</summary>
    private static string SafeName(string text)
    {
        var cleaned = new string(text.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength];
        }

        return cleaned.Length == 0 ? "item" : cleaned;
    }
}
