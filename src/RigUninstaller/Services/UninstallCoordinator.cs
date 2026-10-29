using RigUninstaller.Models;

namespace RigUninstaller.Services;

public interface IUninstallCoordinator
{
    /// <summary>Asks the user to confirm, then runs the whole uninstall flow for one program.</summary>
    Task<UninstallOutcome> RunInteractiveAsync(InstalledProgram program, CancellationToken cancellationToken);

    /// <summary>The same flow without the confirmation dialog, for callers that already have the user's choices.</summary>
    Task<UninstallOutcome> RunAsync(InstalledProgram program, UninstallOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// The uninstall flow for one program: command, confirmation, "is it running?" check,
/// optional restore point, then the program's own uninstaller. Every step is logged.
/// </summary>
public sealed class UninstallCoordinator : IUninstallCoordinator
{
    private const int ExitRebootRequired = 3010;
    private const int ExitRebootInitiated = 1641;

    private readonly IProcessRunner _runner;
    private readonly IRunningProcessFinder _finder;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly IActionLog _log;
    private readonly IDefenderScanner? _defender;
    private readonly IBackupJournal? _journal;

    public UninstallCoordinator(
        IProcessRunner runner,
        IRunningProcessFinder finder,
        IRestorePointService restorePoints,
        IDialogService dialogs,
        IActionLog log,
        IDefenderScanner? defender = null,
        IBackupJournal? journal = null)
    {
        _runner = runner;
        _finder = finder;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _log = log;
        _defender = defender;
        _journal = journal;
    }

    public async Task<UninstallOutcome> RunInteractiveAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        _log.Info($"Starting uninstall of {program.Name} {program.Version}".TrimEnd());

        var plans = UninstallCommandParser.CreatePlans(program);
        if (plans.Normal is null && plans.Quiet is null)
        {
            return Fail(plans.Error ?? "This program has no uninstall command in the registry.", program);
        }

        var choice = _dialogs.ConfirmUninstall(program, plans);
        if (choice is null)
        {
            _log.Info($"Uninstall of {program.Name} cancelled by the user.");
            return new UninstallOutcome(UninstallStatus.Cancelled, "Uninstall cancelled.");
        }

        return await ExecuteAsync(program, plans, new UninstallOptions(choice.Quiet, choice.CreateRestorePoint, choice.ScanWithDefender), cancellationToken);
    }

    public async Task<UninstallOutcome> RunAsync(InstalledProgram program, UninstallOptions options, CancellationToken cancellationToken)
    {
        _log.Info($"Starting uninstall of {program.Name} {program.Version}".TrimEnd());

        var plans = UninstallCommandParser.CreatePlans(program);
        return await ExecuteAsync(program, plans, options, cancellationToken);
    }

    private async Task<UninstallOutcome> ExecuteAsync(
        InstalledProgram program,
        UninstallPlans plans,
        UninstallOptions options,
        CancellationToken cancellationToken)
    {
        if (plans.Normal is null && plans.Quiet is null)
        {
            return Fail(plans.Error ?? "This program has no uninstall command in the registry.", program);
        }

        // A quiet uninstall is only run when asked for; a normal one falls back to the quiet command if that is all there is.
        var plan = options.Quiet ? plans.Quiet : plans.Normal ?? plans.Quiet;
        if (plan is null)
        {
            return Fail("A quiet uninstall is not available for this program.", program);
        }

        var restorePointCreated = false;
        try
        {
            if (options.ScanWithDefender && _defender is { IsAvailable: true } && !await DefenderAllowsAsync(program, cancellationToken))
            {
                _log.Info($"Uninstall of {program.Name} cancelled after the Microsoft Defender scan.");
                return new UninstallOutcome(UninstallStatus.Cancelled, "Uninstall cancelled: Microsoft Defender reported threats.");
            }

            if (!await AppsClosedAsync(program, cancellationToken))
            {
                _log.Info($"Uninstall of {program.Name} cancelled: the program is still running.");
                return new UninstallOutcome(UninstallStatus.Cancelled, "Uninstall cancelled.");
            }

            if (options.CreateRestorePoint)
            {
                var description = $"RigUninstaller: before uninstalling {program.Name}";
                var restore = await _restorePoints.CreateAsync(description, cancellationToken);
                restorePointCreated = restore.Created;
                if (restore.Created)
                {
                    _log.Info("Restore point created before uninstall.");
                    _journal?.Add(new BackupEntry(DateTime.Now, BackupKinds.RestorePoint, program.Name, description, null));
                }
                else if (!_dialogs.AskContinueWithoutRestorePoint(restore.Message))
                {
                    _log.Info($"Uninstall of {program.Name} cancelled: no restore point and the user declined to continue.");
                    return new UninstallOutcome(UninstallStatus.Cancelled, "Uninstall cancelled: no restore point could be made.");
                }
                else
                {
                    _log.Warn("Continuing without a restore point, as chosen by the user.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Uninstall of {program.Name} cancelled.");
            return new UninstallOutcome(UninstallStatus.Cancelled, "Uninstall cancelled.");
        }

        _log.Info($"Running uninstaller: {plan.DisplayCommand}");
        var result = await _runner.RunAsync(plan.FileName, plan.Arguments, cancellationToken);

        if (!result.Started)
        {
            var message = result.Error ?? "The uninstaller could not be started.";
            _log.Error($"Uninstaller for {program.Name} did not start: {message}");
            return new UninstallOutcome(UninstallStatus.Failed, message, RestorePointCreated: restorePointCreated);
        }

        if (result.Canceled)
        {
            _log.Warn($"Stopped waiting for the uninstaller of {program.Name}. It may still be running.");
            return new UninstallOutcome(
                UninstallStatus.StoppedWaiting,
                "Stopped waiting for the uninstaller. It may still be running.",
                Started: true,
                RestorePointCreated: restorePointCreated);
        }

        _log.Info($"Uninstaller finished with exit code {result.ExitCode}.");
        var reboot = result.ExitCode is ExitRebootRequired or ExitRebootInitiated;
        var text = result.ExitCode == 0
            ? "The uninstaller finished."
            : reboot
                ? "The uninstaller finished. Windows needs a restart to complete it."
                : $"The uninstaller finished with exit code {result.ExitCode}.";
        return new UninstallOutcome(
            UninstallStatus.Completed,
            text,
            result.ExitCode,
            Started: true,
            RebootRequired: reboot,
            RestorePointCreated: restorePointCreated);
    }

    /// <summary>Scans the install folder. False when threats were found and the user chose not to go on.</summary>
    private async Task<bool> DefenderAllowsAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        var scan = await _defender!.ScanProgramAsync(program, cancellationToken);
        if (scan.Status == DefenderStatus.ThreatsFound)
        {
            _log.Warn($"Microsoft Defender reported threats for {program.Name}.");
            return _dialogs.AskContinueAfterThreats(scan.Message);
        }

        // A scan that could not run is only noted: the user asked for it, but it should not block the uninstall.
        _log.Info($"Defender scan before uninstall: {scan.Status}.");
        return true;
    }

    /// <summary>Asks until the program is closed. False when the user cancels.</summary>
    private async Task<bool> AppsClosedAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        while (true)
        {
            var running = await Task.Run(() => _finder.FindRunning(program), cancellationToken);
            if (running.Count == 0)
            {
                return true;
            }

            _log.Warn($"{program.Name} appears to be running: {string.Join(", ", running.Select(p => $"{p.Name} (PID {p.Id})"))}");
            switch (_dialogs.AskAppsRunning(program, running))
            {
                case RunningAppsChoice.Cancel:
                    return false;
                case RunningAppsChoice.ContinueAnyway:
                    _log.Warn("User chose to continue although the program is running.");
                    return true;
            }
        }
    }

    private UninstallOutcome Fail(string message, InstalledProgram program)
    {
        _log.Error($"Cannot uninstall {program.Name}: {message}");
        return new UninstallOutcome(UninstallStatus.Failed, message);
    }
}
