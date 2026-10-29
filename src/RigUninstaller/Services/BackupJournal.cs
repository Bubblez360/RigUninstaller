using System.Text.Json;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>A running list of everything the app backed up or moved aside, so it can be undone later.</summary>
public interface IBackupJournal
{
    void Add(BackupEntry entry);

    IReadOnlyList<BackupEntry> ReadAll();
}

/// <summary>One JSON object per line. A damaged line is skipped, never fatal.</summary>
public sealed class BackupJournal : IBackupJournal
{
    private readonly object _gate = new();
    private readonly string _file;

    public BackupJournal(string file)
    {
        _file = file;
    }

    public void Add(BackupEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<BackupEntry> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_file))
            {
                return Array.Empty<BackupEntry>();
            }

            var entries = new List<BackupEntry>();
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<BackupEntry>(line) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // One bad line must not hide the rest of the history.
                }
            }

            return entries;
        }
    }
}
