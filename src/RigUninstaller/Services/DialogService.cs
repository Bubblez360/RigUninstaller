using System.Windows;
using Microsoft.Win32;
using RigUninstaller.Models;
using RigUninstaller.ViewModels;
using RigUninstaller.Views;

namespace RigUninstaller.Services;

/// <summary>Opens the app's own themed dialogs. Must be used from the UI thread.</summary>
public sealed class DialogService : IDialogService
{
    private const string RetryKey = "retry";
    private const string ContinueKey = "continue";
    private const string CancelKey = "cancel";
    private const string YesKey = "yes";

    private readonly IDefenderScanner? _defender;

    public DialogService(IDefenderScanner? defender = null)
    {
        _defender = defender;
    }

    public UninstallChoice? ConfirmUninstall(InstalledProgram program, UninstallPlans plans)
    {
        var viewModel = new UninstallConfirmViewModel(program, plans, _defender?.IsAvailable == true);
        var window = new UninstallConfirmWindow { DataContext = viewModel, Owner = Owner };
        return window.ShowDialog() == true ? viewModel.Choice : null;
    }

    public RunningAppsChoice AskAppsRunning(InstalledProgram program, IReadOnlyList<RunningProcessInfo> running)
    {
        var key = Ask(new PromptViewModel(
            "Close the program first",
            $"{program.Name} seems to be running",
            "Close it, and anything it started, then choose Check again. Uninstalling while it runs can fail or leave files behind.",
            running.Select(p => $"{p.Name}   (process {p.Id})").ToList(),
            new[]
            {
                new PromptButton("Check again", RetryKey, IsPrimary: true, IsDefault: true),
                new PromptButton("Continue anyway", ContinueKey),
                new PromptButton("Cancel", CancelKey, IsCancel: true),
            }));

        return key switch
        {
            RetryKey => RunningAppsChoice.Retry,
            ContinueKey => RunningAppsChoice.ContinueAnyway,
            _ => RunningAppsChoice.Cancel,
        };
    }

    public bool AskContinueWithoutRestorePoint(string reason)
    {
        var key = Ask(new PromptViewModel(
            "No restore point",
            "No restore point could be made",
            $"{reason}\n\nYou can still go on, but you will not be able to roll Windows back to just before this change.",
            Array.Empty<string>(),
            new[]
            {
                new PromptButton("Continue without it", ContinueKey),
                new PromptButton("Cancel", CancelKey, IsPrimary: true, IsDefault: true, IsCancel: true),
            }));

        return key == ContinueKey;
    }

    public void ShowMessage(string heading, string message, bool isError) =>
        Ask(new PromptViewModel(
            isError ? "Something went wrong" : "RigUninstaller",
            heading,
            message,
            Array.Empty<string>(),
            new[] { new PromptButton("OK", "ok", IsPrimary: true, IsDefault: true, IsCancel: true) }));

    public LeftoverChoice? ReviewLeftovers(InstalledProgram program, IReadOnlyList<LeftoverItem> items, bool restorePointAlreadyMade)
    {
        var viewModel = new LeftoversViewModel(program, items, restorePointAlreadyMade);
        var window = new LeftoversWindow { DataContext = viewModel, Owner = Owner };
        return window.ShowDialog() == true ? viewModel.Choice : null;
    }

    public ForceRemoveChoice? ConfirmForceRemove(InstalledProgram program, IReadOnlyList<LeftoverItem> items, string? note)
    {
        var lines = items.Select(i => i.Kind == LeftoverKind.RegistryKey
            ? $"Registry entry (backed up to a .reg file first):  {i.Path}"
            : $"Folder (moved to the Recycle Bin):  {i.Path}").ToList();
        var body = "This does NOT run the program's own uninstaller. It only deletes the entries listed below. " +
                   "Files the program put anywhere else stay behind. Use it only when the normal uninstall is broken or the uninstaller file is missing." +
                   (note is null ? string.Empty : "\n\n" + note);

        var viewModel = new PromptViewModel(
            "Force remove",
            $"Force remove {program.Name}?",
            body,
            lines,
            new[]
            {
                new PromptButton("Cancel", CancelKey, IsPrimary: true, IsDefault: true, IsCancel: true),
                new PromptButton("Force remove", YesKey, NeedsRequiredOptions: true),
            },
            new[]
            {
                new PromptOption("I understand: this skips the uninstaller and deletes the entries above.", Required: true),
                new PromptOption("Create a System Restore point first (recommended)", IsChecked: true),
            },
            isWarning: true);

        return Ask(viewModel) == YesKey ? new ForceRemoveChoice(viewModel.Options[1].IsChecked) : null;
    }

    public BatchOptions? ConfirmBatch(IReadOnlyList<InstalledProgram> programs, bool defenderAvailable)
    {
        var options = new List<PromptOption>
        {
            new("Quiet uninstall where the program supports it (no windows)"),
            new("Create one System Restore point first (recommended)", IsChecked: true),
        };
        if (defenderAvailable)
        {
            options.Add(new PromptOption("Scan each program's folder with Microsoft Defender first"));
        }

        var viewModel = new PromptViewModel(
            "Batch uninstall",
            $"Uninstall {programs.Count} programs?",
            "They are removed one after another with their own uninstallers. A program that is still running is skipped over only if you choose Cancel for it.",
            programs.Select(p => p.Name).ToList(),
            new[]
            {
                new PromptButton("Cancel", CancelKey, IsPrimary: true, IsDefault: true, IsCancel: true),
                new PromptButton($"Uninstall {programs.Count}", YesKey),
            },
            options);

        return Ask(viewModel) == YesKey
            ? new BatchOptions(viewModel.Options[0].IsChecked, viewModel.Options[1].IsChecked, defenderAvailable && viewModel.Options[2].IsChecked)
            : null;
    }

    public bool AskContinueAfterThreats(string details) =>
        Ask(new PromptViewModel(
            "Threats found",
            "Microsoft Defender found something",
            details + "\n\nThe uninstall has not started.",
            Array.Empty<string>(),
            new[]
            {
                new PromptButton("Cancel uninstall", CancelKey, IsPrimary: true, IsDefault: true, IsCancel: true),
                new PromptButton("Uninstall anyway", ContinueKey),
            },
            isWarning: true)) == ContinueKey;

    public bool Confirm(string heading, string message, string yesText, string noText, bool yesIsSafe) =>
        Ask(new PromptViewModel(
            "Please confirm",
            heading,
            message,
            Array.Empty<string>(),
            new[]
            {
                new PromptButton(noText, CancelKey, IsPrimary: !yesIsSafe, IsDefault: !yesIsSafe, IsCancel: true),
                new PromptButton(yesText, YesKey, IsPrimary: yesIsSafe, IsDefault: yesIsSafe),
            })) == YesKey;

    public string? AskSavePath(string title, string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = defaultFileName, Filter = filter, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? AskOpenPath(string title, string filter)
    {
        // DereferenceLinks off: a shortcut is returned as the .lnk itself, so the caller can read its arguments too.
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, DereferenceLinks = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static Window? Owner => Application.Current?.MainWindow;

    private static string Ask(PromptViewModel viewModel)
    {
        var window = new PromptWindow { DataContext = viewModel, Owner = Owner };
        window.ShowDialog();
        return window.ResultKey ?? viewModel.CancelKey;
    }
}
