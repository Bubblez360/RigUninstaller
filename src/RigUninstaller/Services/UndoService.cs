using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>One thing that can be brought back, built from a journal line.</summary>
public sealed record UndoItem(BackupEntry Entry, string TypeText, string Description, bool IsRestored)
{
    public bool CanRestore => !IsRestored;
}

public sealed record UndoResult(bool Success, string Message);

public interface IUndoService
{
    IReadOnlyList<UndoItem> List();

    Task<UndoResult> RestoreAsync(UndoItem item, CancellationToken cancellationToken);
}

/// <summary>
/// The "Undo center": reads the backup journal and restores registry exports (reg import), Recycle Bin items,
/// startup changes and opens System Restore for restore points. Each restore is logged and journaled.
/// </summary>
public sealed class UndoService : IUndoService
{
    private const string RecycleScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $orig = $env:RIG_ORIGINAL_PATH
            if (Test-Path -LiteralPath $orig) { Write-Output 'FAIL: Something already exists at the original location.'; exit 2 }
            $leaf = [IO.Path]::GetFileName($orig)
            $dir = [IO.Path]::GetDirectoryName($orig)
            $bin = (New-Object -ComObject Shell.Application).Namespace(10)
            $item = $bin.Items() | Where-Object {
                $_.ExtendedProperty('System.Recycle.DeletedFrom') -eq $dir -and ($_.Name -eq $leaf -or $_.Name -eq [IO.Path]::GetFileNameWithoutExtension($leaf))
            } | Select-Object -First 1
            if ($null -eq $item) { Write-Output 'FAIL: The item is no longer in the Recycle Bin.'; exit 2 }
            $item.InvokeVerb('undelete')
            Start-Sleep -Milliseconds 700
            if (Test-Path -LiteralPath $orig) { Write-Output 'OK' } else { Write-Output 'FAIL: Windows did not restore it.'; exit 2 }
        } catch { Write-Output ('FAIL: ' + $_.Exception.Message); exit 2 }
        """;

    private readonly IBackupJournal _journal;
    private readonly IConsoleRunner _console;
    private readonly IPowerShellRunner _shell;
    private readonly IFileSystemProbe _fs;
    private readonly IExternalActions _external;
    private readonly IStartupManager _startup;
    private readonly IActionLog _log;

    public UndoService(
        IBackupJournal journal,
        IConsoleRunner console,
        IPowerShellRunner shell,
        IFileSystemProbe fs,
        IExternalActions external,
        IStartupManager startup,
        IActionLog log)
    {
        _journal = journal;
        _console = console;
        _shell = shell;
        _fs = fs;
        _external = external;
        _startup = startup;
        _log = log;
    }

    public IReadOnlyList<UndoItem> List()
    {
        var all = _journal.ReadAll();
        var restored = all.Where(e => e.Kind == BackupKinds.Restored && e.Detail is not null).Select(e => e.Detail!).ToHashSet(StringComparer.Ordinal);

        return all
            .Where(e => e.Kind != BackupKinds.Restored)
            .OrderByDescending(e => e.Time)
            .Select(e => new UndoItem(e, TypeOf(e), Describe(e), restored.Contains(e.Key)))
            .ToList();
    }

    public async Task<UndoResult> RestoreAsync(UndoItem item, CancellationToken cancellationToken)
    {
        var entry = item.Entry;
        _log.Info($"Undo requested: {item.TypeText} - {entry.Target}");

        UndoResult result;
        try
        {
            result = entry.Kind switch
            {
                BackupKinds.RegistryExport => await RestoreRegistryAsync(entry, cancellationToken),
                BackupKinds.RecycledItem => await RestoreRecycledAsync(entry, cancellationToken),
                BackupKinds.StartupChange => await RestoreStartupAsync(entry, cancellationToken),
                BackupKinds.RestorePoint => OpenSystemRestore(entry),
                _ => new UndoResult(false, "This kind of entry cannot be restored."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Error($"Undo failed for {entry.Target}", ex);
            return new UndoResult(false, ex.Message);
        }

        if (result.Success && entry.Kind != BackupKinds.RestorePoint)
        {
            _journal.Add(new BackupEntry(DateTime.Now, BackupKinds.Restored, entry.Program, entry.Target, null, entry.Key));
        }

        _log.Info($"Undo {(result.Success ? "done" : "failed")}: {result.Message}");
        return result;
    }

    private async Task<UndoResult> RestoreRegistryAsync(BackupEntry entry, CancellationToken cancellationToken)
    {
        if (entry.BackupFile is null || !_fs.Exists(entry.BackupFile))
        {
            return new UndoResult(false, "The backup file is missing, so the key cannot be restored.");
        }

        var result = await _console.RunAsync(Path.Combine(Environment.SystemDirectory, "reg.exe"), new[] { "import", entry.BackupFile }, cancellationToken);
        return result.ExitCode == 0
            ? new UndoResult(true, $"Registry key restored: {entry.Target}")
            : new UndoResult(false, $"reg.exe could not import the backup: {result.Output}");
    }

    private async Task<UndoResult> RestoreRecycledAsync(BackupEntry entry, CancellationToken cancellationToken)
    {
        var result = await _shell.RunAsync(RecycleScript, new Dictionary<string, string> { ["RIG_ORIGINAL_PATH"] = entry.Target }, cancellationToken);
        if (result.ExitCode == 0 && result.Output.Contains("OK", StringComparison.Ordinal))
        {
            return new UndoResult(true, $"Restored from the Recycle Bin: {entry.Target}");
        }

        return new UndoResult(false, result.Output.StartsWith("FAIL:", StringComparison.Ordinal) ? result.Output[5..].Trim() : result.Output);
    }

    private async Task<UndoResult> RestoreStartupAsync(BackupEntry entry, CancellationToken cancellationToken)
    {
        var wasEnabled = string.Equals(entry.Detail, "enabled", StringComparison.OrdinalIgnoreCase);
        var change = await _startup.RestoreAsync(entry.Target, wasEnabled, cancellationToken);
        return new UndoResult(change.Success, change.Success ? $"{entry.Program} is {(wasEnabled ? "enabled" : "disabled")} again." : change.Message);
    }

    private UndoResult OpenSystemRestore(BackupEntry entry)
    {
        // Windows only restores a point through its own wizard, which needs a restart. It is opened for the user.
        _external.OpenFile(Path.Combine(Environment.SystemDirectory, "rstrui.exe"));
        return new UndoResult(true, $"System Restore was opened. Choose the point named \"{entry.Target}\".");
    }

    private static string TypeOf(BackupEntry entry) => entry.Kind switch
    {
        BackupKinds.RegistryExport => "Registry backup",
        BackupKinds.RecycledItem => "Recycle Bin item",
        BackupKinds.RestorePoint => "Restore point",
        BackupKinds.StartupChange => "Startup change",
        _ => entry.Kind,
    };

    private static string Describe(BackupEntry entry) => entry.Kind == BackupKinds.StartupChange
        ? $"{entry.Program}: was {entry.Detail}"
        : entry.Target;
}
