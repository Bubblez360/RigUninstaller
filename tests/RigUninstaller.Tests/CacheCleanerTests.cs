using System.Diagnostics;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>Works on a throw-away folder tree that stands in for the user's profile, ProgramData and Windows.</summary>
public abstract class CacheTestBase : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "RigCache_" + Guid.NewGuid().ToString("N"));
    internal readonly FakeLog Log = new();
    protected readonly CacheRoots Roots;

    protected CacheTestBase()
    {
        Roots = new CacheRoots(
            UserTemp: Path.Combine(Root, "Profile", "AppData", "Local", "Temp"),
            LocalAppData: Path.Combine(Root, "Profile", "AppData", "Local"),
            RoamingAppData: Path.Combine(Root, "Profile", "AppData", "Roaming"),
            ProgramData: Path.Combine(Root, "ProgramData"),
            WindowsDirectory: Path.Combine(Root, "Windows"),
            UserProfile: Path.Combine(Root, "Profile"));
        foreach (var folder in new[] { Roots.UserTemp, Roots.RoamingAppData, Roots.ProgramData, Roots.WindowsDirectory, Roots.UserProfile })
        {
            Directory.CreateDirectory(folder);
        }
    }

    public void Dispose()
    {
        // Junctions first, so deleting the tree can never reach through a link.
        foreach (var link in Directory.Exists(Root)
                     ? Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList()
                     : new List<string>())
        {
            Directory.Delete(link, recursive: false);
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    protected CacheCleaner CreateCleaner() => new(new CacheGuard(Roots), Log);

    protected string Write(string relative, int bytes = 100)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    protected CacheCategory Category(string id, params CacheTarget[] targets) => new(id, id, "test", null, true, targets);
}

public class CacheCatalogTests : CacheTestBase
{
    [Fact]
    public void Only_the_plainly_disposable_categories_are_ticked_and_prefetch_is_not()
    {
        var catalog = CacheCatalog.Build(Roots);

        Assert.False(catalog.Single(c => c.Id == "prefetch").TickedByDefault);
        Assert.All(catalog.Where(c => c.Id != "prefetch"), c => Assert.True(c.TickedByDefault));
        Assert.Equal(new[] { "user-temp", "windows-temp", "thumbnails", "crash-reports", "shader-caches", "browser-caches", "app-caches", "prefetch" }, catalog.Select(c => c.Id));
    }

    [Fact]
    public void Every_fixed_target_passes_the_guard_so_the_catalog_and_the_guard_agree()
    {
        var guard = new CacheGuard(Roots);

        foreach (var category in CacheCatalog.Build(Roots))
        {
            foreach (var target in category.Targets.Where(t => !t.Folder.Contains('*')))
            {
                Directory.CreateDirectory(target.Folder);
                Assert.Null(guard.Reject(target.Folder, target.Pattern, target.Recursive));
            }
        }
    }

    [Fact]
    public void Nothing_in_the_catalog_points_at_documents_downloads_or_other_personal_folders()
    {
        var personal = new[] { "Documents", "Downloads", "Desktop", "Pictures", "Music", "Videos", "OneDrive" };

        foreach (var target in CacheCatalog.Build(Roots).SelectMany(c => c.Targets))
        {
            Assert.DoesNotContain(personal, name => target.Folder.Split('\\').Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }
}

public class CacheGuardTests : CacheTestBase
{
    [Fact]
    public void A_normal_cache_folder_is_allowed()
    {
        Directory.CreateDirectory(Path.Combine(Roots.LocalAppData, "D3DSCache"));

        Assert.Null(new CacheGuard(Roots).Reject(Path.Combine(Roots.LocalAppData, "D3DSCache"), null, true));
    }

    [Theory]
    [InlineData("Profile")]                           // the user's whole profile (TEMP set to it by mistake)
    [InlineData("Profile\\Documents")]
    [InlineData("Profile\\Downloads")]
    [InlineData("Profile\\AppData\\Local")]            // the whole of Local
    [InlineData("Profile\\AppData\\Roaming")]
    [InlineData("ProgramData")]
    [InlineData("Windows")]
    [InlineData("Windows\\System32")]
    public void Protected_folders_are_never_cleaned_as_a_whole(string relative)
    {
        var folder = Path.Combine(Root, relative);
        Directory.CreateDirectory(folder);

        Assert.NotNull(new CacheGuard(Roots).Reject(folder, null, recursive: true));
    }

    [Fact]
    public void A_drive_root_and_a_network_path_are_refused()
    {
        var guard = new CacheGuard(Roots);

        Assert.NotNull(guard.Reject(Path.GetPathRoot(Root)!, null, true));
        Assert.NotNull(guard.Reject(@"\\server\share\Temp", null, true));
    }

    [Fact]
    public void A_folder_outside_the_places_caches_live_is_refused()
    {
        var elsewhere = Path.Combine(Root, "Games", "Cache");
        Directory.CreateDirectory(elsewhere);

        Assert.Equal("outside the places caches are kept", new CacheGuard(Roots).Reject(elsewhere, null, true));
    }

    [Fact]
    public void A_folder_that_is_a_link_is_refused()
    {
        var outside = Path.Combine(Root, "Outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(Roots.LocalAppData, "LinkedCache");
        MakeJunction(link, outside);

        Assert.Equal("a link to somewhere else", new CacheGuard(Roots).Reject(link, null, true));
    }

    [Fact]
    public void A_named_file_may_be_cleaned_in_the_windows_folder_but_the_folder_itself_may_not()
    {
        var guard = new CacheGuard(Roots);

        Assert.Null(guard.Reject(Roots.WindowsDirectory, "MEMORY.DMP", recursive: false));
        Assert.NotNull(guard.Reject(Roots.WindowsDirectory, null, recursive: true));
        Assert.NotNull(guard.Reject(Roots.WindowsDirectory, "*", recursive: false)); // a bare wildcard is not a named file
    }

    internal static void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "the test could not create a junction");
    }
}

public class CacheCleanerTests : CacheTestBase
{
    [Fact]
    public async Task Scan_counts_the_bytes_and_files_of_a_category_and_nothing_else()
    {
        Write(@"Profile\AppData\Local\Temp\a.tmp", 100);
        Write(@"Profile\AppData\Local\Temp\sub\b.tmp", 250);
        Write(@"Profile\AppData\Local\Other\c.dat", 999); // not part of the category

        var result = Assert.Single(await CreateCleaner().ScanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, CancellationToken.None));

        Assert.Equal(350, result.Bytes);
        Assert.Equal(2, result.Files);
    }

    [Fact]
    public async Task Scan_never_deletes_anything()
    {
        var file = Write(@"Profile\AppData\Local\Temp\a.tmp");

        await CreateCleaner().ScanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, CancellationToken.None);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Clean_deletes_the_files_including_read_only_ones_and_empty_sub_folders_but_keeps_the_folder()
    {
        var readOnly = Write(@"Profile\AppData\Local\Temp\ro.tmp");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Write(@"Profile\AppData\Local\Temp\deep\er\x.tmp", 500);

        var result = Assert.Single(await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, null, CancellationToken.None));

        Assert.Equal(2, result.Deleted);
        Assert.Equal(600, result.BytesFreed);
        Assert.Equal(0, result.Skipped);
        Assert.True(Directory.Exists(Roots.UserTemp));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Roots.UserTemp));
    }

    [Fact]
    public async Task A_file_that_is_open_in_another_program_is_skipped_and_counted_not_forced()
    {
        var open = Write(@"Profile\AppData\Local\Temp\open.tmp");
        var free = Write(@"Profile\AppData\Local\Temp\free.tmp");

        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Assert.Single(await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, null, CancellationToken.None));

            Assert.Equal(1, result.Deleted);
            Assert.Equal(1, result.Skipped);
        }

        Assert.True(File.Exists(open));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public async Task A_junction_inside_a_cache_is_not_followed_so_the_files_behind_it_survive()
    {
        var outside = Write(@"Precious\keep-me.txt");
        CacheGuardTests.MakeJunction(Path.Combine(Roots.UserTemp, "shortcut-to-precious"), Path.Combine(Root, "Precious"));
        Write(@"Profile\AppData\Local\Temp\junk.tmp");

        var result = Assert.Single(await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, null, CancellationToken.None));

        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_cache_folder_that_is_itself_a_link_is_skipped_with_a_warning()
    {
        var outside = Write(@"Precious\keep-me.txt");
        CacheGuardTests.MakeJunction(Path.Combine(Roots.LocalAppData, "D3DSCache"), Path.Combine(Root, "Precious"));

        var result = Assert.Single(await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Path.Combine(Roots.LocalAppData, "D3DSCache"))) }, null, CancellationToken.None));

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(outside));
        Assert.Contains(Log.Lines, l => l.StartsWith("WARN") && l.Contains("a link to somewhere else"));
    }

    [Fact]
    public async Task A_temp_folder_pointing_at_the_whole_profile_deletes_nothing()
    {
        var document = Write(@"Profile\Documents\thesis.docx");
        var roots = Roots with { UserTemp = Roots.UserProfile };
        var cleaner = new CacheCleaner(new CacheGuard(roots), Log);

        var result = Assert.Single(await cleaner.CleanAsync(new[] { Category("t", new CacheTarget(roots.UserTemp)) }, null, CancellationToken.None));

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(document));
        Assert.Contains(Log.Lines, l => l.StartsWith("WARN") && l.Contains("protected folder"));
    }

    [Fact]
    public async Task A_pattern_target_removes_only_matching_files_and_leaves_sub_folders_alone()
    {
        var explorer = Path.Combine(Roots.LocalAppData, "Microsoft", "Windows", "Explorer");
        var thumb = Write(@"Profile\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db");
        var other = Write(@"Profile\AppData\Local\Microsoft\Windows\Explorer\settings.dat");
        var nested = Write(@"Profile\AppData\Local\Microsoft\Windows\Explorer\sub\thumbcache_96.db");

        await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(explorer, "thumbcache_*.db", Recursive: false)) }, null, CancellationToken.None);

        Assert.False(File.Exists(thumb));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested)); // not recursive
    }

    [Fact]
    public async Task The_memory_dump_goes_but_the_rest_of_the_windows_folder_stays()
    {
        var dump = Write(@"Windows\MEMORY.DMP", 5000);
        var system = Write(@"Windows\notepad.exe", 700);
        var inSubfolder = Write(@"Windows\System32\MEMORY.DMP", 1);

        var result = Assert.Single(await CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Roots.WindowsDirectory, "MEMORY.DMP", Recursive: false)) }, null, CancellationToken.None));

        Assert.Equal(5000, result.BytesFreed);
        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(system));
        Assert.True(File.Exists(inSubfolder));
    }

    [Fact]
    public void A_star_path_part_matches_each_browser_profile_that_has_the_folder()
    {
        Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\Cache\f1");
        Write(@"Profile\AppData\Local\Google\Chrome\User Data\Profile 1\Cache\f2");
        Directory.CreateDirectory(Path.Combine(Roots.LocalAppData, "Google", "Chrome", "User Data", "Profile 2")); // no Cache inside
        Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\Cookies"); // not a cache

        var found = CacheCleaner.Expand(Path.Combine(Roots.LocalAppData, "Google", "Chrome", "User Data", "*", "Cache")).Select(p => Path.GetRelativePath(Roots.LocalAppData, p)).OrderBy(p => p).ToList();

        Assert.Equal(new[] { @"Google\Chrome\User Data\Default\Cache", @"Google\Chrome\User Data\Profile 1\Cache" }, found);
    }

    [Fact]
    public async Task Browser_cleaning_keeps_cookies_history_and_logins()
    {
        var cache = Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001");
        var cookies = Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\Network\Cookies");
        var history = Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\History");
        var logins = Write(@"Profile\AppData\Local\Google\Chrome\User Data\Default\Login Data");
        var browsers = CacheCatalog.Build(Roots).Single(c => c.Id == "browser-caches");

        await CreateCleaner().CleanAsync(new[] { browsers }, null, CancellationToken.None);

        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(history));
        Assert.True(File.Exists(logins));
    }

    [Fact]
    public async Task Cleaning_the_real_catalog_leaves_unrelated_files_in_the_profile_windows_and_programdata()
    {
        var bystanders = new[]
        {
            Write(@"Profile\Documents\letter.docx"), Write(@"Profile\Desktop\notes.txt"), Write(@"Profile\AppData\Roaming\App\settings.json"),
            Write(@"Profile\AppData\Local\App\data.db"), Write(@"ProgramData\App\license.dat"), Write(@"Windows\System32\kernel.dll"),
            Write(@"Windows\Fonts\arial.ttf"),
        };
        Write(@"Profile\AppData\Local\Temp\junk.tmp");
        Write(@"Windows\Temp\junk2.tmp");
        Write(@"Windows\Prefetch\APP.pf");

        await CreateCleaner().CleanAsync(CacheCatalog.Build(Roots), null, CancellationToken.None);

        Assert.All(bystanders, file => Assert.True(File.Exists(file), file));
    }

    [Fact]
    public async Task A_cancelled_scan_or_clean_stops_with_the_cancellation_exception()
    {
        Write(@"Profile\AppData\Local\Temp\a.tmp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCleaner().ScanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCleaner().CleanAsync(new[] { Category("t", new CacheTarget(Roots.UserTemp)) }, null, cts.Token));
    }

    [Fact]
    public async Task A_missing_cache_folder_is_simply_empty()
    {
        var result = Assert.Single(await CreateCleaner().ScanAsync(new[] { Category("t", new CacheTarget(Path.Combine(Roots.LocalAppData, "NotThere"))) }, CancellationToken.None));

        Assert.Equal(0, result.Bytes);
        Assert.Equal(0, result.Files);
    }
}

public class CacheCleanupViewModelTests
{
    private sealed class FakeCacheCleaner : ICacheCleaner
    {
        public Dictionary<string, (long Bytes, int Files)> Sizes { get; } = new();

        public List<string> Cleaned { get; } = new();

        public Func<IReadOnlyList<CacheCategory>, IReadOnlyList<CacheCleanResult>>? OnClean { get; set; }

        public Exception? ThrowOnClean { get; set; }

        public Task<IReadOnlyList<CacheScanResult>> ScanAsync(IReadOnlyList<CacheCategory> categories, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CacheScanResult>>(categories
                .Select(c => Sizes.TryGetValue(c.Id, out var s) ? new CacheScanResult(c.Id, s.Bytes, s.Files) : new CacheScanResult(c.Id, 0, 0))
                .ToList());

        public Task<IReadOnlyList<CacheCleanResult>> CleanAsync(IReadOnlyList<CacheCategory> categories, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (ThrowOnClean is not null)
            {
                throw ThrowOnClean;
            }

            Cleaned.AddRange(categories.Select(c => c.Id));
            IReadOnlyList<CacheCleanResult> results = OnClean?.Invoke(categories)
                ?? categories.Select(c => new CacheCleanResult(c.Id, Sizes[c.Id].Bytes, Sizes[c.Id].Files, 0)).ToList();
            foreach (var c in categories)
            {
                Sizes[c.Id] = (0, 0); // what was cleaned is gone
            }

            return Task.FromResult(results);
        }
    }

    private static readonly IReadOnlyList<CacheCategory> Categories = new[]
    {
        new CacheCategory("temp", "Temp files", "d", null, true, new[] { new CacheTarget(@"C:\x") }),
        new CacheCategory("dump", "Dumps", "d", "careful", true, new[] { new CacheTarget(@"C:\y") }),
        new CacheCategory("pre", "Prefetch", "d", null, false, new[] { new CacheTarget(@"C:\z") }),
    };

    private readonly FakeCacheCleaner _cleaner = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeLog _log = new();

    private CacheCleanupViewModel Create() => new(_cleaner, Categories, _dialogs, _log);

    private async Task<CacheCleanupViewModel> ScannedAsync()
    {
        _cleaner.Sizes["temp"] = (3L * 1024 * 1024 * 1024, 1000);
        _cleaner.Sizes["dump"] = (500L * 1024 * 1024, 4);
        _cleaner.Sizes["pre"] = (20L * 1024 * 1024, 300);
        var vm = Create();
        await vm.ScanAsync();
        return vm;
    }

    [Fact]
    public async Task Opening_the_tab_measures_everything_and_ticks_only_the_default_categories()
    {
        var vm = await ScannedAsync();

        Assert.Equal(new[] { "3 GB", "500 MB", "20 MB" }, vm.Rows.Select(r => r.SizeText));
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.IsChecked));
        Assert.Equal("3.49 GB selected", vm.SelectedText);
        Assert.Equal("Clean 3.49 GB", vm.CleanButtonText);
        Assert.True(vm.CleanCommand.CanExecute(null));
        Assert.Contains("found in total", vm.StatusText);
    }

    [Fact]
    public async Task Ticking_prefetch_changes_the_total_and_unticking_everything_disables_clean()
    {
        var vm = await ScannedAsync();

        vm.Rows[2].IsChecked = true;
        Assert.Equal("3.51 GB selected", vm.SelectedText);

        foreach (var row in vm.Rows)
        {
            row.IsChecked = false;
        }

        Assert.Equal("Nothing selected", vm.SelectedText);
        Assert.False(vm.CleanCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_empty_category_cannot_count_as_selected()
    {
        _cleaner.Sizes["temp"] = (0, 0);
        var vm = Create();
        await vm.ScanAsync();

        Assert.Equal("Empty", vm.Rows[0].SizeText);
        Assert.False(vm.Rows[0].CanTick);
        Assert.DoesNotContain("temp", vm.SelectedText);
    }

    [Fact]
    public async Task Declining_the_confirmation_deletes_nothing()
    {
        var vm = await ScannedAsync();
        _dialogs.ConfirmAnswer = false;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Empty(_cleaner.Cleaned);
        Assert.Contains("Delete 3.49 GB", Assert.Single(_dialogs.Confirmations));
    }

    [Fact]
    public async Task Confirming_cleans_only_the_ticked_categories_and_reports_what_was_freed()
    {
        var vm = await ScannedAsync();
        _dialogs.ConfirmAnswer = true;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "temp", "dump" }, _cleaner.Cleaned);
        Assert.Equal("Freed 3.49 GB (1,004 files).", vm.ResultText);
        Assert.True(vm.HasResult);
        Assert.Contains(_log.Lines, l => l.Contains("Cache cleanup started"));
        Assert.Equal("Empty", vm.Rows[0].SizeText); // measured again afterwards
    }

    [Fact]
    public async Task Files_that_were_in_use_are_mentioned_in_the_result()
    {
        var vm = await ScannedAsync();
        _cleaner.OnClean = cats => cats.Select(c => new CacheCleanResult(c.Id, 1024, 1, 7)).ToList();

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Contains("14 files were in use and left alone", vm.ResultText);
    }

    [Fact]
    public async Task A_failure_is_shown_not_thrown()
    {
        var vm = await ScannedAsync();
        _cleaner.ThrowOnClean = new IOException("disk gone");

        await vm.CleanCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains("disk gone", message.Message);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("Cache cleanup failed"));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_cancelled_clean_says_so_and_keeps_what_was_deleted()
    {
        var vm = await ScannedAsync();
        _cleaner.ThrowOnClean = new OperationCanceledException();

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Contains("Stopped", vm.ResultText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Activate_measures_once()
    {
        var vm = Create();

        vm.Activate();
        vm.Activate();

        Assert.True(vm.Rows.All(r => r.IsMeasured));
    }
}



