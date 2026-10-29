using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class FileActionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zap-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Info_creates_folder_and_writes_line_with_level()
    {
        var log = new FileActionLog(_dir);

        log.Info("scan started");

        var text = File.ReadAllText(log.LogFilePath);
        Assert.Contains("INFO", text);
        Assert.Contains("scan started", text);
    }

    [Fact]
    public void Error_includes_exception_message()
    {
        var log = new FileActionLog(_dir);

        log.Error("delete failed", new IOException("file locked"));

        var text = File.ReadAllText(log.LogFilePath);
        Assert.Contains("ERROR", text);
        Assert.Contains("file locked", text);
    }

    [Fact]
    public void ReadTail_returns_only_last_lines()
    {
        var log = new FileActionLog(_dir);
        for (var i = 1; i <= 10; i++) log.Info("line " + i);

        var tail = log.ReadTail(3);

        Assert.DoesNotContain("line 7", tail);
        Assert.Contains("line 8", tail);
        Assert.Contains("line 10", tail);
    }

    [Fact]
    public void ReadTail_returns_empty_text_when_no_log_yet()
    {
        var log = new FileActionLog(_dir);

        Assert.Equal(string.Empty, log.ReadTail(10));
    }
}
