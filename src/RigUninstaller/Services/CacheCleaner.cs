namespace RigUninstaller.Services;

public sealed record CacheScanResult(string CategoryId, long Bytes, int Files);

/// <param name="Skipped">Files that could not be deleted, nearly always because a program has them open.</param>
public sealed record CacheCleanResult(string CategoryId, long BytesFreed, int Deleted, int Skipped);

public interface ICacheCleaner
{
    /// <summary>How much each category holds. Reads only.</summary>
    Task<IReadOnlyList<CacheScanResult>> ScanAsync(IReadOnlyList<CacheCategory> categories, CancellationToken cancellationToken);

    /// <summary>Deletes the contents of the given categories. Files in use are skipped, nothing else is touched.</summary>
    Task<IReadOnlyList<CacheCleanResult>> CleanAsync(IReadOnlyList<CacheCategory> categories, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The rules every cache folder must pass before anything in it is deleted. The catalog is the allowlist; this
/// is the second lock, so an odd environment (TEMP set to the user's profile, say) can never widen what is deleted.
/// </summary>
public sealed class CacheGuard
{
    private readonly CacheRoots _roots;
    private readonly HashSet<string> _forbidden;
    private readonly string[] _allowedBases;

    public CacheGuard(CacheRoots roots)
    {
        _roots = roots;
        _allowedBases = new[] { roots.UserTemp, roots.LocalAppData, roots.RoamingAppData, roots.ProgramData, roots.WindowsDirectory }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .ToArray();

        var windows = roots.WindowsDirectory;
        var profile = roots.UserProfile;
        _forbidden = new[]
        {
            profile, windows, Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64"), Path.Combine(windows, "WinSxS"),
            roots.ProgramData, roots.LocalAppData, roots.RoamingAppData,
            Path.GetDirectoryName(profile) ?? string.Empty,
            Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Program Files"),
            Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Program Files (x86)"),
            Path.Combine(profile, "Desktop"), Path.Combine(profile, "Documents"), Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Pictures"), Path.Combine(profile, "Music"), Path.Combine(profile, "Videos"), Path.Combine(profile, "OneDrive"),
        }.Where(p => p.Length > 0).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Why this target must not be touched, or null when it may be cleaned.</summary>
    public string? Reject(string folder, string? pattern, bool recursive)
    {
        var path = Normalize(folder);
        if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "not a local folder";
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        if (path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return "a drive root";
        }

        if (!_allowedBases.Any(b => IsInside(path, b)))
        {
            return "outside the places caches are kept";
        }

        // A few named files may sit directly in a protected folder (MEMORY.DMP in Windows); a whole-folder clean never may.
        var singleFilesOnly = !recursive && pattern is { Length: >= 8 };
        if (!singleFilesOnly && _forbidden.Contains(path))
        {
            return "a protected folder";
        }

        return IsReparsePoint(path) ? "a link to somewhere else" : null;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static bool IsInside(string path, string baseFolder) =>
        path.Equals(baseFolder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(baseFolder + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell, so leave it alone
        }
    }
}

/// <summary>Measures and empties the cache folders of the catalog. Never follows a link, never deletes a folder it was not given.</summary>
public sealed class CacheCleaner : ICacheCleaner
{
    private readonly CacheGuard _guard;
    private readonly IActionLog _log;

    public CacheCleaner(CacheGuard guard, IActionLog log)
    {
        _guard = guard;
        _log = log;
    }

    public Task<IReadOnlyList<CacheScanResult>> ScanAsync(IReadOnlyList<CacheCategory> categories, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<CacheScanResult>>(() =>
        {
            var results = new List<CacheScanResult>();
            foreach (var category in categories)
            {
                long bytes = 0;
                var files = 0;
                foreach (var file in FilesOf(category, cancellationToken))
                {
                    bytes += file.Length;
                    files++;
                }

                results.Add(new CacheScanResult(category.Id, bytes, files));
            }

            return results;
        }, cancellationToken);

    public Task<IReadOnlyList<CacheCleanResult>> CleanAsync(IReadOnlyList<CacheCategory> categories, IProgress<string>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<CacheCleanResult>>(() =>
        {
            var results = new List<CacheCleanResult>();
            foreach (var category in categories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Cleaning {category.Name}...");
                var result = Clean(category, cancellationToken);
                results.Add(result);
                _log.Info($"Cache cleanup \"{category.Name}\": {result.Deleted} file(s) deleted, {result.BytesFreed} bytes freed, {result.Skipped} skipped (in use or protected).");
            }

            return results;
        }, cancellationToken);

    private CacheCleanResult Clean(CacheCategory category, CancellationToken cancellationToken)
    {
        long freed = 0;
        var deleted = 0;
        var skipped = 0;

        foreach (var file in FilesOf(category, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var size = file.Length;
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }

                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skipped++; // open in another program, or not ours to delete: leave it
            }
        }

        foreach (var (folder, _, recursive) in ResolvedTargets(category))
        {
            if (recursive)
            {
                RemoveEmptyFolders(folder, cancellationToken);
            }
        }

        return new CacheCleanResult(category.Id, freed, deleted, skipped);
    }

    /// <summary>Every file the category covers: only inside folders that passed the guard, links not followed.</summary>
    private IEnumerable<FileInfo> FilesOf(CacheCategory category, CancellationToken cancellationToken)
    {
        foreach (var (folder, pattern, recursive) in ResolvedTargets(category))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // junctions and symlinks are never entered
            };

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles(pattern ?? "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
        }
    }

    private IEnumerable<(string Folder, string? Pattern, bool Recursive)> ResolvedTargets(CacheCategory category)
    {
        foreach (var target in category.Targets)
        {
            foreach (var folder in Expand(target.Folder))
            {
                if (_guard.Reject(folder, target.Pattern, target.Recursive) is { } reason)
                {
                    _log.Warn($"Cache cleanup skipped {folder}: {reason}.");
                    continue;
                }

                yield return (folder, target.Pattern, target.Recursive);
            }
        }
    }

    /// <summary>Turns a folder with "*" path parts into the real folders that exist.</summary>
    public static IEnumerable<string> Expand(string template)
    {
        var root = Path.GetPathRoot(template);
        if (string.IsNullOrEmpty(root))
        {
            return Array.Empty<string>();
        }

        IEnumerable<string> current = new[] { root };
        foreach (var part in template[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = part.Contains('*') ? current.SelectMany(parent => SubFolders(parent, part)).ToList()
                                         : current.Select(parent => Path.Combine(parent, part)).ToList();
        }

        return current.Where(Directory.Exists).ToList();
    }

    private static IEnumerable<string> SubFolders(string parent, string pattern)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Removes sub-folders that are now empty, deepest first. The folder that was given stays.</summary>
    private static void RemoveEmptyFolders(string folder, CancellationToken cancellationToken)
    {
        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderByDescending(d => d.Length)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sub in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(sub, recursive: false); // fails if anything is still inside, which is what we want
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not empty or in use: leave it.
            }
        }
    }
}
