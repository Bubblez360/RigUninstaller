using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class RestorePointServiceTests
{
    private sealed class FakePowerShell : IPowerShellRunner
    {
        public ScriptResult Result { get; set; } = new(0, "OK");

        public string? LastScript { get; private set; }

        public IReadOnlyDictionary<string, string>? LastEnvironment { get; private set; }

        public Task<ScriptResult> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            LastScript = script;
            LastEnvironment = environment;
            return Task.FromResult(Result);
        }
    }

    private readonly FakePowerShell _shell = new();
    private readonly FakeLog _log = new();

    private RestorePointService Service() => new(_shell, _log);

    [Fact]
    public async Task OK_output_means_a_restore_point_was_created()
    {
        var result = await Service().CreateAsync("RigUninstaller: uninstall Acme", CancellationToken.None);

        Assert.True(result.Created);
        Assert.Contains(_log.Lines, l => l.StartsWith("INFO") && l.Contains("restore point", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_description_travels_in_an_environment_variable_not_in_the_script()
    {
        await Service().CreateAsync("RigUninstaller: uninstall Acme'; Remove-Item C:\\ -Recurse #", CancellationToken.None);

        Assert.DoesNotContain("Acme", _shell.LastScript);
        Assert.Contains("Remove-Item", _shell.LastEnvironment!["RIG_RESTORE_DESCRIPTION"]);
    }

    [Fact]
    public async Task Description_is_single_line_and_at_most_200_characters()
    {
        await Service().CreateAsync("line one\r\nline two " + new string('x', 400), CancellationToken.None);

        var description = _shell.LastEnvironment!["RIG_RESTORE_DESCRIPTION"];
        Assert.DoesNotContain('\n', description);
        Assert.DoesNotContain('\r', description);
        Assert.True(description.Length <= 200);
    }

    [Fact]
    public async Task The_24_hour_limit_is_explained_in_plain_words()
    {
        _shell.Result = new ScriptResult(
            2,
            "FAIL: A new system restore point cannot be created because one has already been created within the past 1440 minutes.");

        var result = await Service().CreateAsync("d", CancellationToken.None);

        Assert.False(result.Created);
        Assert.Contains("24 hours", result.Message);
    }

    [Fact]
    public async Task Other_failures_keep_the_original_message()
    {
        _shell.Result = new ScriptResult(2, "FAIL: Something odd happened");

        var result = await Service().CreateAsync("d", CancellationToken.None);

        Assert.False(result.Created);
        Assert.Contains("Something odd happened", result.Message);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN"));
    }

    [Fact]
    public async Task A_crashed_script_without_output_is_reported_with_its_exit_code()
    {
        _shell.Result = new ScriptResult(5, "");

        var result = await Service().CreateAsync("d", CancellationToken.None);

        Assert.False(result.Created);
        Assert.Contains("5", result.Message);
    }
}
