using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class LeftoverScannerTests
{
    private static readonly SafetyGuardRoots Roots = new(
        SystemDrive: @"C:\",
        WindowsDir: @"C:\Windows",
        ProgramFiles: @"C:\Program Files",
        ProgramFilesX86: @"C:\Program Files (x86)",
        ProgramData: @"C:\ProgramData",
        UserProfile: @"C:\Users\Tester",
        AppDataRoaming: @"C:\Users\Tester\AppData\Roaming",
        AppDataLocal: @"C:\Users\Tester\AppData\Local");

    private const string UserStart = @"C:\Users\Tester\AppData\Roaming\Microsoft\Windows\Start Menu\Programs";
    private const string CommonStart = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";

    private sealed class FakeFs : IFileSystemProbe
    {
        public Dictionary<string, List<string>> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddDir(string parent, string name)
        {
            if (!Dirs.TryGetValue(parent, out var list)) Dirs[parent] = list = new List<string>();
            list.Add(Path.Combine(parent, name));
        }

        public void AddFile(string parent, string name)
        {
            if (!Files.TryGetValue(parent, out var list)) Files[parent] = list = new List<string>();
            list.Add(Path.Combine(parent, name));
        }

        public IReadOnlyList<string> GetDirectories(string folder) => Dirs.TryGetValue(folder, out var l) ? l : new List<string>();

        public IReadOnlyList<string> GetFiles(string folder) => Files.TryGetValue(folder, out var l) ? l : new List<string>();

        public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);

        public bool Exists(string path) => true;
    }

    private sealed class FakeReg : IRegistryProbe
    {
        public Dictionary<string, List<string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string parent, string name)
        {
            if (!Keys.TryGetValue(parent, out var list)) Keys[parent] = list = new List<string>();
            list.Add(name);
        }

        public IReadOnlyList<string> GetSubKeyNames(string keyPath) => Keys.TryGetValue(keyPath, out var l) ? l : new List<string>();
    }

    private readonly FakeFs _fs = new();
    private readonly FakeReg _reg = new();
    private readonly FakeLog _log = new();

    private LeftoverScanner Scanner(string ownFolder = @"C:\Tools\RigUninstaller") =>
        new(_fs, _reg, new SafetyGuard(Roots), Roots, ownFolder, _log);

    private static InstalledProgram Removed(string name = "Acme Editor 2.5", string? publisher = "Acme Corp") => new()
    {
        Name = name,
        Publisher = publisher,
        RegistryKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme",
        KeyName = "Acme",
    };

    private static InstalledProgram Other(string name, string installLocation) => new()
    {
        Name = name,
        InstallLocation = installLocation,
        RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
        KeyName = name,
    };

    private IReadOnlyList<LeftoverItem> Scan(InstalledProgram? program = null, params InstalledProgram[] others) =>
        Scanner().Scan(program ?? Removed(), others);

    // ---- files and folders ----

    [Fact]
    public void Finds_a_folder_named_like_the_program_in_program_files()
    {
        _fs.AddDir(@"C:\Program Files", "Acme Editor");
        _fs.AddDir(@"C:\Program Files", "Unrelated App");

        var item = Assert.Single(Scan());

        Assert.Equal(LeftoverKind.Folder, item.Kind);
        Assert.Equal(@"C:\Program Files\Acme Editor", item.Path);
        Assert.Equal(Confidence.High, item.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Users\Tester\AppData\Roaming")]
    [InlineData(@"C:\Users\Tester\AppData\Local")]
    [InlineData(@"C:\Users\Tester\AppData\Local\Programs")]
    [InlineData(@"C:\ProgramData")]
    public void Looks_in_every_requested_location(string root)
    {
        _fs.AddDir(root, "AcmeEditor");

        var item = Assert.Single(Scan());

        Assert.Equal(Path.Combine(root, "AcmeEditor"), item.Path);
    }

    [Fact]
    public void Finds_start_menu_shortcuts_and_folders()
    {
        _fs.AddFile(UserStart, "Acme Editor.lnk");
        _fs.AddDir(CommonStart, "Acme Editor");
        _fs.AddFile(CommonStart, "Other.lnk");

        var items = Scan();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == LeftoverKind.File && i.Path == UserStart + @"\Acme Editor.lnk");
        Assert.Contains(items, i => i.Kind == LeftoverKind.Folder && i.Path == CommonStart + @"\Acme Editor");
    }

    [Fact]
    public void Publisher_folder_is_low_and_its_matching_children_are_listed_separately()
    {
        _fs.AddDir(@"C:\Program Files", "Acme");
        _fs.AddDir(@"C:\Program Files\Acme", "Acme Editor");
        _fs.AddDir(@"C:\Program Files\Acme", "Other Product");

        var items = Scan();

        Assert.Equal(2, items.Count);
        var publisher = Assert.Single(items, i => i.Path == @"C:\Program Files\Acme");
        Assert.Equal(Confidence.Low, publisher.Confidence);
        Assert.Contains("publisher", publisher.Reason, StringComparison.OrdinalIgnoreCase);
        var child = Assert.Single(items, i => i.Path == @"C:\Program Files\Acme\Acme Editor");
        Assert.Equal(Confidence.High, child.Confidence);
    }

    [Fact]
    public void A_vendor_folder_named_like_the_first_word_is_low_not_medium()
    {
        // Uninstalling Google Chrome must not offer the whole Google folder as a likely leftover.
        _fs.AddDir(@"C:\Program Files", "Google");
        _fs.AddDir(@"C:\Program Files\Google", "Chrome");
        _fs.AddDir(@"C:\Program Files\Google", "Drive");

        var items = Scan(Removed("Google Chrome", "Google LLC"));

        Assert.Equal(Confidence.Low, Assert.Single(items, i => i.Path == @"C:\Program Files\Google").Confidence);
        Assert.Equal(Confidence.Medium, Assert.Single(items, i => i.Path == @"C:\Program Files\Google\Chrome").Confidence);
        Assert.DoesNotContain(items, i => i.Path.EndsWith("Drive"));
    }

    [Fact]
    public void The_products_own_folder_is_not_searched_inside()
    {
        _fs.AddDir(@"C:\Users\Tester\AppData\Local", "Discord");
        _fs.AddFile(@"C:\Users\Tester\AppData\Local\Discord", "Discord.exe");
        _fs.AddDir(@"C:\Users\Tester\AppData\Local\Discord", "app-1.0");

        var items = Scan(Removed("Discord", "Discord Inc."));

        Assert.Equal(@"C:\Users\Tester\AppData\Local\Discord", Assert.Single(items).Path);
    }

    [Fact]
    public void An_item_inside_a_listed_folder_of_equal_or_higher_confidence_is_dropped()
    {
        // Removing the publisher folder removes "Editor Settings" too, so listing it twice only confuses.
        _fs.AddDir(@"C:\Program Files", "Acme");
        _fs.AddDir(@"C:\Program Files\Acme", "Editor Settings");

        var items = Scan();

        Assert.Equal(@"C:\Program Files\Acme", Assert.Single(items).Path);
    }

    [Fact]
    public void A_more_certain_item_inside_a_less_certain_folder_stays_listed()
    {
        _fs.AddDir(@"C:\Program Files", "Acme");
        _fs.AddDir(@"C:\Program Files\Acme", "Acme Editor");

        Assert.Equal(2, Scan().Count);
    }

    [Fact]
    public void Another_installed_program_with_the_same_name_caps_the_rating_at_medium()
    {
        // Two Python versions installed: the shared "Python" folder belongs to both.
        _fs.AddDir(@"C:\Users\Tester\AppData\Local\Programs", "Python");
        _reg.Add(@"HKCU\Software", "Python");

        var items = Scan(Removed("Python 3.11.9 (64-bit)", "Python Software Foundation"), Other("Python 3.14.3", @"C:\somewhere\else"));

        Assert.All(items, i => Assert.Equal(Confidence.Medium, i.Confidence));
        Assert.All(items, i => Assert.Contains("Python 3.14.3", i.Reason));
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Shared_publisher_folders_like_Microsoft_are_not_listed_but_their_matching_children_are()
    {
        _fs.AddDir(@"C:\Users\Tester\AppData\Local", "Microsoft");
        _fs.AddDir(@"C:\Users\Tester\AppData\Local\Microsoft", "Acme Editor");

        var items = Scan(Removed(publisher: "Microsoft Corporation"));

        var item = Assert.Single(items);
        Assert.Equal(@"C:\Users\Tester\AppData\Local\Microsoft\Acme Editor", item.Path);
    }

    [Fact]
    public void Windows_folders_are_never_listed_even_when_the_name_matches()
    {
        _fs.AddDir(@"C:\Program Files", "Windows Defender");
        _fs.AddDir(@"C:\Program Files", "WindowsApps");

        var items = Scan(Removed("Windows Defender Helper", publisher: null));

        Assert.Empty(items);
    }

    [Fact]
    public void Reparse_points_are_skipped()
    {
        _fs.AddDir(@"C:\Program Files", "Acme Editor");
        _fs.ReparsePoints.Add(@"C:\Program Files\Acme Editor");

        Assert.Empty(Scan());
    }

    [Fact]
    public void The_apps_own_folder_is_never_listed()
    {
        // A matching folder that contains this app must never be offered for deletion.
        _fs.AddDir(@"C:\Program Files", "Acme Editor");

        var items = Scanner(@"C:\Program Files\Acme Editor\Rig").Scan(Removed(), Array.Empty<InstalledProgram>());

        Assert.Empty(items);
    }

    [Fact]
    public void A_folder_used_by_another_installed_program_is_downgraded_to_low()
    {
        _fs.AddDir(@"C:\Program Files", "Acme Editor");

        var items = Scan(null, Other("Acme Plugin Pack", @"C:\Program Files\Acme Editor\Plugins"));

        var item = Assert.Single(items);
        Assert.Equal(Confidence.Low, item.Confidence);
        Assert.Contains("Acme Plugin Pack", item.Reason);
    }

    [Fact]
    public void Another_program_in_a_different_folder_does_not_downgrade_anything()
    {
        _fs.AddDir(@"C:\Program Files", "Acme Editor");

        var items = Scan(null, Other("Beta", @"C:\Program Files\Beta"));

        Assert.Equal(Confidence.High, Assert.Single(items).Confidence);
    }

    // ---- registry ----

    [Fact]
    public void Finds_registry_keys_in_all_three_software_roots()
    {
        _reg.Add(@"HKCU\Software", "AcmeEditor");
        _reg.Add(@"HKLM\SOFTWARE", "Acme Editor");
        _reg.Add(@"HKLM\SOFTWARE\WOW6432Node", "Acme Editor");

        var items = Scan();

        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(LeftoverKind.RegistryKey, i.Kind));
        Assert.Contains(items, i => i.Path == @"HKCU\Software\AcmeEditor");
        Assert.Contains(items, i => i.Path == @"HKLM\SOFTWARE\Acme Editor");
        Assert.Contains(items, i => i.Path == @"HKLM\SOFTWARE\WOW6432Node\Acme Editor");
    }

    [Fact]
    public void Registry_publisher_key_is_low_and_product_subkey_is_listed()
    {
        _reg.Add(@"HKCU\Software", "Acme");
        _reg.Add(@"HKCU\Software\Acme", "Acme Editor");
        _reg.Add(@"HKCU\Software\Acme", "Other Thing");

        var items = Scan();

        Assert.Equal(2, items.Count);
        Assert.Equal(Confidence.Low, Assert.Single(items, i => i.Path == @"HKCU\Software\Acme").Confidence);
        Assert.Equal(Confidence.High, Assert.Single(items, i => i.Path == @"HKCU\Software\Acme\Acme Editor").Confidence);
    }

    [Fact]
    public void Windows_core_registry_keys_are_never_listed()
    {
        _reg.Add(@"HKLM\SOFTWARE", "Microsoft");
        _reg.Add(@"HKLM\SOFTWARE\Microsoft", "Windows");
        _reg.Add(@"HKLM\SOFTWARE\Microsoft", "Windows NT");
        _reg.Add(@"HKLM\SOFTWARE", "Classes");

        var items = Scan(Removed("Windows Classes Helper", publisher: "Microsoft Corporation"));

        Assert.Empty(items);
    }

    // ---- result shape ----

    [Fact]
    public void Results_are_sorted_with_the_most_certain_first()
    {
        _fs.AddDir(@"C:\Program Files", "Acme");
        _fs.AddDir(@"C:\Program Files", "AcmeEditorLogs");
        _fs.AddDir(@"C:\ProgramData", "Acme Editor");

        var items = Scan();

        Assert.Equal(new[] { Confidence.High, Confidence.Medium, Confidence.Low }, items.Select(i => i.Confidence));
    }

    [Fact]
    public void The_same_path_found_twice_is_listed_once_with_the_best_confidence()
    {
        _fs.AddDir(@"C:\Program Files", "Acme");
        _fs.AddDir(@"C:\Program Files\Acme", "Acme Editor");
        _fs.AddDir(@"C:\Program Files", "Acme Editor"); // a different path, same name

        var items = Scan();

        Assert.Equal(items.Count, items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Nothing_found_gives_an_empty_list_and_a_log_line()
    {
        _fs.AddDir(@"C:\Program Files", "Unrelated");

        Assert.Empty(Scan());
        Assert.Contains(_log.Lines, l => l.Contains("Leftover scan") && l.Contains("0"));
    }

    [Fact]
    public void Scan_logs_counts_per_confidence()
    {
        _fs.AddDir(@"C:\Program Files", "Acme Editor");
        _fs.AddDir(@"C:\Program Files", "Acme");

        Scan();

        Assert.Contains(_log.Lines, l => l.Contains("1 high") && l.Contains("1 low"));
    }
}
