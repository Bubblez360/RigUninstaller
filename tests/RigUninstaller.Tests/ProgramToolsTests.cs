using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramToolsTests
{
    private static readonly SafetyGuardRoots Roots = new(
        @"C:\", @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
        @"C:\Users\Tester", @"C:\Users\Tester\AppData\Roaming", @"C:\Users\Tester\AppData\Local");

    private static InstalledProgram P(
        string name,
        string? publisher = null,
        string? version = null,
        long? sizeKb = null,
        string? location = null,
        string? icon = null,
        string? uninstall = null,
        string key = "k") => new()
    {
        Name = name,
        Publisher = publisher,
        Version = version,
        EstimatedSizeKb = sizeKb,
        InstallLocation = location,
        DisplayIcon = icon,
        UninstallString = uninstall,
        RegistryKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + key,
        KeyName = key,
    };

    // ---- CSV ----

    [Fact]
    public void Csv_has_a_header_and_one_line_per_program()
    {
        var rows = new[] { new ProgramRowViewModel(P("Acme", "Acme Inc", "1.0", 2048, @"C:\Acme")) };

        var csv = CsvExporter.Build(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Name,Publisher,Version,Size (bytes),Install date,Install location,Signed by,Last used,Tags", csv[0]);
        Assert.Equal(@"Acme,Acme Inc,1.0,2097152,,C:\Acme,,,", csv[1]);
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_newlines()
    {
        var rows = new[] { new ProgramRowViewModel(P("Tool, \"Pro\"\nEdition")) };

        var csv = CsvExporter.Build(rows);

        Assert.Contains("\"Tool, \"\"Pro\"\"\nEdition\"", csv);
    }

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+cmd")]
    [InlineData("-1+2")]
    [InlineData("@x")]
    public void Csv_makes_formula_looking_text_harmless(string name)
    {
        var csv = CsvExporter.Build(new[] { new ProgramRowViewModel(P(name)) });

        Assert.Contains("\r\n'" + name, csv);
    }

    // ---- search URL ----

    [Fact]
    public void Search_url_is_https_and_encodes_the_names()
    {
        var url = SearchUrl.For(P("Acme & Co Editor", "Acme Inc"));

        Assert.Equal("https", url.Scheme);
        Assert.Contains("Acme%20%26%20Co%20Editor", url.AbsoluteUri);
    }

    // ---- program locator (target mode) ----

    [Fact]
    public void Locator_picks_the_program_whose_folder_contains_the_exe()
    {
        var acme = P("Acme", location: @"C:\Program Files\Acme", key: "a");
        var beta = P("Beta", location: @"C:\Program Files\Beta", key: "b");

        var found = ProgramLocator.Find(@"C:\Program Files\Acme\bin\acme.exe", new[] { acme, beta }, new SafetyGuard(Roots));

        Assert.Equal("Acme", found?.Name);
    }

    [Fact]
    public void Locator_prefers_the_exact_icon_file_and_then_the_deepest_folder()
    {
        var suite = P("Suite", location: @"C:\Program Files\Suite", key: "s");
        var tool = P("Suite Tool", location: @"C:\Program Files\Suite\Tool", key: "t");
        var withIcon = P("Icon Owner", icon: @"C:\Program Files\Suite\app.exe,0", key: "i");

        var guard = new SafetyGuard(Roots);

        Assert.Equal("Suite Tool", ProgramLocator.Find(@"C:\Program Files\Suite\Tool\x.exe", new[] { suite, tool }, guard)?.Name);
        Assert.Equal("Icon Owner", ProgramLocator.Find(@"C:\Program Files\Suite\app.exe", new[] { suite, tool, withIcon }, guard)?.Name);
    }

    [Fact]
    public void Locator_returns_null_when_nothing_matches_or_the_folder_is_protected()
    {
        var windows = P("Win", location: @"C:\Windows\System32", key: "w");

        Assert.Null(ProgramLocator.Find(@"C:\Windows\System32\notepad.exe", new[] { windows }, new SafetyGuard(Roots)));
        Assert.Null(ProgramLocator.Find(@"D:\x\y.exe", Array.Empty<InstalledProgram>(), new SafetyGuard(Roots)));
    }

    // ---- winget ----

    private static string Row(string name, string id, string version, string available, string source) =>
        name.PadRight(20) + id.PadRight(30) + version.PadRight(10) + available.PadRight(10) + source;

    private static readonly string WingetOutput = string.Join(
        "\r\n",
        "   - \\ | ",
        Row("Name", "Id", "Version", "Available", "Source"),
        new string('-', 70),
        Row("Git", "Git.Git", "2.45.1", "2.46.0", "winget"),
        Row("Notepad++", "Notepad++.Notepad++", "8.6", "", "winget"),
        Row("Mystery App", @"ARP\Machine\X64\{GUID}", "1.0", "", ""),
        "1 upgrades available.");

    [Fact]
    public void Winget_table_is_parsed_into_packages()
    {
        var packages = WingetTableParser.Parse(WingetOutput);

        Assert.Equal(3, packages.Count);
        Assert.Equal(new WingetPackage("Git", "Git.Git", "2.45.1", "2.46.0", "winget"), packages[0]);
        Assert.Null(packages[1].Available);
        Assert.Null(packages[2].Source);
        Assert.Equal(@"ARP\Machine\X64\{GUID}", packages[2].Id);
    }

    [Fact]
    public void Winget_output_without_a_table_gives_nothing()
    {
        Assert.Empty(WingetTableParser.Parse("No installed package found."));
    }

    [Fact]
    public void Winget_matcher_links_by_name_and_ignores_packages_winget_does_not_know()
    {
        var packages = WingetTableParser.Parse(WingetOutput);
        var git = P("git", key: "g");
        var mystery = P("Mystery App", key: "m");

        var match = WingetMatcher.Match(new[] { git, mystery }, packages);

        Assert.Equal("Git.Git", match[git.RegistryKeyPath].Id);
        Assert.False(match.ContainsKey(mystery.RegistryKeyPath));
    }

    private sealed class FakeConsole : IConsoleRunner
    {
        public ScriptResult Result { get; set; } = new(0, string.Empty);

        public IReadOnlyList<string>? LastArgs { get; private set; }

        public Task<ScriptResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            LastArgs = arguments;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Winget_service_lists_and_refuses_unsafe_ids()
    {
        var console = new FakeConsole { Result = new ScriptResult(0, WingetOutput) };
        var runner = new FakeRunner();
        var service = new WingetService(console, runner, new FakeLog(), @"C:\fake\winget.exe");

        var packages = await service.ListAsync(CancellationToken.None);
        var bad = await service.UninstallAsync("Git.Git & calc", CancellationToken.None);
        var good = await service.UpgradeAsync("Git.Git", CancellationToken.None);

        Assert.Equal(3, packages.Count);
        Assert.False(bad.Started);
        Assert.Contains("valid winget id", bad.Error);
        Assert.True(good.Started);
        Assert.Contains("upgrade --id Git.Git --exact", runner.Calls.Single().Arguments);
    }

    // ---- keep and bloatware stores ----

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zap-stores-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void Keep_tags_survive_a_restart()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "keep.json");
        var program = P("Acme", key: "a");

        new KeepStore(file, new FakeLog()).SetKept(program, true);
        var again = new KeepStore(file, new FakeLog());

        Assert.True(again.IsKept(program));
        again.SetKept(program, false);
        Assert.False(new KeepStore(file, new FakeLog()).IsKept(program));
    }

    [Fact]
    public void A_damaged_keep_file_means_nothing_is_kept_and_is_logged()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "keep.json");
        File.WriteAllText(file, "{ not json");
        var log = new FakeLog();

        Assert.False(new KeepStore(file, log).IsKept(P("Acme")));
        Assert.Contains(log.Lines, l => l.StartsWith("WARN"));
    }

    [Fact]
    public void Bloatware_list_is_created_with_starter_entries_and_matches_names_and_publishers()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "bloatware.json");

        var list = new BloatwareList(file, new FakeLog());

        Assert.True(File.Exists(file));
        Assert.True(list.IsBloat(P("McAfee LiveSafe")));
        Assert.True(list.IsBloat(P("Some Game", publisher: "WildTangent Inc")));
        Assert.False(list.IsBloat(P("Notepad++")));
    }

    [Fact]
    public void Edited_bloatware_list_is_picked_up_on_reload_and_short_entries_are_ignored()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "bloatware.json");
        var list = new BloatwareList(file, new FakeLog());

        File.WriteAllText(file, "{ \"names\": [\"zzzapp\", \"ab\"], \"publishers\": [] }");
        list.Reload();

        Assert.True(list.IsBloat(P("My ZZZAPP 2")));
        Assert.False(list.IsBloat(P("McAfee")));
        Assert.False(list.IsBloat(P("Table")));
    }

    // ---- insight: last used, orphan, signature ----

    private sealed class FakeFs : IFileSystemProbe
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetDirectories(string folder) => Array.Empty<string>();

        public IReadOnlyList<string> GetFiles(string folder) => Files.TryGetValue(folder, out var list) ? list : new List<string>();

        public bool IsReparsePoint(string path) => false;

        public bool Exists(string path) => Existing.Contains(path);
    }

    private sealed class FakeTimes : IFileTimes
    {
        public Dictionary<string, DateTime> Access { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<DateTime>> Prefetch { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTime? GetLastAccess(string path) => Access.TryGetValue(path, out var t) ? t : null;

        public IReadOnlyList<DateTime> GetPrefetchTimes(string exeName) => Prefetch.TryGetValue(exeName, out var l) ? l : new List<DateTime>();
    }

    [Fact]
    public void Last_used_prefers_prefetch_over_the_access_time()
    {
        var fs = new FakeFs();
        var times = new FakeTimes();
        fs.Existing.Add(@"C:\Acme\acme.exe");
        times.Access[@"C:\Acme\acme.exe"] = new DateTime(2020, 1, 1);
        times.Prefetch["acme.exe"] = new List<DateTime> { new(2026, 3, 1), new(2026, 5, 9) };

        var info = new LastUsedFinder(fs, times).Find(P("Acme", icon: @"C:\Acme\acme.exe,0"));

        Assert.Equal(new DateTime(2026, 5, 9), info.When);
        Assert.Contains("Prefetch", info.Source);
    }

    [Fact]
    public void Last_used_falls_back_to_access_time_and_finds_the_exe_by_name_in_the_install_folder()
    {
        var fs = new FakeFs();
        var times = new FakeTimes();
        fs.Files[@"C:\Acme"] = new List<string> { @"C:\Acme\unins000.exe", @"C:\Acme\Updater.exe", @"C:\Acme\AcmeEditor.exe" };
        times.Access[@"C:\Acme\AcmeEditor.exe"] = new DateTime(2025, 12, 24);

        var info = new LastUsedFinder(fs, times).Find(P("Acme Editor", location: @"C:\Acme"));

        Assert.Equal(new DateTime(2025, 12, 24), info.When);
        Assert.Contains("access", info.Source);
    }

    [Fact]
    public void Last_used_is_unknown_without_an_exe()
    {
        var info = new LastUsedFinder(new FakeFs(), new FakeTimes()).Find(P("Acme"));

        Assert.Null(info.When);
    }

    private sealed class FakeSignatures : ISignatureChecker
    {
        public int Calls { get; private set; }

        public SignatureInfo Check(string path)
        {
            Calls++;
            return new SignatureInfo(SignatureState.Unsigned, null, "none");
        }
    }

    private sealed class FakeLastUsed : ILastUsedFinder
    {
        public LastUsedInfo Find(InstalledProgram program) => new(null, string.Empty);
    }

    [Fact]
    public void Insight_flags_a_missing_uninstaller_and_a_missing_install_folder()
    {
        var fs = new FakeFs();
        var service = new ProgramInsightService(new FakeSignatures(), new FakeLastUsed(), fs);

        var noUninstaller = service.Analyze(P("A", uninstall: @"""C:\Gone\unins000.exe"" /S"));
        fs.Existing.Add(@"C:\Here\unins000.exe");
        var noFolder = service.Analyze(P("B", uninstall: @"""C:\Here\unins000.exe""", location: @"C:\Gone"));
        fs.Existing.Add(@"C:\Here");
        var healthy = service.Analyze(P("C", uninstall: @"""C:\Here\unins000.exe""", location: @"C:\Here"));

        Assert.Contains("uninstaller file", noUninstaller.OrphanReason);
        Assert.Contains("install folder", noFolder.OrphanReason);
        Assert.Null(healthy.OrphanReason);
    }

    [Fact]
    public void Insight_never_calls_an_msi_program_orphan_for_its_uninstaller_and_checks_each_file_once()
    {
        var fs = new FakeFs();
        var signatures = new FakeSignatures();
        var service = new ProgramInsightService(signatures, new FakeLastUsed(), fs);
        var msi = service.Analyze(P("Msi", uninstall: "MsiExec.exe /X{12345678-ABCD-4EF0-9876-0123456789AB}"));
        fs.Existing.Add(@"C:\Here\unins000.exe");

        service.Analyze(P("X", uninstall: @"""C:\Here\unins000.exe""", key: "x"));
        service.Analyze(P("Y", uninstall: @"""C:\Here\unins000.exe""", key: "y"));

        Assert.Null(msi.OrphanReason);
        Assert.Equal("Windows Installer", msi.Signature.Signer);
        Assert.Equal(1, signatures.Calls);
    }

    [Fact]
    public void Real_signature_check_tells_signed_from_unsigned_files()
    {
        var checker = new SignatureChecker();
        var unsigned = checker.Check(typeof(SignatureChecker).Assembly.Location);
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe");

        Assert.Equal(SignatureState.Unsigned, unsigned.State);
        Assert.Equal(SignatureState.NotChecked, checker.Check(@"C:\definitely\missing.exe").State);
        if (File.Exists(dotnet))
        {
            var signed = checker.Check(dotnet);
            Assert.Equal(SignatureState.Signed, signed.State);
            Assert.Contains("Microsoft", signed.Signer);
        }
    }

    // ---- row view model additions ----

    [Fact]
    public void Row_tags_and_texts_follow_keep_bloat_and_insight()
    {
        var row = new ProgramRowViewModel(P("Acme"));
        row.IsKept = true;
        row.IsBloat = true;
        row.Insight = new ProgramInsight(
            new SignatureInfo(SignatureState.Unsigned, null, "none"),
            new LastUsedInfo(new DateTime(2026, 1, 2), "Prefetch"),
            "The uninstaller file is missing.");

        Assert.Equal("Keep, Bloatware, Broken entry, Unsigned", row.Tags);
        Assert.Equal("Unsigned", row.SignedByText);
        Assert.True(row.IsUnsigned);
        Assert.True(row.IsOrphan);
        Assert.Equal("2026-01-02", row.LastUsedText);
    }

    [Fact]
    public void A_last_used_date_from_the_access_time_is_marked_approximate()
    {
        var row = new ProgramRowViewModel(P("Acme"));

        row.Insight = new ProgramInsight(
            new SignatureInfo(SignatureState.Signed, "Acme", "ok"),
            new LastUsedInfo(new DateTime(2026, 1, 2), "File last-access time (may be off)"),
            null);
        Assert.Equal("~2026-01-02", row.LastUsedText);

        row.Insight = new ProgramInsight(
            new SignatureInfo(SignatureState.Signed, "Acme", "ok"),
            new LastUsedInfo(null, string.Empty),
            null);
        Assert.Equal("—", row.LastUsedText);
    }

    [Fact]
    public void Ticking_a_row_calls_back_so_the_batch_button_can_update()
    {
        var calls = 0;
        var row = new ProgramRowViewModel(P("Acme"), () => calls++);

        row.IsChecked = true;
        row.IsChecked = true;

        Assert.Equal(1, calls);
    }
}
