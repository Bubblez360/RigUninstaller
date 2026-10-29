using System.Text.RegularExpressions;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum DefenderStatus
{
    Clean,
    ThreatsFound,
    Failed,

    /// <summary>No folder to scan is known for the program.</summary>
    NothingToScan,
}

public sealed record DefenderResult(DefenderStatus Status, string Message);

public interface IDefenderScanner
{
    /// <summary>False when Microsoft Defender's command line tool is not installed.</summary>
    bool IsAvailable { get; }

    /// <summary>Runs a custom scan on the program's install folder.</summary>
    Task<DefenderResult> ScanProgramAsync(InstalledProgram program, CancellationToken cancellationToken);
}

/// <summary>Scans a program's folder with Microsoft Defender's MpCmdRun.exe (-Scan -ScanType 3 -File).</summary>
public sealed class DefenderScanner : IDefenderScanner
{
    private static readonly Regex Threats = new(@"found\s+(\d+)\s+threats?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IConsoleRunner _console;
    private readonly SafetyGuard _guard;
    private readonly IFileSystemProbe _fs;
    private readonly IActionLog _log;
    private readonly string _toolPath;

    public DefenderScanner(IConsoleRunner console, SafetyGuard guard, IFileSystemProbe fs, IActionLog log, string toolPath)
    {
        _console = console;
        _guard = guard;
        _fs = fs;
        _log = log;
        _toolPath = toolPath;
    }

    /// <summary>The path from the Windows documentation, then the newest copy in Defender's platform folder.</summary>
    public static string FindTool()
    {
        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        if (File.Exists(classic))
        {
            return classic;
        }

        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
        try
        {
            var newest = Directory.EnumerateDirectories(platform)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);
            return newest ?? classic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return classic;
        }
    }

    public bool IsAvailable => File.Exists(_toolPath);

    public async Task<DefenderResult> ScanProgramAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        var folder = ProgramFolders.For(program, _guard).FirstOrDefault(_fs.Exists);
        if (folder is null)
        {
            _log.Info($"Defender scan skipped for {program.Name}: no install folder known.");
            return new DefenderResult(DefenderStatus.NothingToScan, "No install folder is known for this program, so there is nothing to scan.");
        }

        _log.Info($"Microsoft Defender scan of {folder}");
        var result = await _console.RunAsync(_toolPath, new[] { "-Scan", "-ScanType", "3", "-File", folder }, cancellationToken);

        var found = Threats.Match(result.Output);
        DefenderResult outcome;
        if (found.Success && int.Parse(found.Groups[1].Value) > 0)
        {
            outcome = new DefenderResult(DefenderStatus.ThreatsFound, $"Microsoft Defender reported {found.Groups[1].Value} threat(s) in {folder}.\n\n{Tail(result.Output)}");
        }
        else if (result.ExitCode == 0)
        {
            outcome = new DefenderResult(DefenderStatus.Clean, $"Microsoft Defender found no threats in {folder}.");
        }
        else
        {
            outcome = new DefenderResult(DefenderStatus.Failed, $"The scan did not finish (exit code {result.ExitCode}). {Tail(result.Output)}".Trim());
        }

        _log.Info($"Defender result for {program.Name}: {outcome.Status}");
        return outcome;
    }

    private static string Tail(string output) =>
        string.Join("\n", output.Replace("\r", string.Empty).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(4));
}
