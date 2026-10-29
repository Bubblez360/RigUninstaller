using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.Tests;

internal sealed class FakeLog : IActionLog
{
    public List<string> Lines { get; } = new();

    public string LogFilePath => @"C:\fake\log.txt";

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Warn(string message) => Lines.Add("WARN " + message);

    public void Error(string message, Exception? exception = null) =>
        Lines.Add("ERROR " + message + (exception is null ? "" : " | " + exception.Message));

    public string ReadTail(int lineCount) => string.Join(Environment.NewLine, Lines);
}

internal sealed class FakeTheme : IThemeService
{
    public bool IsDark { get; private set; }

    public void Apply(bool dark) => IsDark = dark;

    public void Toggle() => IsDark = !IsDark;
}

internal sealed class FakeWindows : IWindowService
{
    public int LogShown { get; private set; }

    public void ShowLog() => LogShown++;
}

internal sealed class FakeScanner : IProgramScanner
{
    private int _calls;

    public ScanResult Result { get; set; } = new(Array.Empty<InstalledProgram>(), 0, 0, 0, 0, 0);

    public Exception? Throw { get; set; }

    public int Calls => _calls;

    public ScanResult Scan()
    {
        Interlocked.Increment(ref _calls);
        return Throw is null ? Result : throw Throw;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public UninstallChoice? Choice { get; set; } = new(Quiet: false, CreateRestorePoint: true);

    public int ConfirmCalls { get; private set; }

    public Queue<RunningAppsChoice> RunningAnswers { get; } = new();

    public bool ContinueWithoutRestore { get; set; }

    public List<string> RestoreAsked { get; } = new();

    public List<(string Heading, string Message, bool IsError)> Messages { get; } = new();

    public UninstallChoice? ConfirmUninstall(InstalledProgram program, UninstallPlans plans)
    {
        ConfirmCalls++;
        return Choice;
    }

    public RunningAppsChoice AskAppsRunning(InstalledProgram program, IReadOnlyList<RunningProcessInfo> running) =>
        RunningAnswers.Count > 0 ? RunningAnswers.Dequeue() : RunningAppsChoice.Cancel;

    public bool AskContinueWithoutRestorePoint(string reason)
    {
        RestoreAsked.Add(reason);
        return ContinueWithoutRestore;
    }

    public void ShowMessage(string heading, string message, bool isError) => Messages.Add((heading, message, isError));

    public LeftoverChoice? LeftoverResponse { get; set; }

    public int ReviewCalls { get; private set; }

    public IReadOnlyList<LeftoverItem>? LastReviewItems { get; private set; }

    public bool LastRestoreAlready { get; private set; }

    public LeftoverChoice? ReviewLeftovers(InstalledProgram program, IReadOnlyList<LeftoverItem> items, bool restorePointAlreadyMade)
    {
        ReviewCalls++;
        LastReviewItems = items;
        LastRestoreAlready = restorePointAlreadyMade;
        return LeftoverResponse;
    }

    public ForceRemoveChoice? ForceRemoveResponse { get; set; } = new(CreateRestorePoint: true);

    public IReadOnlyList<LeftoverItem>? LastForceRemoveItems { get; private set; }

    public string? LastForceRemoveNote { get; private set; }

    public ForceRemoveChoice? ConfirmForceRemove(InstalledProgram program, IReadOnlyList<LeftoverItem> items, string? note)
    {
        LastForceRemoveItems = items;
        LastForceRemoveNote = note;
        return ForceRemoveResponse;
    }

    public BatchOptions? BatchResponse { get; set; } = new(PreferQuiet: false, CreateRestorePoint: true);

    public IReadOnlyList<InstalledProgram>? LastBatchPrograms { get; private set; }

    public BatchOptions? ConfirmBatch(IReadOnlyList<InstalledProgram> programs, bool defenderAvailable)
    {
        LastBatchPrograms = programs;
        return BatchResponse;
    }

    public bool ContinueAfterThreats { get; set; }

    public int ThreatPrompts { get; private set; }

    public bool AskContinueAfterThreats(string details)
    {
        ThreatPrompts++;
        return ContinueAfterThreats;
    }

    public bool ConfirmAnswer { get; set; } = true;

    public List<string> Confirmations { get; } = new();

    public bool Confirm(string heading, string message, string yesText, string noText, bool yesIsSafe)
    {
        Confirmations.Add(heading);
        return ConfirmAnswer;
    }

    public string? SavePath { get; set; }

    public string? AskSavePath(string title, string defaultFileName, string filter) => SavePath;

    public string? OpenPath { get; set; }

    public string? AskOpenPath(string title, string filter) => OpenPath;
}

internal sealed class FakeLeftoverCoordinator : ILeftoverCoordinator
{
    public Func<InstalledProgram, IReadOnlyList<InstalledProgram>, bool, CancellationToken, Task<LeftoverFlowResult>> Handler { get; set; } =
        (_, _, _, _) => Task.FromResult(new LeftoverFlowResult(
            LeftoverFlowStatus.NoneFound, 0, 0, 0, Array.Empty<CleanupFailure>(), "No leftovers found."));

    public List<(InstalledProgram Program, IReadOnlyList<InstalledProgram> Others, bool RestoreAlready)> Calls { get; } = new();

    public Task<LeftoverFlowResult> RunAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> otherPrograms,
        bool restorePointAlreadyMade,
        CancellationToken cancellationToken)
    {
        Calls.Add((program, otherPrograms, restorePointAlreadyMade));
        return Handler(program, otherPrograms, restorePointAlreadyMade, cancellationToken);
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public ProcessRunResult Result { get; set; } = new(true, 0, false, null);

    public List<(string FileName, string Arguments)> Calls { get; } = new();

    public Task<ProcessRunResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Result);
    }
}

internal sealed class FakeRestore : IRestorePointService
{
    public RestorePointResult Result { get; set; } = new(true, "Restore point created.");

    public int Calls { get; private set; }

    public string? LastDescription { get; private set; }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
    {
        Calls++;
        LastDescription = description;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeFinder : IRunningProcessFinder
{
    public Queue<IReadOnlyList<RunningProcessInfo>> Answers { get; } = new();

    public int Calls { get; private set; }

    public IReadOnlyList<RunningProcessInfo> FindRunning(InstalledProgram program)
    {
        Calls++;
        return Answers.Count > 0 ? Answers.Dequeue() : Array.Empty<RunningProcessInfo>();
    }
}

internal sealed class FakeCoordinator : IUninstallCoordinator
{
    public Func<InstalledProgram, CancellationToken, Task<UninstallOutcome>> Handler { get; set; } =
        (_, _) => Task.FromResult(new UninstallOutcome(UninstallStatus.Completed, "Done.", 0, Started: true));

    public List<InstalledProgram> Requested { get; } = new();

    public List<UninstallOptions> OptionsUsed { get; } = new();

    public Task<UninstallOutcome> RunInteractiveAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        Requested.Add(program);
        return Handler(program, cancellationToken);
    }

    public Task<UninstallOutcome> RunAsync(InstalledProgram program, UninstallOptions options, CancellationToken cancellationToken)
    {
        Requested.Add(program);
        OptionsUsed.Add(options);
        return Handler(program, cancellationToken);
    }
}

internal sealed class FakeIconProvider : IIconProvider
{
    /// <summary>Icons to hand out, keyed by program name. Missing names give null (no icon).</summary>
    public Dictionary<string, System.Windows.Media.ImageSource> Icons { get; } = new();

    public System.Windows.Media.ImageSource? GetIcon(InstalledProgram program) =>
        Icons.TryGetValue(program.Name, out var icon) ? icon : null;
}

internal sealed class FakeRegistryReader : IRegistryEntryReader
{
    public List<RegistryEntry> Entries { get; } = new();

    public IReadOnlyList<RegistryEntry> ReadAll() => Entries;
}

internal sealed class MemoryJournal : IBackupJournal
{
    public List<BackupEntry> Entries { get; } = new();

    public void Add(BackupEntry entry) => Entries.Add(entry);

    public IReadOnlyList<BackupEntry> ReadAll() => Entries;
}

/// <summary>Scripted PowerShell: every call is recorded; the handler decides the answer.</summary>
internal sealed class FakeShell : IPowerShellRunner
{
    public Func<string, IReadOnlyDictionary<string, string>, ScriptResult> Handler { get; set; } = (_, _) => new ScriptResult(0, "OK");

    public List<(string Script, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = new();

    public Task<ScriptResult> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        Calls.Add((script, environment));
        return Task.FromResult(Handler(script, environment));
    }
}

internal sealed class FakeConsoleRunner : IConsoleRunner
{
    public ScriptResult Result { get; set; } = new(0, string.Empty);

    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

    public Task<ScriptResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Result);
    }
}

internal sealed class FakeLeftoverCleaner : ILeftoverCleaner
{
    public CleanupResult Result { get; set; } = new(0, 1, 0, Array.Empty<CleanupFailure>(), false);

    public List<IReadOnlyList<LeftoverItem>> Calls { get; } = new();

    public Task<CleanupResult> CleanAsync(InstalledProgram program, IReadOnlyList<LeftoverItem> items, CancellationToken cancellationToken)
    {
        Calls.Add(items);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeStartupManager : IStartupManager
{
    public List<StartupItem> Items { get; } = new();

    public int Lists { get; private set; }

    public List<string> Calls { get; } = new();

    public StartupChangeResult Result { get; set; } = new(true, "ok");

    public Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken cancellationToken)
    {
        Lists++;
        return Task.FromResult<IReadOnlyList<StartupItem>>(Items.ToList());
    }

    public Task<StartupChangeResult> SetEnabledAsync(StartupItem item, bool enable, CancellationToken cancellationToken)
    {
        Calls.Add($"set:{item.Id}:{enable}");
        return Task.FromResult(Result);
    }

    public Task<StartupChangeResult> RestoreAsync(string itemId, bool enable, CancellationToken cancellationToken)
    {
        Calls.Add($"restore:{itemId}:{enable}");
        return Task.FromResult(Result);
    }

    public Task<StartupChangeResult> AddAsync(string programPath, CancellationToken cancellationToken)
    {
        Calls.Add($"add:{programPath}");
        return Task.FromResult(Result);
    }
}
