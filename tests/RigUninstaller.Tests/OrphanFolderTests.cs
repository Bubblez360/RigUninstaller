using System.Diagnostics;
using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class OrphanFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RigOrphan_" + Guid.NewGuid().ToString("N"));
    private readonly FakeLog _log = new();
    private readonly List<string> _running = new();
    private readonly string _roaming;
    private readonly string _programFiles;
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    public OrphanFolderScannerTests()
    {
        _roaming = Path.Combine(_root, "Roaming");
        _programFiles = Path.Combine(_root, "ProgramFiles");
        Directory.CreateDirectory(_roaming);
        Directory.CreateDirectory(_programFiles);
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList())
        {
            Directory.Delete(link, recursive: false);
        }

        Directory.Delete(_root, recursive: true);
    }

    private OrphanFolderScanner Create(OrphanScanOptions? options = null)
    {
        var guard = new SafetyGuard(new SafetyGuardRoots(
            SystemDrive: Path.GetPathRoot(_root)!,
            WindowsDir: Path.Combine(_root, "Windows"),
            ProgramFiles: _programFiles,
            ProgramFilesX86: Path.Combine(_root, "ProgramFilesX86"),
            ProgramData: Path.Combine(_root, "ProgramData"),
            UserProfile: Path.Combine(_root, "Profile"),
            AppDataRoaming: _roaming,
            AppDataLocal: Path.Combine(_root, "Local")));
        return new OrphanFolderScanner(
            new[] { ("AppData (Roaming)", _roaming), ("Program Files", _programFiles) },
            guard, new FolderSizer(), () => Now, () => _running, options ?? new OrphanScanOptions(), _log);
    }

    /// <summary>A folder holding one file, with everything last changed <paramref name="daysAgo"/> days before "now".</summary>
    private string MakeFolder(string root, string name, int daysAgo = 200, int bytes = 2 * 1024 * 1024)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(file, new byte[bytes]);
        var when = Now.AddDays(-daysAgo);
        File.SetLastWriteTimeUtc(file, when);
        Directory.SetLastWriteTimeUtc(folder, when);
        return folder;
    }

    private static InstalledProgram Program(string name, string? publisher = null, string? location = null) => new()
    {
        Name = name,
        Publisher = publisher,
        InstallLocation = location,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    [Fact]
    public async Task An_old_big_folder_that_no_program_owns_is_listed_with_a_plain_reason()
    {
        var folder = MakeFolder(_roaming, "OldGameLauncher", daysAgo: 200);

        var found = Assert.Single(await Create().ScanAsync(new[] { Program("Discord") }, CancellationToken.None));

        Assert.Equal(folder, found.Path);
        Assert.Equal("OldGameLauncher", found.Name);
        Assert.Equal("AppData (Roaming)", found.Location);
        Assert.Equal(2 * 1024 * 1024, found.Bytes);
        Assert.Contains("No installed program is called \"OldGameLauncher\"", found.Reason);
        Assert.Contains("200 days", found.Reason);
    }

    [Fact]
    public async Task Folders_that_match_an_installed_program_its_publisher_or_install_folder_are_left_out()
    {
        MakeFolder(_roaming, "discord");
        MakeFolder(_roaming, "OBS-Studio");
        MakeFolder(_roaming, "Riot Games");
        MakeFolder(_roaming, "MyCustomTool");
        MakeFolder(_roaming, "Overwolf");
        var programs = new[]
        {
            Program("Discord"),
            Program("OBS Studio"),
            Program("Riot Client", publisher: "Riot Games, Inc."),
            Program("Some Tool", location: @"D:\Apps\MyCustomTool"),
            Program("Overwolf 0.311.1.4"), // the version is part of the name; the folder is still its own
        };

        var found = await Create().ScanAsync(programs, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task A_program_that_is_running_right_now_owns_its_folder()
    {
        MakeFolder(_roaming, "PortableThing");
        _running.Add("PortableThing");

        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Microsoft Office")]
    [InlineData("Windows Defender")]
    [InlineData("Packages")]
    [InlineData("Google")]
    [InlineData("NVIDIA Corporation")]
    [InlineData("Common Files")]
    [InlineData("{1234ABCD-0000-0000-0000-000000000000}")]
    [InlineData("regid.1991-06.com.microsoft")]
    [InlineData(".cache")]
    [InlineData("ab")]
    [InlineData("cache")]
    [InlineData("Logs")]
    [InlineData("Crashpad")]
    public async Task Shared_system_and_too_short_names_are_never_listed(string name)
    {
        MakeFolder(_roaming, name);

        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_changed_recently_is_left_alone_because_something_may_be_using_it()
    {
        MakeFolder(_roaming, "FreshFolder", daysAgo: 3);

        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task One_recent_file_deeper_inside_keeps_the_whole_folder_off_the_list()
    {
        var folder = MakeFolder(_roaming, "AliveInside", daysAgo: 300);
        var nested = Path.Combine(folder, "logs", "today");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "x.log"), "alive");
        File.SetLastWriteTimeUtc(Path.Combine(nested, "x.log"), Now.AddDays(-1));

        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task Tiny_folders_are_not_worth_listing_unless_the_limit_is_lowered()
    {
        MakeFolder(_roaming, "TinyLeftover", bytes: 10 * 1024);

        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
        Assert.Single(await Create(new OrphanScanOptions(MinBytes: 1024)).ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task The_biggest_come_first_and_every_root_is_searched()
    {
        MakeFolder(_roaming, "SmallOne", bytes: 1 * 1024 * 1024);
        MakeFolder(_programFiles, "BigOne", bytes: 5 * 1024 * 1024);
        MakeFolder(_roaming, "MiddleOne", bytes: 3 * 1024 * 1024);

        var found = await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None);

        Assert.Equal(new[] { "BigOne", "MiddleOne", "SmallOne" }, found.Select(f => f.Name));
        Assert.Equal("Program Files", found[0].Location);
    }

    [Fact]
    public async Task A_missing_root_is_simply_skipped()
    {
        var scanner = new OrphanFolderScanner(
            new[] { ("Nowhere", Path.Combine(_root, "NotThere")) }, new SafetyGuard(), new FolderSizer(), () => Now, () => _running, new OrphanScanOptions(), _log);

        Assert.Empty(await scanner.ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task A_link_to_another_folder_is_never_listed_or_followed()
    {
        var target = MakeFolder(_root, "RealStuff");
        var link = Path.Combine(_roaming, "LinkedLeftover");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            process.WaitForExit();
        }

        Assert.True(Directory.Exists(link));
        Assert.Empty(await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_scan_stops()
    {
        MakeFolder(_roaming, "Something");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ScanAsync(Array.Empty<InstalledProgram>(), cts.Token));
    }

    [Fact]
    public async Task Scanning_never_changes_anything()
    {
        var folder = MakeFolder(_roaming, "LeaveMeBe");

        await Create().ScanAsync(Array.Empty<InstalledProgram>(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(folder, "data.bin")));
    }

    // ---- name matching ----

    [Theory]
    [InlineData("OBS-Studio", "OBS Studio", true)]
    [InlineData("obs", "OBS Studio", true)]           // a short form of an installed name
    [InlineData("Riot Games", "Riot Games, Inc.", true)]
    [InlineData("Discord", "Discord", true)]
    [InlineData("OldGameLauncher", "Discord", false)]
    [InlineData("Epic", "Discord", false)]
    [InlineData("Code", "Microsoft Visual Studio Code", true)] // data kept under one word of the program's name
    [InlineData("Studio", "OBS Studio", true)]
    [InlineData("Cod", "Microsoft Visual Studio Code", true)]  // three letters: judged too short, never listed
    public void Folder_names_are_compared_ignoring_case_spaces_and_punctuation(string folder, string program, bool expectedKnown)
    {
        var known = OrphanFolderScanner.KnownKeys(new[] { Program(program, publisher: program) }, Array.Empty<string>());

        Assert.Equal(expectedKnown, OrphanFolderScanner.IsKnown(folder, known));
    }

    [Fact]
    public void Normalize_keeps_only_lower_case_letters_and_digits()
    {
        Assert.Equal("7zip2408", OrphanFolderScanner.Normalize("7-Zip 24.08"));
        Assert.Equal(string.Empty, OrphanFolderScanner.Normalize(null));
    }
}

public class OrphanScanViewModelTests
{
    private sealed class FakeOrphanScanner : IOrphanFolderScanner
    {
        public IReadOnlyList<OrphanFolder> Result { get; set; } = Array.Empty<OrphanFolder>();

        public IReadOnlyList<InstalledProgram>? Programs { get; private set; }

        public Task<IReadOnlyList<OrphanFolder>> ScanAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken)
        {
            Programs = programs;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCleaner : ILeftoverCleaner
    {
        public CleanupResult Result { get; set; } = new(0, 0, 0, Array.Empty<CleanupFailure>(), false);

        public InstalledProgram? Program { get; private set; }

        public List<LeftoverItem> Items { get; } = new();

        public Action<IReadOnlyList<LeftoverItem>>? OnClean { get; set; }

        public Task<CleanupResult> CleanAsync(InstalledProgram program, IReadOnlyList<LeftoverItem> items, CancellationToken cancellationToken)
        {
            Program = program;
            Items.AddRange(items);
            OnClean?.Invoke(items);
            return Task.FromResult(Result);
        }
    }

    private static OrphanFolder Folder(string name, long mb) =>
        new($@"C:\Users\PC\AppData\Roaming\{name}", name, "AppData (Roaming)", mb * 1024 * 1024, new DateTime(2025, 12, 1), $"No installed program is called \"{name}\".");

    private readonly FakeOrphanScanner _scanner = new();
    private readonly RecordingCleaner _cleaner = new();
    private readonly FakeFiles _files = new();
    private readonly FakeExternal _external = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeLog _log = new();

    private OrphanScanViewModel Create() => new(_scanner, _cleaner, _files, _external, _dialogs, _log);

    private async Task<OrphanScanViewModel> FoundAsync()
    {
        _scanner.Result = new[] { Folder("OldA", 300), Folder("OldB", 50), Folder("OldC", 5) };
        var vm = Create();
        await vm.FindAsync();
        return vm;
    }

    [Fact]
    public async Task Finding_lists_the_folders_with_nothing_ticked_and_hands_over_the_installed_programs()
    {
        var vm = Create();
        var programs = new[] { new InstalledProgram { Name = "Discord", RegistryKeyPath = "k", KeyName = "k" } };
        vm.SetPrograms(programs);
        _scanner.Result = new[] { Folder("OldA", 300), Folder("OldB", 50) };

        await vm.FindAsync();

        Assert.Same(programs, _scanner.Programs);
        Assert.Equal(new[] { "OldA", "OldB" }, vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
        Assert.Equal("Nothing ticked", vm.SelectedText);
        Assert.False(vm.MoveCommand.CanExecute(null));
        Assert.Contains("350 MB in total", vm.StatusText);
    }

    [Fact]
    public async Task Nothing_found_says_so_in_words()
    {
        var vm = Create();

        await vm.FindAsync();

        Assert.True(vm.IsEmptyAfterScan);
        Assert.Contains("Nothing suspicious found", vm.StatusText);
    }

    [Fact]
    public async Task Ticking_counts_the_folders_and_their_size_and_labels_the_button()
    {
        var vm = await FoundAsync();

        vm.Rows[0].IsChecked = true;
        vm.Rows[2].IsChecked = true;

        Assert.Equal("2 ticked, 305 MB", vm.SelectedText);
        Assert.Equal("Move 2 to Recycle Bin", vm.MoveButtonText);
        Assert.True(vm.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Declining_the_confirmation_moves_nothing()
    {
        var vm = await FoundAsync();
        vm.Rows[0].IsChecked = true;
        _dialogs.ConfirmAnswer = false;

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.Empty(_cleaner.Items);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task Confirming_sends_the_ticked_folders_to_the_cleaner_under_one_clear_name_and_drops_them_from_the_list()
    {
        var vm = await FoundAsync();
        vm.Rows[0].IsChecked = true;
        vm.Rows[1].IsChecked = true;
        _dialogs.ConfirmAnswer = true;
        _cleaner.Result = new CleanupResult(2, 0, 0, Array.Empty<CleanupFailure>(), false);
        // Only OldC is still on disk afterwards, as it would be after a real move.
        _files.Existing.Add(@"C:\Users\PC\AppData\Roaming\OldC");

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.Equal("Old leftover folders", _cleaner.Program!.Name);
        Assert.Equal(new[] { @"C:\Users\PC\AppData\Roaming\OldA", @"C:\Users\PC\AppData\Roaming\OldB" }, _cleaner.Items.Select(i => i.Path));
        Assert.All(_cleaner.Items, i => Assert.Equal(LeftoverKind.Folder, i.Kind));
        Assert.Equal(new[] { "OldC" }, vm.Rows.Select(r => r.Name));
        Assert.Contains("Moved 2 folders (350 MB) to the Recycle Bin", vm.ResultText);
        Assert.Contains("Undo center", vm.ResultText);
        Assert.Contains("Move 2 folders", Assert.Single(_dialogs.Confirmations));
    }

    [Fact]
    public async Task A_folder_that_could_not_be_moved_stays_in_the_list_and_the_reason_is_shown()
    {
        var vm = await FoundAsync();
        vm.Rows[0].IsChecked = true;
        _dialogs.ConfirmAnswer = true;
        _files.Existing.Add(@"C:\Users\PC\AppData\Roaming\OldA"); // still there
        _cleaner.Result = new CleanupResult(0, 0, 0, new[] { new CleanupFailure(new LeftoverItem(LeftoverKind.Folder, @"C:\Users\PC\AppData\Roaming\OldA", Confidence.Low, "x"), "in use") }, false);

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.Contains(vm.Rows, r => r.Name == "OldA");
        var message = Assert.Single(_dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains("in use", message.Message);
        Assert.Contains("could not be moved", vm.ResultText);
    }

    [Fact]
    public async Task Look_opens_the_folder_in_Explorer()
    {
        var vm = await FoundAsync();

        vm.OpenCommand.Execute(vm.Rows[1]);

        Assert.Equal(new[] { @"folder:C:\Users\PC\AppData\Roaming\OldB" }, _external.Calls);
    }

    [Fact]
    public async Task A_scan_failure_is_shown_not_thrown()
    {
        var failing = new ThrowingScanner();
        var vm = new OrphanScanViewModel(failing, _cleaner, _files, _external, _dialogs, _log);

        await vm.FindAsync();

        Assert.Contains("Could not scan", vm.StatusText);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR"));
        Assert.False(vm.IsBusy);
    }

    private sealed class ThrowingScanner : IOrphanFolderScanner
    {
        public Task<IReadOnlyList<OrphanFolder>> ScanAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken) =>
            throw new IOException("disk error");
    }
}
