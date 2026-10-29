using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>A folder that no installed program seems to own and that has not been touched for a long time.</summary>
public sealed record OrphanFolder(string Path, string Name, string Location, long Bytes, DateTime LastChanged, string Reason);

/// <param name="MinAgeDays">Folders changed more recently than this are never listed: a program may be using them.</param>
/// <param name="MinBytes">Smaller folders are not worth the user's attention.</param>
public sealed record OrphanScanOptions(int MinAgeDays = 45, long MinBytes = 512 * 1024);

public interface IOrphanFolderScanner
{
    /// <summary>Folders in AppData, ProgramData and Program Files that match no installed program. Reads only.</summary>
    Task<IReadOnlyList<OrphanFolder>> ScanAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken);
}

/// <summary>
/// Finds the folders programs leave behind when they were removed some other way (or long ago, before this app).
/// It can only guess, so it is careful: shared and system folders are never listed, anything with a name that
/// matches an installed program, its publisher or a running program is left out, recent folders are left out,
/// and the user decides on every single one.
/// </summary>
public sealed class OrphanFolderScanner : IOrphanFolderScanner
{
    // Shared by many programs or part of Windows: a missing match says nothing about these.
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "WindowsApps", "Packages", "Temp", "Programs", "Common Files", "Package Cache", "dotnet",
        "Application Data", "SoftwareDistribution", "USOShared", "USOPrivate", "Comms", "ConnectedDevicesPlatform", "D3DSCache",
        "CrashDumps", "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "Diagnostics", "History", "Temporary Internet Files",
        "Google", "Mozilla", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Apple", "Oracle", "Adobe", "Sun", "Packages",
        "ssh", "pip", "npm", "NuGet", "node-gyp", "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
        "Default", "Default User", "Public", "All Users", "Classic", "ModifiableWindowsApps", "Reference Assemblies", "WindowsPowerShell",
        "Internet Explorer", "Windows Defender", "Windows Mail", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "Uninstall Information", "InstallShield Installation Information", "WinSxS",
        // Generic names that say nothing about who owns them.
        "cache", "caches", "logs", "log", "tmp", "data", "config", "common", "shared", "downloads", "updates", "update",
        "installer", "installers", "backup", "backups", "crashes", "Crashpad", "crash reports", "plugins", "extensions",
    };

    private static readonly string[] SharedPrefixes = { "Microsoft", "Windows", "regid.", "{" };

    private readonly IReadOnlyList<(string Label, string Path)> _roots;
    private readonly SafetyGuard _guard;
    private readonly IFolderSizer _sizer;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<IEnumerable<string>> _runningNames;
    private readonly OrphanScanOptions _options;
    private readonly IActionLog _log;

    public OrphanFolderScanner(
        IReadOnlyList<(string Label, string Path)> roots,
        SafetyGuard guard,
        IFolderSizer sizer,
        Func<DateTime> utcNow,
        Func<IEnumerable<string>> runningNames,
        OrphanScanOptions options,
        IActionLog log)
    {
        _roots = roots;
        _guard = guard;
        _sizer = sizer;
        _utcNow = utcNow;
        _runningNames = runningNames;
        _options = options;
        _log = log;
    }

    /// <summary>The places programs keep their own folders.</summary>
    public static IReadOnlyList<(string Label, string Path)> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new (string, string)[]
        {
            ("AppData (Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("AppData (Local)", local),
            ("Local Programs", Path.Combine(local, "Programs")),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        };
    }

    public Task<IReadOnlyList<OrphanFolder>> ScanAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<OrphanFolder>>(() => Scan(programs, cancellationToken), cancellationToken);

    private IReadOnlyList<OrphanFolder> Scan(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken)
    {
        var known = KnownKeys(programs, _runningNames());
        var now = _utcNow();
        var found = new List<OrphanFolder>();

        foreach (var (label, root) in _roots)
        {
            foreach (var folder in SubFolders(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                if (IsShared(name) || IsKnown(name, known) || !_guard.CheckPath(folder).IsAllowed)
                {
                    continue;
                }

                var changed = NewestChange(folder);
                var age = (now - changed).TotalDays;
                if (age < _options.MinAgeDays)
                {
                    continue;
                }

                var bytes = _sizer.GetSize(folder, cancellationToken) ?? 0;
                if (bytes < _options.MinBytes)
                {
                    continue;
                }

                found.Add(new OrphanFolder(
                    folder, name, label, bytes, changed.ToLocalTime(),
                    $"No installed program is called \"{name}\", and nothing in it has changed for {(int)age} days."));
            }
        }

        _log.Info($"Old leftover scan: {found.Count} folder(s) with no matching program.");
        return found.OrderByDescending(f => f.Bytes).ToList();
    }

    private static IEnumerable<string> SubFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsShared(string name) =>
        name.StartsWith('.')
        || SharedNames.Contains(name)
        || SharedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest change inside the folder, looking two levels deep (enough to tell if anything is alive in it).</summary>
    private static DateTime NewestChange(string folder)
    {
        var newest = Directory.GetLastWriteTimeUtc(folder);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var seen = 0;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }

                if (++seen >= 2000)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: treat what we saw as the answer.
        }

        return newest;
    }

    // ---- matching a folder name against the installed programs ----

    /// <summary>Names that mean "this folder belongs to something that is installed or running".</summary>
    public static HashSet<string> KnownKeys(IEnumerable<InstalledProgram> programs, IEnumerable<string> runningNames)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? text)
        {
            var key = Normalize(text);
            if (key.Length >= 3)
            {
                keys.Add(key);
            }
        }

        foreach (var program in programs)
        {
            Add(program.Name);
            Add(program.Publisher);
            var words = program.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Add(words.FirstOrDefault());
            Add(string.Join(' ', words.Take(2)));

            // Programs often keep data under one word of their name: "Microsoft Visual Studio Code" uses "Code".
            foreach (var word in words.Where(w => w.Length >= 4))
            {
                Add(word);
            }

            var uninstaller = UninstallCommandParser.CreatePlans(program).Normal is { IsMsi: false } plan ? plan.FileName : null;
            foreach (var path in new[] { program.InstallLocation, DisplayIconParser.Parse(program.DisplayIcon)?.Path, uninstaller })
            {
                AddPathNames(path, Add);
            }
        }

        foreach (var running in runningNames)
        {
            Add(running);
        }

        return keys;
    }

    private static void AddPathNames(string? path, Action<string?> add)
    {
        var folder = ProgramFolders.Tidy(path);
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
        {
            return;
        }

        // A file path names the folder it sits in; a folder path names itself and the folder above it.
        var directory = Path.HasExtension(folder) ? Path.GetDirectoryName(folder) : folder;
        add(Path.GetFileName(directory));
        add(Path.GetFileName(Path.GetDirectoryName(directory)));
    }

    public static bool IsKnown(string folderName, HashSet<string> known)
    {
        var folder = Normalize(folderName);
        if (folder.Length < 3)
        {
            return true; // too short to judge: never list it
        }

        return known.Any(key => key == folder
                                || key.StartsWith(folder, StringComparison.Ordinal)
                                || (key.Length >= 4 && folder.StartsWith(key, StringComparison.Ordinal)));
    }

    /// <summary>Lower case letters and digits only, so "OBS-Studio", "obs studio" and "OBS Studio" are the same.</summary>
    public static string Normalize(string? text) =>
        new string((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
