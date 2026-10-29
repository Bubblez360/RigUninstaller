using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RigUninstaller.Services;

/// <summary>Thread-safe append-only text log stored next to the app.</summary>
public sealed class FileActionLog : IActionLog
{
    private const string FileName = "riguninstaller.log";

    private readonly object _gate = new();
    private readonly string _directory;

    public FileActionLog(string logDirectory)
    {
        _directory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        LogFilePath = Path.Combine(_directory, FileName);
    }

    public string LogFilePath { get; }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");

    public string ReadTail(int lineCount)
    {
        lock (_gate)
        {
            if (!File.Exists(LogFilePath))
            {
                return string.Empty;
            }

            // FileShare.ReadWrite so we can read while the logger is still appending.
            using var stream = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            var skip = Math.Max(0, lines.Count - Math.Max(0, lineCount));
            return string.Join(Environment.NewLine, lines.Skip(skip));
        }
    }

    private void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken log must never crash a cleanup in progress; surface it to the debugger instead.
            Debug.WriteLine($"[FileActionLog] could not write log: {ex.Message}");
        }
    }
}
