using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>The options ticked in the confirmation dialog.</summary>
public sealed record UninstallChoice(bool Quiet, bool CreateRestorePoint, bool ScanWithDefender = false);

/// <summary>What the user chose in the force-remove warning.</summary>
public sealed record ForceRemoveChoice(bool CreateRestorePoint);

/// <summary>What the user chose before a batch uninstall starts.</summary>
public sealed record BatchOptions(bool PreferQuiet, bool CreateRestorePoint, bool ScanWithDefender = false);

public enum RunningAppsChoice
{
    /// <summary>The user closed the app and wants the check run again.</summary>
    Retry,

    ContinueAnyway,
    Cancel,
}

/// <summary>Every question the app asks the user. Behind an interface so flows can be tested without windows.</summary>
public interface IDialogService
{
    /// <summary>Shows the "are you sure" dialog. Returns null when the user cancels.</summary>
    UninstallChoice? ConfirmUninstall(InstalledProgram program, UninstallPlans plans);

    RunningAppsChoice AskAppsRunning(InstalledProgram program, IReadOnlyList<RunningProcessInfo> running);

    /// <summary>True when the user wants to go on although no restore point could be made.</summary>
    bool AskContinueWithoutRestorePoint(string reason);

    void ShowMessage(string heading, string message, bool isError);

    /// <summary>Shows the leftovers checklist. Returns null when the user closes it without removing anything.</summary>
    LeftoverChoice? ReviewLeftovers(InstalledProgram program, IReadOnlyList<LeftoverItem> items, bool restorePointAlreadyMade);

    /// <summary>The strong warning before a force remove. Null when the user backs out.</summary>
    ForceRemoveChoice? ConfirmForceRemove(InstalledProgram program, IReadOnlyList<LeftoverItem> items, string? note);

    /// <summary>Lists the programs of a batch and asks for the options. Null when cancelled.</summary>
    BatchOptions? ConfirmBatch(IReadOnlyList<InstalledProgram> programs, bool defenderAvailable);

    /// <summary>True when the user wants to go on although Microsoft Defender reported threats.</summary>
    bool AskContinueAfterThreats(string details);

    /// <summary>Asks for a yes/no. The primary button is the safe one only if <paramref name="yesIsSafe"/> is true.</summary>
    bool Confirm(string heading, string message, string yesText, string noText, bool yesIsSafe);

    /// <summary>Asks where to save a file. Null when cancelled.</summary>
    string? AskSavePath(string title, string defaultFileName, string filter);

    /// <summary>Asks which existing file to use. Null when cancelled.</summary>
    string? AskOpenPath(string title, string filter);
}
