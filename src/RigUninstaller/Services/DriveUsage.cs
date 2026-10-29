namespace RigUninstaller.Services;

/// <summary>One drive with its size and free space.</summary>
public sealed record DriveEntry(string RootPath, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>Share of the drive that is used, 0 to 1.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / TotalBytes, 0, 1);

    /// <summary>"F:" for "F:\".</summary>
    public string Name => RootPath.TrimEnd('\\');
}

public interface IDriveSource
{
    IReadOnlyList<DriveEntry> GetDrives();
}

/// <summary>Every internal and removable drive that is ready (C:, F:, a USB stick...).</summary>
public sealed class DriveSource : IDriveSource
{
    private const long MinimumBytes = 1024L * 1024 * 1024;

    public IReadOnlyList<DriveEntry> GetDrives()
    {
        var result = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Tiny partitions (the 100 MB "System Reserved" one) are Windows' own and not worth a card.
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady || drive.TotalSize < MinimumBytes)
                {
                    continue;
                }

                result.Add(new DriveEntry(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears or cannot be read while we look is simply left out.
            }
        }

        return result;
    }
}

/// <summary>A folder or file inside the folder being looked at, with the space it uses.</summary>
public sealed record FolderEntry(string Name, string FullPath, long Bytes, bool IsFolder);

public interface IFolderBreakdown
{
    /// <summary>
    /// Measures what is directly inside a folder: each sub folder (everything below it) and each file.
    /// Entries are reported as they finish; the full list comes back biggest first. Read-only.
    /// </summary>
    Task<IReadOnlyList<FolderEntry>> ScanAsync(string folder, IProgress<FolderEntry>? progress, CancellationToken cancellationToken);
}

/// <summary>Measures folder sizes in parallel. Junctions and links are not followed, so nothing is counted twice.</summary>
public sealed class FolderBreakdown : IFolderBreakdown
{
    private const int MaxEntries = 400;
    private const int Parallelism = 4;

    public Task<IReadOnlyList<FolderEntry>> ScanAsync(string folder, IProgress<FolderEntry>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(folder, progress, cancellationToken), cancellationToken);

    private static IReadOnlyList<FolderEntry> Scan(string folder, IProgress<FolderEntry>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<FolderEntry>();
        var gate = new object();
        var directories = new List<DirectoryInfo>();

        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (info is FileInfo file)
                {
                    var entry = new FolderEntry(file.Name, file.FullName, file.Length, false);
                    entries.Add(entry);
                    progress?.Report(entry);
                }
                else if (info is DirectoryInfo directory)
                {
                    directories.Add(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        Parallel.ForEach(
            directories,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            directory =>
            {
                var entry = new FolderEntry(directory.Name, directory.FullName, SizeOf(directory, cancellationToken), true);
                lock (gate)
                {
                    entries.Add(entry);
                }

                progress?.Report(entry);
            });

        return entries.OrderByDescending(e => e.Bytes).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxEntries).ToList();
    }

    private static long SizeOf(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var file in directory.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was counted before the problem still counts.
        }

        return total;
    }
}
