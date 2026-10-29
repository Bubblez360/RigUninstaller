namespace RigUninstaller.Services;

/// <summary>
/// One place a cache lives. The folder's CONTENTS go, never the folder itself. A "*" as a whole path part matches
/// any one folder (browser profiles). With <paramref name="Pattern"/> only matching files go, and with
/// <paramref name="Recursive"/> false the sub-folders are left alone.
/// </summary>
public sealed record CacheTarget(string Folder, string? Pattern = null, bool Recursive = true);

/// <param name="Id">Stable name used in the log and the tests.</param>
/// <param name="Note">What to know before cleaning it, or null.</param>
/// <param name="TickedByDefault">Ticked when the tab opens. Only the safe, plainly disposable ones are.</param>
public sealed record CacheCategory(
    string Id,
    string Name,
    string Description,
    string? Note,
    bool TickedByDefault,
    IReadOnlyList<CacheTarget> Targets);

/// <summary>The few folders everything else is worked out from. Tests pass their own.</summary>
public sealed record CacheRoots(
    string UserTemp,
    string LocalAppData,
    string RoamingAppData,
    string ProgramData,
    string WindowsDirectory,
    string UserProfile)
{
    public static CacheRoots FromEnvironment() => new(
        Path.GetTempPath().TrimEnd('\\'),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// The allowlist: every cache the app is able to clean. Anything not listed here can never be touched, however the
/// rest of the code is called. All of it is disposable data that Windows or the program makes again by itself.
/// </summary>
public static class CacheCatalog
{
    public static IReadOnlyList<CacheCategory> Build(CacheRoots roots)
    {
        var local = roots.LocalAppData;
        var roaming = roots.RoamingAppData;
        var windows = roots.WindowsDirectory;

        return new[]
        {
            new CacheCategory(
                "user-temp", "Your temporary files",
                "Scratch files that programs leave in your Temp folder.",
                null, true,
                Distinct(new CacheTarget(roots.UserTemp), new CacheTarget(Path.Combine(local, "Temp")))),

            new CacheCategory(
                "windows-temp", "Windows temporary files",
                "Temporary files Windows and installers leave in the Windows Temp folder.",
                null, true,
                new[] { new CacheTarget(Path.Combine(windows, "Temp")) }),

            new CacheCategory(
                "thumbnails", "Thumbnail and icon cache",
                "Small pictures Explorer saves so folders open faster. Windows makes them again.",
                "Explorer keeps some of them open, so a few are skipped until you restart it.", true,
                new[]
                {
                    new CacheTarget(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false),
                    new CacheTarget(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", Recursive: false),
                }),

            new CacheCategory(
                "crash-reports", "Crash dumps and error reports",
                "Memory dumps and reports Windows keeps after a program or the PC crashed.",
                "Only useful if you want to investigate a crash.", true,
                new[]
                {
                    new CacheTarget(Path.Combine(local, "CrashDumps")),
                    new CacheTarget(Path.Combine(local, "Microsoft", "Windows", "WER")),
                    new CacheTarget(Path.Combine(roots.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")),
                    new CacheTarget(Path.Combine(roots.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")),
                    new CacheTarget(Path.Combine(windows, "Minidump")),
                    new CacheTarget(windows, "MEMORY.DMP", Recursive: false),
                }),

            new CacheCategory(
                "shader-caches", "Graphics shader caches",
                "Compiled graphics code that games and the graphics driver save (DirectX, NVIDIA, AMD).",
                "Games rebuild it, so each game may stutter a little the first time you play it again.", true,
                new[]
                {
                    new CacheTarget(Path.Combine(local, "D3DSCache")),
                    new CacheTarget(Path.Combine(local, "NVIDIA", "DXCache")),
                    new CacheTarget(Path.Combine(local, "NVIDIA", "GLCache")),
                    new CacheTarget(Path.Combine(local, "AMD", "DxCache")),
                }),

            new CacheCategory(
                "browser-caches", "Browser caches",
                "Saved web pages and pictures in Chrome, Edge, Brave, Opera and Firefox.",
                "Close your browsers first for the full effect. Passwords, history and logins are not touched.", true,
                BrowserTargets(local, roaming)),

            new CacheCategory(
                "app-caches", "App caches (Discord, Steam web)",
                "Web content that Discord and the Steam window keep so they load faster.",
                "Close Discord and Steam first for the full effect.", true,
                new[]
                {
                    new CacheTarget(Path.Combine(roaming, "discord", "Cache")),
                    new CacheTarget(Path.Combine(roaming, "discord", "Code Cache")),
                    new CacheTarget(Path.Combine(roaming, "discord", "GPUCache")),
                    new CacheTarget(Path.Combine(local, "Steam", "htmlcache")),
                }),

            new CacheCategory(
                "prefetch", "Prefetch (app start-up cache)",
                "Windows' notes about which files a program loads at start, so it can start faster.",
                "Windows builds it again by itself, but programs can start a little slower the first time. "
                + "The \"Last used\" dates in this app are read from it too, so they become approximate.",
                false,
                new[] { new CacheTarget(Path.Combine(windows, "Prefetch")) }),
        };
    }

    private static IReadOnlyList<CacheTarget> Distinct(params CacheTarget[] targets) =>
        targets.DistinctBy(t => t.Folder, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The cache folders inside each browser profile ("*" is the profile: Default, Profile 1...).</summary>
    private static IReadOnlyList<CacheTarget> BrowserTargets(string local, string roaming)
    {
        var chromium = new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
        };

        var targets = new List<CacheTarget>();
        foreach (var userData in chromium)
        {
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                targets.Add(new CacheTarget(Path.Combine(userData, "*", cache)));
            }
        }

        foreach (var opera in new[] { Path.Combine(local, "Opera Software", "Opera GX Stable"), Path.Combine(roaming, "Opera Software", "Opera GX Stable") })
        {
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                targets.Add(new CacheTarget(Path.Combine(opera, cache)));
            }
        }

        targets.Add(new CacheTarget(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "cache2")));
        return targets;
    }
}
