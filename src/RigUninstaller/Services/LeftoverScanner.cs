using RigUninstaller.Models;

namespace RigUninstaller.Services;

public interface ILeftoverScanner
{
    /// <summary>
    /// Looks for files, folders, shortcuts and registry keys of a program that is no longer installed.
    /// <paramref name="otherPrograms"/> are the programs that stay: anything they use is rated Low.
    /// </summary>
    IReadOnlyList<LeftoverItem> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms);
}

/// <summary>
/// Searches the usual places programs leave things behind and rates each find by how closely its name
/// matches the program or its publisher. It only reads; nothing is deleted here. Everything the safety
/// guard forbids (Windows folders, core registry keys, drivers) is dropped before it can be listed.
/// </summary>
public sealed class LeftoverScanner : ILeftoverScanner
{
    private readonly IFileSystemProbe _fs;
    private readonly IRegistryProbe _registry;
    private readonly SafetyGuard _guard;
    private readonly IActionLog _log;
    private readonly string _ownFolder;
    private readonly string[] _folderRoots;
    private readonly string[] _startMenuRoots;
    private readonly string[] _registryRoots;

    public LeftoverScanner(
        IFileSystemProbe fs,
        IRegistryProbe registry,
        SafetyGuard guard,
        SafetyGuardRoots roots,
        string ownFolder,
        IActionLog log)
    {
        _fs = fs;
        _registry = registry;
        _guard = guard;
        _log = log;
        _ownFolder = TrimEnd(ownFolder);

        _folderRoots = new[]
        {
            roots.ProgramFiles,
            roots.ProgramFilesX86,
            roots.AppDataRoaming,
            roots.AppDataLocal,
            Path.Combine(roots.AppDataLocal, "Programs"),
            roots.ProgramData,
        }.Select(TrimEnd).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        _startMenuRoots = new[]
        {
            Path.Combine(roots.ProgramData, @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(roots.AppDataRoaming, @"Microsoft\Windows\Start Menu\Programs"),
        }.Select(TrimEnd).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        _registryRoots = new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" };
    }

    public IReadOnlyList<LeftoverItem> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> otherPrograms)
    {
        var names = ProgramNameMatcher.Describe(program);
        var run = new ScanRun(this, names, otherPrograms);

        foreach (var root in _folderRoots)
        {
            foreach (var directory in _fs.GetDirectories(root))
            {
                run.ConsiderPath(directory, LeftoverKind.Folder, descend: true);
            }
        }

        foreach (var root in _startMenuRoots)
        {
            foreach (var directory in _fs.GetDirectories(root))
            {
                run.ConsiderPath(directory, LeftoverKind.Folder, descend: true);
            }

            foreach (var file in _fs.GetFiles(root))
            {
                run.ConsiderPath(file, LeftoverKind.File, descend: false);
            }
        }

        foreach (var root in _registryRoots)
        {
            foreach (var name in _registry.GetSubKeyNames(root))
            {
                run.ConsiderKey($@"{root}\{name}", descend: true);
            }
        }

        var found = run.Items.ToList();
        var items = found
            .Where(item => !IsCoveredByAnother(item, found))
            .OrderByDescending(i => i.Confidence)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Info(
            $"Leftover scan for {program.Name}: {items.Count} items " +
            $"({items.Count(i => i.Confidence == Confidence.High)} high, " +
            $"{items.Count(i => i.Confidence == Confidence.Medium)} medium, " +
            $"{items.Count(i => i.Confidence == Confidence.Low)} low)");
        return items;
    }

    /// <summary>
    /// True when the item sits inside a listed folder or key that is at least as certain: removing the
    /// outer one removes this too, so listing both would only be noise (and a second, failing delete).
    /// </summary>
    private static bool IsCoveredByAnother(LeftoverItem item, IReadOnlyList<LeftoverItem> all) =>
        all.Any(other => other != item
                         && other.Kind != LeftoverKind.File
                         && (other.Kind == LeftoverKind.RegistryKey) == (item.Kind == LeftoverKind.RegistryKey)
                         && other.Confidence >= item.Confidence
                         && item.Path.StartsWith(other.Path + @"\", StringComparison.OrdinalIgnoreCase));

    private static string TrimEnd(string path) => path.Trim().TrimEnd('\\', '/');

    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>The state of one scan: what was found so far.</summary>
    private sealed class ScanRun
    {
        private readonly LeftoverScanner _scanner;
        private readonly ProgramNames _names;
        private readonly IReadOnlyList<InstalledProgram> _others;
        private readonly Dictionary<string, LeftoverItem> _found = new(StringComparer.OrdinalIgnoreCase);
        private readonly InstalledProgram? _sameNameProgram;

        public ScanRun(LeftoverScanner scanner, ProgramNames names, IReadOnlyList<InstalledProgram> others)
        {
            _scanner = scanner;
            _names = names;
            _others = others;

            // Another version of the same program (Python 3.11 and 3.14, two 7-Zip builds) shares these places.
            _sameNameProgram = others.FirstOrDefault(other => ProgramNameMatcher.Describe(other).CoreCompact == names.CoreCompact);
        }

        public IEnumerable<LeftoverItem> Items => _found.Values;

        public void ConsiderPath(string path, LeftoverKind kind, bool descend)
        {
            var name = kind == LeftoverKind.File ? Path.GetFileNameWithoutExtension(path) : LastSegment(path);
            var isPublisher = ProgramNameMatcher.IsPublisherName(name, _names);
            var rating = Rate(name, isPublisher);
            Add(kind, path, rating);

            // Look inside a vendor folder for the product's own folder. The product's own folder is not searched.
            if (isPublisher && descend && rating is not { Confidence: Confidence.High })
            {
                foreach (var child in _scanner._fs.GetDirectories(path))
                {
                    Add(LeftoverKind.Folder, child, ProgramNameMatcher.ScoreAsProduct(LastSegment(child), _names));
                }

                foreach (var child in _scanner._fs.GetFiles(path))
                {
                    Add(LeftoverKind.File, child, ProgramNameMatcher.ScoreAsProduct(Path.GetFileNameWithoutExtension(child), _names));
                }
            }
        }

        public void ConsiderKey(string keyPath, bool descend)
        {
            var name = LastSegment(keyPath);
            var isPublisher = ProgramNameMatcher.IsPublisherName(name, _names);
            var rating = Rate(name, isPublisher);
            Add(LeftoverKind.RegistryKey, keyPath, rating);

            if (isPublisher && descend && rating is not { Confidence: Confidence.High })
            {
                foreach (var child in _scanner._registry.GetSubKeyNames(keyPath))
                {
                    Add(LeftoverKind.RegistryKey, $@"{keyPath}\{child}", ProgramNameMatcher.ScoreAsProduct(child, _names));
                }
            }
        }

        /// <summary>
        /// A folder named like the publisher is a vendor folder that may hold their other programs,
        /// so it is Low, unless its name is literally the program's own name (Discord by "Discord Inc.").
        /// </summary>
        private (Confidence Confidence, string Reason)? Rate(string name, bool isPublisher)
        {
            var product = ProgramNameMatcher.ScoreAsProduct(name, _names);
            if (!isPublisher || product is { Confidence: Confidence.High })
            {
                return product;
            }

            return _names.PublisherIsShared
                ? null
                : (Confidence.Low, "Named after the publisher; may be shared with their other programs.");
        }

        private void Add(LeftoverKind kind, string path, (Confidence Confidence, string Reason)? rating)
        {
            if (rating is not { } score || !IsSafeToOffer(kind, path))
            {
                return;
            }

            var item = new LeftoverItem(kind, path, score.Confidence, score.Reason);
            if (item.Confidence == Confidence.High && _sameNameProgram is not null)
            {
                item = item with
                {
                    Confidence = Confidence.Medium,
                    Reason = $"{item.Reason} Another installed program, {_sameNameProgram.Name}, has the same name, so this may be shared.",
                };
            }

            if (kind != LeftoverKind.RegistryKey)
            {
                item = DowngradeIfInUse(item);
            }

            if (!_found.TryGetValue(path, out var existing) || item.Confidence > existing.Confidence)
            {
                _found[path] = item;
            }
        }

        private bool IsSafeToOffer(LeftoverKind kind, string path)
        {
            if (kind == LeftoverKind.RegistryKey)
            {
                return _scanner._guard.CheckRegistryKey(path).IsAllowed;
            }

            return _scanner._guard.CheckPath(path).IsAllowed
                   && !_scanner._fs.IsReparsePoint(path)
                   && !ContainsOwnFolder(path);
        }

        // Never offer the folder this app runs from (or one that contains it).
        private bool ContainsOwnFolder(string path)
        {
            var folder = TrimEnd(path);
            var own = _scanner._ownFolder;
            return own.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || own.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A folder that another installed program lives in (or under) is shared: rate it Low.</summary>
        private LeftoverItem DowngradeIfInUse(LeftoverItem item)
        {
            var folder = TrimEnd(item.Path);
            foreach (var other in _others)
            {
                if (string.IsNullOrWhiteSpace(other.InstallLocation))
                {
                    continue;
                }

                var location = TrimEnd(other.InstallLocation.Trim('"'));
                var overlaps = location.Equals(folder, StringComparison.OrdinalIgnoreCase)
                               || location.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)
                               || folder.StartsWith(location + @"\", StringComparison.OrdinalIgnoreCase);
                if (overlaps)
                {
                    return item with
                    {
                        Confidence = Confidence.Low,
                        Reason = $"{item.Reason} Also used by {other.Name}, which is still installed.",
                    };
                }
            }

            return item;
        }
    }
}
