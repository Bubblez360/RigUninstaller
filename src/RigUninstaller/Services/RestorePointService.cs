using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

public sealed record ScriptResult(int ExitCode, string Output);

/// <summary>Runs a Windows PowerShell script. Behind an interface so no test ever touches System Restore.</summary>
public interface IPowerShellRunner
{
    Task<ScriptResult> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    public async Task<ScriptResult> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        // Windows PowerShell 5.1: it is the one that has the Checkpoint-Computer command.
        var powerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ScriptResult(-1, "Windows PowerShell could not be started.");
            }

            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                return new ScriptResult(process.ExitCode, (await output).Trim());
            }
            catch (OperationCanceledException)
            {
                // Only our own helper script is stopped here, never an uninstaller.
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Win32Exception ex)
        {
            return new ScriptResult(-1, $"Windows PowerShell could not be started: {ex.Message}");
        }
    }
}

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken);
}

/// <summary>Creates a System Restore point through Windows' own Checkpoint-Computer command.</summary>
public sealed class RestorePointService : IRestorePointService
{
    private const string DescriptionVariable = "RIG_RESTORE_DESCRIPTION";
    private const int MaxDescriptionLength = 200;

    // The description is passed as an environment variable and never pasted into the script text,
    // so a program name from the registry cannot inject commands.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            Checkpoint-Computer -Description $env:RIG_RESTORE_DESCRIPTION -RestorePointType 'APPLICATION_UNINSTALL' -WarningAction Stop
            Write-Output 'OK'
        } catch {
            Write-Output ('FAIL: ' + $_.Exception.Message)
            exit 2
        }
        """;

    private readonly IPowerShellRunner _shell;
    private readonly IActionLog _log;

    public RestorePointService(IPowerShellRunner shell, IActionLog log)
    {
        _shell = shell;
        _log = log;
    }

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
    {
        var clean = Sanitize(description);
        _log.Info($"Creating System Restore point: {clean}");

        var result = await _shell.RunAsync(
            Script,
            new Dictionary<string, string> { [DescriptionVariable] = clean },
            cancellationToken);

        if (result.ExitCode == 0 && result.Output.Contains("OK", StringComparison.Ordinal))
        {
            _log.Info("System Restore point created.");
            return new RestorePointResult(true, "Restore point created.");
        }

        var message = Explain(result);
        _log.Warn($"System Restore point not created: {message}");
        return new RestorePointResult(false, message);
    }

    private static string Sanitize(string description)
    {
        var singleLine = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= MaxDescriptionLength ? singleLine : singleLine[..MaxDescriptionLength];
    }

    private static string Explain(ScriptResult result)
    {
        if (result.Output.Contains("1440 minutes", StringComparison.OrdinalIgnoreCase)
            || result.Output.Contains("already been created", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows only allows one restore point every 24 hours, and one was already made today.";
        }

        var text = result.Output.StartsWith("FAIL:", StringComparison.Ordinal) ? result.Output[5..].Trim() : result.Output;
        return text.Length > 0
            ? text
            : $"Windows PowerShell stopped without a message (exit code {result.ExitCode}).";
    }
}
