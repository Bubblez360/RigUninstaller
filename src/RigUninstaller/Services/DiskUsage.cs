using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <param name="IsEstimated">True when the size was measured from the install folder because the program reports none.</param>
/// <param name="Drive">The drive the program is installed on, e.g. "C:". <see cref="DiskUsage.UnknownDrive"/> when it cannot be told.</param>
public sealed record DiskUsageEntry(string Name, long Bytes, bool IsEstimated, string Drive = DiskUsage.UnknownDrive);

public interface IFolderSizer
{
    /// <summary>Total size of the files in a folder, or null when it cannot be read.</summary>
    long? GetSize(string folder, CancellationToken cancellationToken);
}

public sealed class FolderSizer : IFolderSizer
{
    private const int MaxFiles = 200_000;

    public long? GetSize(string folder, CancellationToken cancellationToken)
    {
        try
        {
            long total = 0;
            var count = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
                if (++count >= MaxFiles)
                {
                    break;
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Ranks the programs by the space they use. Uses the size the program reports, or measures its folder.</summary>
public static class DiskUsage
{
    public const string UnknownDrive = "Unknown";
    public const string NetworkDrive = "Network";

    public static IReadOnlyList<DiskUsageEntry> Top(
        IEnumerable<InstalledProgram> programs,
        int count,
        IFolderSizer sizer,
        SafetyGuard guard,
        IFileSystemProbe fs,
        CancellationToken cancellationToken) =>
        All(programs, sizer, guard, fs, cancellationToken).Take(count).ToList();

    /// <summary>Every program with a known size, biggest first, each tagged with its drive.</summary>
    public static IReadOnlyList<DiskUsageEntry> All(
        IEnumerable<InstalledProgram> programs,
        IFolderSizer sizer,
        SafetyGuard guard,
        IFileSystemProbe fs,
        CancellationToken cancellationToken)
    {
        var entries = new List<DiskUsageEntry>();
        foreach (var program in programs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (program.SizeBytes is { } reported)
            {
                entries.Add(new DiskUsageEntry(program.Name, reported, false, DriveOf(program)));
                continue;
            }

            var folder = ProgramFolders.For(program, guard).FirstOrDefault(fs.Exists);
            if (folder is not null && sizer.GetSize(folder, cancellationToken) is { } measured && measured > 0)
            {
                entries.Add(new DiskUsageEntry(program.Name, measured, true, DriveOf(program)));
            }
        }

        return entries.OrderByDescending(e => e.Bytes).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"C:" for a program installed on C:. Looks at the install folder, then the icon file, then the uninstaller.</summary>
    public static string DriveOf(InstalledProgram program)
    {
        var plans = UninstallCommandParser.CreatePlans(program);
        var paths = new[]
        {
            program.InstallLocation,
            DisplayIconParser.Parse(program.DisplayIcon)?.Path,
            plans.Normal is { IsMsi: false } normal ? normal.FileName : null,
        };

        foreach (var path in paths)
        {
            var cleaned = ProgramFolders.Tidy(path);
            if (!Path.IsPathRooted(cleaned))
            {
                continue;
            }

            if (DriveFromRoot(Path.GetPathRoot(cleaned)) is { } drive)
            {
                return drive;
            }
        }

        return UnknownDrive;
    }

    private static string? DriveFromRoot(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        // "\\?\C:\" is a local drive written the long way; other "\\" roots are network shares.
        var local = root.StartsWith(@"\\?\", StringComparison.Ordinal) ? root[4..] : root;
        if (local.Length >= 2 && char.IsAsciiLetter(local[0]) && local[1] == ':')
        {
            return local[..2].ToUpperInvariant();
        }

        return root.StartsWith(@"\\", StringComparison.Ordinal) && !root.StartsWith(@"\\?\", StringComparison.Ordinal) ? NetworkDrive : null;
    }
}
