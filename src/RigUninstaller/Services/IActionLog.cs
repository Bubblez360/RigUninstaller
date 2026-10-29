namespace RigUninstaller.Services;

/// <summary>Action log: every delete, registry change and uninstall gets a line here.</summary>
public interface IActionLog
{
    string LogFilePath { get; }

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    /// <summary>Returns the last <paramref name="lineCount"/> lines of the log (empty text if no log yet).</summary>
    string ReadTail(int lineCount);
}
