namespace RigUninstaller.Models;

/// <summary>One concrete command to run: a program file plus its arguments.</summary>
public sealed record UninstallPlan(string FileName, string Arguments, bool IsMsi, bool IsQuiet)
{
    /// <summary>The command as a person would type it, with the path quoted when it has spaces.</summary>
    public string DisplayCommand
    {
        get
        {
            var file = FileName.Contains(' ') ? $"\"{FileName}\"" : FileName;
            return Arguments.Length == 0 ? file : $"{file} {Arguments}";
        }
    }
}

/// <summary>
/// The commands available for a program. <see cref="Normal"/> is the program's own uninstaller,
/// <see cref="Quiet"/> runs it without windows. Both are null only when <see cref="Error"/> says why.
/// </summary>
public sealed record UninstallPlans(UninstallPlan? Normal, UninstallPlan? Quiet, string? Error);

/// <summary>What the user chose in the confirmation dialog.</summary>
public sealed record UninstallOptions(bool Quiet, bool CreateRestorePoint, bool ScanWithDefender = false);

public enum UninstallStatus
{
    /// <summary>The uninstaller ran and ended. Whether the program is really gone is decided by a rescan.</summary>
    Completed,

    /// <summary>Stopped by the user before anything ran.</summary>
    Cancelled,

    /// <summary>The uninstaller was started, but the user stopped waiting for it.</summary>
    StoppedWaiting,

    /// <summary>The uninstaller could not be started or no command exists.</summary>
    Failed,
}

public sealed record UninstallOutcome(
    UninstallStatus Status,
    string Message,
    int? ExitCode = null,
    bool Started = false,
    bool RebootRequired = false,
    bool RestorePointCreated = false);

/// <summary>A running process found under a program's install folder.</summary>
public sealed record RunningProcessInfo(int Id, string Name, string Path);

/// <summary>Result of starting a process and waiting for it (and anything it started) to end.</summary>
public sealed record ProcessRunResult(bool Started, int ExitCode, bool Canceled, string? Error);

public sealed record RestorePointResult(bool Created, string Message);
