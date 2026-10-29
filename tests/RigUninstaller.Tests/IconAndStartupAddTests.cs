using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class CommandPathParserTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "RigIcons_" + Guid.NewGuid().ToString("N"));

    public CommandPathParserTests() => Directory.CreateDirectory(Path.Combine(_folder, "My App"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Make(string relative)
    {
        var path = Path.Combine(_folder, relative);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_quoted_path_with_arguments_gives_the_file()
    {
        var exe = Make(@"My App\app.exe");

        Assert.Equal(exe, CommandPathParser.ExtractFilePath($"\"{exe}\" --start --flag"));
    }

    [Fact]
    public void An_unquoted_path_with_spaces_and_arguments_gives_the_file()
    {
        var exe = Make(@"My App\tool.exe");

        Assert.Equal(exe, CommandPathParser.ExtractFilePath($"{exe} /s /quiet"));
    }

    [Fact]
    public void A_plain_path_is_returned_as_it_is()
    {
        var shortcut = Make(@"My App\Notes.lnk");

        Assert.Equal(shortcut, CommandPathParser.ExtractFilePath(shortcut));
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var exe = Make("app2.exe");
        Environment.SetEnvironmentVariable("RIG_ICON_TEST_DIR", _folder);

        Assert.Equal(exe, CommandPathParser.ExtractFilePath("%RIG_ICON_TEST_DIR%\\app2.exe -x"));
    }

    [Fact]
    public void A_bare_system_program_name_is_found_in_the_system_folder()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), CommandPathParser.ExtractFilePath("cmd.exe /c echo hi"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\definitely\not\here\app.exe -x")]
    [InlineData("\"unterminated")]
    [InlineData(@"\\deadhost\share\app.exe -x")]
    [InlineData("\"\\\\deadhost\\share\\app.exe\" -x")]
    public void Nothing_usable_gives_null(string? command)
    {
        Assert.Null(CommandPathParser.ExtractFilePath(command));
    }
}

public class StoreIconProviderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "RigStore_" + Guid.NewGuid().ToString("N"));

    public StoreIconProviderTests() => Directory.CreateDirectory(Path.Combine(_folder, "Assets"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static StoreApp App(string location) => new("Contoso.Notes", "Contoso.Notes_1_x64__abc", "CN=Contoso", "1.0", false, false, "Store", location);

    private void WriteManifest(string logoAttribute = "Assets\\Square44x44Logo.png") =>
        File.WriteAllText(Path.Combine(_folder, "AppxManifest.xml"),
            $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications><Application Id="App"><uap:VisualElements Square44x44Logo="{logoAttribute}" /></Application></Applications>
            </Package>
            """);

    private void WritePng(string name)
    {
        var bitmap = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_folder, "Assets", name));
        encoder.Save(stream);
    }

    [Theory]
    [InlineData("Square44x44Logo.targetsize-48.png", "Square44x44Logo.scale-200.png")]
    [InlineData("Square44x44Logo.targetsize-48_altform-unplated.png", "Square44x44Logo.targetsize-48.png")]
    [InlineData("Square44x44Logo.scale-200.png", "Square44x44Logo.scale-100.png")]
    [InlineData("Square44x44Logo.targetsize-48.png", "Square44x44Logo.targetsize-16.png")]
    public void The_better_picture_ranks_lower(string better, string worse)
    {
        Assert.True(StoreIconProvider.Rank(better, false) < StoreIconProvider.Rank(worse, false));
    }

    [Fact]
    public void High_contrast_and_light_variants_are_never_picked()
    {
        Assert.Equal(int.MaxValue, StoreIconProvider.Rank("Square44x44Logo.targetsize-48_contrast-white.png", false));
        Assert.Equal(int.MaxValue, StoreIconProvider.Rank("Square44x44Logo.targetsize-48_altform-lightunplated.png", false));
    }

    [Fact]
    public void The_logo_named_in_the_manifest_is_loaded()
    {
        WriteManifest();
        WritePng("Square44x44Logo.scale-200.png");

        var icon = new StoreIconProvider().GetIcon(App(_folder));

        Assert.NotNull(icon);
        Assert.True(icon!.IsFrozen);
    }

    [Fact]
    public void Missing_manifest_or_picture_or_location_gives_null()
    {
        var provider = new StoreIconProvider();

        Assert.Null(provider.GetIcon(App(string.Empty)));
        Assert.Null(provider.GetIcon(App(Path.Combine(_folder, "nope"))));
        Assert.Null(provider.GetIcon(App(_folder)));

        WriteManifest();
        Assert.Null(new StoreIconProvider().GetIcon(App(_folder)));
    }

    [Fact]
    public void A_logo_path_that_climbs_out_of_the_package_is_ignored()
    {
        WriteManifest(@"..\..\outside.png");

        Assert.Null(new StoreIconProvider().GetIcon(App(_folder)));
    }

    [Fact]
    public void A_logo_in_a_sibling_folder_with_the_same_name_start_is_ignored()
    {
        var sibling = _folder + "Evil";
        Directory.CreateDirectory(sibling);
        try
        {
            WriteManifest(@"..\" + Path.GetFileName(sibling) + @"\logo.png");
            File.Copy(Path.Combine(_folder, "Assets") + "\\..\\AppxManifest.xml", Path.Combine(sibling, "AppxManifest.xml"), true);
            var bitmap = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(sibling, "logo.png")))
            {
                encoder.Save(stream);
            }

            Assert.Null(new StoreIconProvider().GetIcon(App(_folder)));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void A_broken_picture_gives_null_instead_of_throwing()
    {
        WriteManifest();
        File.WriteAllText(Path.Combine(_folder, "Assets", "Square44x44Logo.scale-200.png"), "not a picture");

        Assert.Null(new StoreIconProvider().GetIcon(App(_folder)));
    }

    [Theory]
    [InlineData("19568ShareX.ShareX", "ShareX")]
    [InlineData("28017CharlesMilette.TranslucentTB", "CharlesMilette.TranslucentTB")]
    [InlineData("B9ECED6F.ArmouryCrate", "ArmouryCrate")]
    [InlineData("Microsoft.WindowsCalculator", "Microsoft.WindowsCalculator")]
    [InlineData("ASUSTeKCOMPUTERINC.MyASUS", "ASUSTeKCOMPUTERINC.MyASUS")]
    [InlineData("DEADBEEF.Tool", "DEADBEEF.Tool")] // eight letters and no digit: a real name, not an id
    public void Store_package_names_lose_the_publisher_id_prefix(string name, string expected)
    {
        var app = new StoreApp(name, name + "_1", "CN=X", "1.0", false, false, "Store");

        Assert.Equal(expected, app.DisplayName);
    }

    [Theory]
    [InlineData("CN=366A5DE5-2EC7-43FD-B559-0E4F3B8C5A11", null)]
    [InlineData("CN=Microsoft Corporation, O=Microsoft", "Microsoft Corporation")]
    [InlineData("", null)]
    public void A_publisher_that_is_only_an_id_is_not_shown(string publisher, string? expected)
    {
        var app = new StoreApp("A", "A_1", publisher, "1.0", false, false, "Store");

        Assert.Equal(expected, app.ReadablePublisher);
    }

    [Fact]
    public void The_store_parser_keeps_the_install_location()
    {
        var apps = StoreAppService.Parse("""[{"Name":"A","FullName":"A_1","Publisher":"CN=X","Version":"1","InstallLocation":"C:\\Program Files\\WindowsApps\\A_1"}]""");

        Assert.Equal(@"C:\Program Files\WindowsApps\A_1", Assert.Single(apps).InstallLocation);
    }
}

public class StartupAddTests : IDisposable
{
    private sealed class Registry : IStartupRegistry
    {
        public Dictionary<(StartupHive, bool), List<(string Name, string Command)>> Runs { get; } = new();

        public Dictionary<(StartupHive, string, string), byte[]> Approved { get; } = new();

        public Exception? ThrowOnSet { get; set; }

        public IReadOnlyList<(string Name, string Command)> GetRunValues(StartupHive hive, bool wow64) =>
            Runs.TryGetValue((hive, wow64), out var list) ? list : new List<(string, string)>();

        public byte[]? GetApproved(StartupHive hive, string approvedKey, string name) =>
            Approved.TryGetValue((hive, approvedKey, name), out var data) ? data : null;

        public void SetApproved(StartupHive hive, string approvedKey, string name, byte[] data) => Approved[(hive, approvedKey, name)] = data;

        public IReadOnlyList<string> GetStartupFolderFiles(bool allUsers) => new List<string>();

        public void SetRunValue(StartupHive hive, string name, string command)
        {
            if (ThrowOnSet is not null) throw ThrowOnSet;
            if (!Runs.TryGetValue((hive, false), out var list))
            {
                Runs[(hive, false)] = list = new List<(string, string)>();
            }

            list.Add((name, command));
        }
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "RigAdd_" + Guid.NewGuid().ToString("N"));
    private readonly Registry _registry = new();
    private readonly FakeLog _log = new();

    public StartupAddTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private StartupManager Create() => new(_registry, new FakeShell(), new MemoryJournal(), _log);

    private string Make(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task A_program_is_added_for_the_current_user_with_a_quoted_path_and_turned_on()
    {
        var exe = Make("Cool Tool.exe");

        var result = await Create().AddAsync(exe, CancellationToken.None);

        Assert.True(result.Success);
        var added = Assert.Single(_registry.Runs[(StartupHive.CurrentUser, false)]);
        Assert.Equal("Cool Tool", added.Name);
        Assert.Equal($"\"{exe}\"", added.Command);
        Assert.True(StartupApproved.IsEnabled(_registry.Approved[(StartupHive.CurrentUser, "Run", "Cool Tool")]));
        Assert.Contains(_log.Lines, l => l.Contains("Added") && l.Contains("Cool Tool"));
    }

    [Fact]
    public async Task Nothing_is_added_for_a_missing_file_a_wrong_type_or_a_network_path()
    {
        var text = Make("notes.txt");
        var manager = Create();

        Assert.False((await manager.AddAsync(Path.Combine(_folder, "ghost.exe"), CancellationToken.None)).Success);
        Assert.False((await manager.AddAsync(text, CancellationToken.None)).Success);
        Assert.False((await manager.AddAsync(@"\\server\share\app.exe", CancellationToken.None)).Success);
        Assert.False((await manager.AddAsync("app.exe", CancellationToken.None)).Success);
        Assert.False((await manager.AddAsync("C:app.exe", CancellationToken.None)).Success);
        Assert.False((await manager.AddAsync(@"\app.exe", CancellationToken.None)).Success);
        Assert.Empty(_registry.Runs);
    }

    [Fact]
    public async Task A_program_that_already_starts_is_not_added_twice()
    {
        var exe = Make("Steam.exe");
        _registry.Runs[(StartupHive.LocalMachine, false)] = new() { ("SteamLauncher", $"\"{exe}\" -silent") };

        var result = await Create().AddAsync(exe, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("already", result.Message);
        Assert.False(_registry.Runs.ContainsKey((StartupHive.CurrentUser, false)));
    }

    [Fact]
    public async Task A_name_that_is_taken_is_not_overwritten()
    {
        var exe = Make("Discord.exe");
        _registry.Runs[(StartupHive.CurrentUser, false)] = new() { ("Discord", @"C:\Other\Update.exe") };

        var result = await Create().AddAsync(exe, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(_registry.Runs[(StartupHive.CurrentUser, false)]);
    }

    [Fact]
    public async Task A_registry_failure_is_reported_not_thrown()
    {
        var exe = Make("Tool.exe");
        _registry.ThrowOnSet = new UnauthorizedAccessException("Access denied");

        var result = await Create().AddAsync(exe, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.Message);
    }
}

public class StartupViewModelAddTests
{
    private readonly FakeStartupManager _manager = new();
    private readonly FakeDialogs _dialogs = new();

    private StartupViewModel Create() => new(_manager, _dialogs, new FakeLog());

    [Fact]
    public async Task Picking_a_file_and_confirming_adds_it_then_reloads()
    {
        _dialogs.OpenPath = @"C:\Tools\tool.exe";
        var vm = Create();

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"add:C:\Tools\tool.exe" }, _manager.Calls);
        Assert.Equal(1, _manager.Lists);
        Assert.Equal("Added to startup", Assert.Single(_dialogs.Messages).Heading);
    }

    [Fact]
    public async Task Cancelling_the_file_picker_or_the_confirmation_changes_nothing()
    {
        var vm = Create();

        await vm.AddCommand.ExecuteAsync(null); // no file picked
        _dialogs.OpenPath = @"C:\Tools\tool.exe";
        _dialogs.ConfirmAnswer = false;
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Empty(_manager.Calls);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task A_failed_add_shows_the_reason_as_an_error()
    {
        _dialogs.OpenPath = @"C:\Tools\tool.exe";
        _manager.Result = new StartupChangeResult(false, "That file does not exist.");

        await Create().AddCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Equal("That file does not exist.", message.Message);
    }

    [Fact]
    public async Task Items_get_their_own_icon_or_the_no_icon_picture()
    {
        _manager.Items.Add(new StartupItem("RUN|CurrentUser|Run|A", "A", @"C:\a.exe", "Run (current user)", StartupKind.RunKey, true));
        _manager.Items.Add(new StartupItem("RUN|CurrentUser|Run|B", "B", @"C:\b.exe", "Run (current user)", StartupKind.RunKey, true));
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        icon.Freeze();
        var vm = new StartupViewModel(_manager, _dialogs, new FakeLog(), new FakeCommandIcons(icon));

        await vm.LoadAsync();

        Assert.Same(icon, vm.Items[0].Icon);
        Assert.NotSame(icon, vm.Items[1].Icon); // the placeholder (or null when no application resources exist)
    }

    private sealed class FakeCommandIcons : ICommandIconProvider
    {
        private readonly ImageSource _icon;

        public FakeCommandIcons(ImageSource icon) => _icon = icon;

        public ImageSource? GetIcon(string command) => command.Contains("a.exe") ? _icon : null;
    }
}
