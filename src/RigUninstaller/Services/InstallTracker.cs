using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>What a snapshot saw: folders, files and registry keys at one moment.</summary>
public sealed class InstallSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;

    public HashSet<string> Directories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the walk stopped early because it hit the size limit.</summary>
    public bool Truncated { get; init; }
}

public sealed record TraceEntry(LeftoverKind Kind, string Path);

/// <summary>What one tracked install added, linked to the program that appeared in the list.</summary>
public sealed record InstallTrace(string ProgramName, string ProgramKey, DateTime Created, IReadOnlyList<TraceEntry> Entries);

public interface ISnapshotSource
{
    Task<InstallSnapshot> TakeAsync(IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Works out what was added between two snapshots, keeping only the outermost items.</summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<TraceEntry> Compute(InstallSnapshot before, InstallSnapshot after)
    {
        var addedDirs = after.Directories.Where(d => !before.Directories.Contains(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedFiles = after.Files.Where(f => !before.Files.Contains(f)).ToList();
        var addedKeys = after.Keys.Where(k => !before.Keys.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<TraceEntry>();
        entries.AddRange(addedDirs.Where(d => !HasAddedAncestor(d, addedDirs, '\\')).Select(d => new TraceEntry(LeftoverKind.Folder, d)));
        entries.AddRange(addedFiles.Where(f => !HasAddedAncestor(f, addedDirs, '\\')).Select(f => new TraceEntry(LeftoverKind.File, f)));
        entries.AddRange(addedKeys.Where(k => !HasAddedAncestor(k, addedKeys, '\\')).Select(k => new TraceEntry(LeftoverKind.RegistryKey, k)));
        return entries.OrderBy(e => e.Kind).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // An item inside an added folder or key goes with it, so it does not need a line of its own.
    private static bool HasAddedAncestor(string path, HashSet<string> added, char separator)
    {
        var index = path.LastIndexOf(separator);
        while (index > 0)
        {
            path = path[..index];
            if (added.Contains(path))
            {
                return true;
            }

            index = path.LastIndexOf(separator);
        }

        return false;
    }
}

public interface ITraceStore
{
    void Save(InstallTrace trace);

    /// <summary>The traced items that still exist and are safe to offer, as High-confidence leftovers.</summary>
    IReadOnlyList<LeftoverItem> LoadItems(InstalledProgram program);
}

/// <summary>Trace logs as JSON files in Data\Traces, one per program.</summary>
public sealed class TraceStore : ITraceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _folder;
    private readonly IFileSystemProbe _fs;
    private readonly IRegistryProbe _registry;
    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;

    public TraceStore(string folder, IFileSystemProbe fs, IRegistryProbe registry, SafetyGuard guard, IActionLog log)
    {
        _folder = folder;
        _fs = fs;
        _registry = registry;
        _guard = guard;
        _log = log;
    }

    public void Save(InstallTrace trace)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FileFor(trace.ProgramKey), JsonSerializer.Serialize(trace, Options));
        _log.Info($"Install trace saved for {trace.ProgramName}: {trace.Entries.Count} items.");
    }

    public IReadOnlyList<LeftoverItem> LoadItems(InstalledProgram program)
    {
        var file = FileFor(program.RegistryKeyPath);
        if (!File.Exists(file))
        {
            return Array.Empty<LeftoverItem>();
        }

        try
        {
            var trace = JsonSerializer.Deserialize<InstallTrace>(File.ReadAllText(file), Options);
            return trace is null
                ? Array.Empty<LeftoverItem>()
                : trace.Entries
                    .Where(StillThereAndSafe)
                    .Select(e => new LeftoverItem(e.Kind, e.Path, Confidence.High, "Created while you tracked this install."))
                    .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn($"Could not read the install trace of {program.Name}: {ex.Message}");
            return Array.Empty<LeftoverItem>();
        }
    }

    private bool StillThereAndSafe(TraceEntry entry)
    {
        if (entry.Kind == LeftoverKind.RegistryKey)
        {
            if (!_guard.CheckRegistryKey(entry.Path).IsAllowed)
            {
                return false;
            }

            var cut = entry.Path.LastIndexOf('\\');
            return _registry.GetSubKeyNames(entry.Path[..cut]).Contains(entry.Path[(cut + 1)..], StringComparer.OrdinalIgnoreCase);
        }

        return _guard.CheckPath(entry.Path).IsAllowed && !_fs.IsReparsePoint(entry.Path) && _fs.Exists(entry.Path);
    }

    private string FileFor(string programKey)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(programKey.ToUpperInvariant())))[..16];
        return Path.Combine(_folder, hash + ".json");
    }
}

public sealed record TrackResult(IReadOnlyList<InstalledProgram> Linked, int EntryCount, string Message);

public interface IInstallTracker
{
    bool IsTracking { get; }

    /// <summary>Takes the first snapshot. Call before installing.</summary>
    Task BeginAsync(IReadOnlyList<InstalledProgram> programsBefore, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Takes the second snapshot, saves what changed and links it to the program(s) that appeared.</summary>
    Task<TrackResult> FinishAsync(IReadOnlyList<InstalledProgram> programsNow, IProgress<string>? progress, CancellationToken cancellationToken);

    void Cancel();
}

public sealed class InstallTracker : IInstallTracker
{
    private readonly ISnapshotSource _source;
    private readonly ITraceStore _store;
    private readonly IActionLog _log;
    private InstallSnapshot? _before;
    private HashSet<string> _programKeysBefore = new(StringComparer.OrdinalIgnoreCase);

    public InstallTracker(ISnapshotSource source, ITraceStore store, IActionLog log)
    {
        _source = source;
        _store = store;
        _log = log;
    }

    public bool IsTracking => _before is not null;

    public async Task BeginAsync(IReadOnlyList<InstalledProgram> programsBefore, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _log.Info("Install tracking: taking the first snapshot.");
        var snapshot = await _source.TakeAsync(progress, cancellationToken);
        _programKeysBefore = programsBefore.Select(p => p.RegistryKeyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _before = snapshot;
        _log.Info($"First snapshot: {snapshot.Directories.Count} folders, {snapshot.Files.Count} files, {snapshot.Keys.Count} registry keys{(snapshot.Truncated ? " (limit reached)" : string.Empty)}.");
    }

    public async Task<TrackResult> FinishAsync(IReadOnlyList<InstalledProgram> programsNow, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var before = _before ?? throw new InvalidOperationException("Tracking was not started.");

        _log.Info("Install tracking: taking the second snapshot.");
        var after = await _source.TakeAsync(progress, cancellationToken);
        var entries = SnapshotDiff.Compute(before, after);

        var linked = programsNow.Where(p => !_programKeysBefore.Contains(p.RegistryKeyPath)).ToList();
        _before = null;

        if (linked.Count == 0)
        {
            _log.Warn($"Install tracking finished with {entries.Count} changes but no new program appeared in the list.");
            return new TrackResult(linked, entries.Count, "No new program appeared in the list, so nothing was saved. Finish the installation first, then try again.");
        }

        foreach (var program in linked)
        {
            _store.Save(new InstallTrace(program.Name, program.RegistryKeyPath, DateTime.Now, entries));
        }

        var names = string.Join(", ", linked.Select(p => p.Name));
        _log.Info($"Install trace linked to {names}: {entries.Count} items.");
        return new TrackResult(linked, entries.Count, $"Saved a trace of {entries.Count} items for {names}. Uninstalling it later will use this for exact leftover removal.");
    }

    public void Cancel()
    {
        _before = null;
        _log.Info("Install tracking cancelled.");
    }
}

/// <summary>Walks the usual install places and the Software registry keys. Skips Windows' own folders and caches.</summary>
public sealed class SnapshotSource : ISnapshotSource
{
    private const int MaxDepth = 6;
    private const int MaxRegistryDepth = 3;
    private const int MaxEntries = 700_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Cache", "Code Cache", "GPUCache", "Crashpad", "CrashDumps", "Logs", "INetCache",
    };

    private readonly SafetyGuardRoots _roots;
    private readonly SafetyGuard _guard;

    public SnapshotSource(SafetyGuardRoots roots, SafetyGuard guard)
    {
        _roots = roots;
        _guard = guard;
    }

    public Task<InstallSnapshot> TakeAsync(IProgress<string>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Take(progress, cancellationToken), cancellationToken);

    private InstallSnapshot Take(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The Start Menu sits inside the protected Windows folders, so its two Programs folders are walked on their own.
        var roots = new[]
        {
            _roots.ProgramFiles, _roots.ProgramFilesX86, _roots.AppDataRoaming, _roots.AppDataLocal, _roots.ProgramData,
            Path.Combine(_roots.ProgramData, @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(_roots.AppDataRoaming, @"Microsoft\Windows\Start Menu\Programs"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            progress?.Report($"Looking at {root}");
            Walk(root, 0, dirs, files, cancellationToken);
        }

        progress?.Report("Looking at the registry");
        foreach (var (hive, label, path) in new[]
                 {
                     (RegistryHive.CurrentUser, "HKCU", "Software"),
                     (RegistryHive.LocalMachine, "HKLM", "SOFTWARE"),
                     (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node"),
                 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key is not null)
            {
                WalkKey(key, $@"{label}\{path}", 0, keys, cancellationToken);
            }
        }

        return new InstallSnapshot { Directories = dirs, Files = files, Keys = keys, Truncated = dirs.Count + files.Count + keys.Count >= MaxEntries };
    }

    private void Walk(string folder, int depth, HashSet<string> dirs, HashSet<string> files, CancellationToken cancellationToken)
    {
        if (depth >= MaxDepth || dirs.Count + files.Count >= MaxEntries)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || !_guard.CheckPath(entry.FullName).IsAllowed)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    if (SkippedFolders.Contains(directory.Name))
                    {
                        continue;
                    }

                    dirs.Add(directory.FullName);
                    Walk(directory.FullName, depth + 1, dirs, files, cancellationToken);
                }
                else
                {
                    files.Add(entry.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we may not read cannot have been filled by an installer we run, so skip it.
        }
    }

    private static void WalkKey(RegistryKey key, string path, int depth, HashSet<string> keys, CancellationToken cancellationToken)
    {
        if (depth >= MaxRegistryDepth || keys.Count >= MaxEntries)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var name in key.GetSubKeyNames())
            {
                var childPath = $@"{path}\{name}";
                keys.Add(childPath);

                // Windows' own and class registrations are far too big and are never offered anyway.
                var isCoreRoot = depth == 0 && name is "Classes" or "Policies";
                var isMicrosoftChild = path.EndsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase);
                if (isCoreRoot || isMicrosoftChild || name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && depth == 0)
                {
                    continue;
                }

                using var child = key.OpenSubKey(name);
                if (child is not null)
                {
                    WalkKey(child, childPath, depth + 1, keys, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Keys we may not read are skipped.
        }
    }
}
