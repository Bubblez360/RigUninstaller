using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RigUninstaller.Models;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Services;

/// <summary>Builds CSV text for the program list. Cells that start like a spreadsheet formula get a leading quote.</summary>
public static class CsvExporter
{
    private static readonly string[] Header =
    {
        "Name", "Publisher", "Version", "Size (bytes)", "Install date", "Install location", "Signed by", "Last used", "Tags",
    };

    public static string Build(IEnumerable<ProgramRowViewModel> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", Header)).Append("\r\n");
        foreach (var row in rows)
        {
            var program = row.Program;
            var cells = new[]
            {
                Text(program.Name),
                Text(program.Publisher),
                Text(program.Version),
                program.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                program.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                Text(program.InstallLocation),
                Text(row.SignedByText),
                Text(row.LastUsedText),
                Text(row.Tags),
            };
            builder.Append(string.Join(",", cells)).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A cell like "=cmd|..." would run as a formula when opened in Excel: make it plain text.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Builds the "search this program online" address.</summary>
public static class SearchUrl
{
    public static Uri For(InstalledProgram program)
    {
        var query = $"{program.Name} {program.Publisher} what is this program".Replace("  ", " ").Trim();
        return new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
    }
}

/// <summary>Finds which installed program a running exe belongs to.</summary>
public static class ProgramLocator
{
    public static InstalledProgram? Find(string exePath, IReadOnlyList<InstalledProgram> programs, SafetyGuard guard)
    {
        InstalledProgram? best = null;
        var bestScore = -1;
        foreach (var program in programs)
        {
            var score = Score(exePath, program, guard);
            if (score > bestScore)
            {
                best = program;
                bestScore = score;
            }
        }

        return bestScore >= 0 ? best : FindByFolderName(exePath, programs);
    }

    // Folders that say nothing about which program an exe belongs to.
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "program files", "program files (x86)", "windows", "windowsapps", "users", "appdata", "local", "roaming", "programs",
        "system32", "syswow64", "common files", "bin", "app", "x64", "x86", "win64", "win32", "programdata", "data", "cef",
    };

    /// <summary>
    /// Fallback for programs that register another folder than the one they run from (the NVIDIA App registers its
    /// installer folder). A folder on the exe's path that is the program's name picks it, but only when exactly one
    /// program fits, so a shared vendor folder never picks a wrong one.
    /// </summary>
    private static InstalledProgram? FindByFolderName(string exePath, IReadOnlyList<InstalledProgram> programs)
    {
        var folders = (Path.GetDirectoryName(exePath) ?? string.Empty)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.Length >= 4 && !f.EndsWith(':') && !GenericFolders.Contains(f))
            .Reverse(); // the deepest folder is the most specific

        foreach (var folder in folders)
        {
            // A one-word folder ("Microsoft", "Google") is a vendor folder, so it must match the whole name.
            var allowPrefix = folder.Contains(' ');
            var fits = programs
                .Where(p => p.Name.Equals(folder, StringComparison.OrdinalIgnoreCase)
                            || (allowPrefix && p.Name.StartsWith(folder + " ", StringComparison.OrdinalIgnoreCase)))
                .Take(2)
                .ToList();
            if (fits.Count == 1)
            {
                return fits[0];
            }
        }

        return null;
    }

    // The exact icon file wins; otherwise the longest (most specific) matching folder wins.
    private static int Score(string exePath, InstalledProgram program, SafetyGuard guard)
    {
        if (DisplayIconParser.Parse(program.DisplayIcon) is { } icon && icon.Path.Equals(exePath, StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var matching = ProgramFolders.For(program, guard)
            .Where(folder => exePath.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Length)
            .DefaultIfEmpty(-1)
            .Max();
        return matching;
    }
}

/// <summary>Opens things for the right-click menu. Behind an interface so nothing is launched in tests.</summary>
public interface IExternalActions
{
    void OpenFolder(string folder);

    void OpenRegistryKey(string fullKeyPath);

    void OpenWebAddress(Uri address);

    void OpenFile(string path);
}

public sealed class ExternalActions : IExternalActions
{
    private const string RegeditKey = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });

    public void OpenRegistryKey(string fullKeyPath)
    {
        // Regedit opens at the key stored in "LastKey". Only that harmless UI setting is written.
        var parts = fullKeyPath.Split('\\', 2);
        var root = parts[0].ToUpperInvariant() switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            _ => parts[0],
        };

        using (var key = Registry.CurrentUser.CreateSubKey(RegeditKey))
        {
            key.SetValue("LastKey", $@"Computer\{root}\{(parts.Length > 1 ? parts[1] : string.Empty)}");
        }

        Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
    }

    public void OpenWebAddress(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only https addresses are opened.", nameof(address));
        }

        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

/// <summary>Which program a window on screen belongs to (the "target mode" crosshair).</summary>
public interface IWindowProbe
{
    /// <summary>Path of the exe that owns the window under the mouse. Null for this app or when it cannot be read.</summary>
    string? GetProcessPathAtCursor();
}

public sealed class WindowProbe : IWindowProbe
{
    private const uint GaRoot = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public string? GetProcessPathAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return null;
        }

        var window = NativeMethods.WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var root = NativeMethods.GetAncestor(window, GaRoot);
        NativeMethods.GetWindowThreadProcessId(root == IntPtr.Zero ? window : root, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null;
        }

        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

public sealed record WingetPackage(string Name, string Id, string Version, string? Available, string? Source);

/// <summary>Reads the text table printed by "winget list".</summary>
public static class WingetTableParser
{
    private static readonly Regex Summary = new(@"^\d+\s+(upgrade|package)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<WingetPackage> Parse(string output)
    {
        var lines = output.Replace("\r", string.Empty).Split('\n');
        var headerIndex = Array.FindIndex(lines, l => l.StartsWith("Name", StringComparison.Ordinal) && l.Contains(" Id", StringComparison.Ordinal));
        if (headerIndex < 0)
        {
            return Array.Empty<WingetPackage>();
        }

        var header = lines[headerIndex];
        var starts = new Dictionary<string, int>
        {
            ["Name"] = 0,
            ["Id"] = header.IndexOf(" Id", StringComparison.Ordinal) + 1,
            ["Version"] = header.IndexOf("Version", StringComparison.Ordinal),
            ["Available"] = header.IndexOf("Available", StringComparison.Ordinal),
            ["Source"] = header.IndexOf("Source", StringComparison.Ordinal),
        };

        var result = new List<WingetPackage>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (line.Trim().Length == 0 || line.StartsWith("---", StringComparison.Ordinal) || Summary.IsMatch(line.Trim()))
            {
                continue;
            }

            var name = Cell(line, starts["Name"], Next(starts, "Name", line));
            var id = Cell(line, starts["Id"], Next(starts, "Id", line));
            if (name.Length == 0 || id.Length == 0)
            {
                continue;
            }

            result.Add(new WingetPackage(
                name,
                id,
                Cell(line, starts["Version"], Next(starts, "Version", line)),
                Empty(starts["Available"] >= 0 ? Cell(line, starts["Available"], Next(starts, "Available", line)) : null),
                Empty(starts["Source"] >= 0 ? Cell(line, starts["Source"], line.Length) : null)));
        }

        return result;
    }

    // The next column's start, or the end of the line.
    private static int Next(Dictionary<string, int> starts, string column, string line)
    {
        var order = new[] { "Name", "Id", "Version", "Available", "Source" };
        var index = Array.IndexOf(order, column);
        foreach (var next in order.Skip(index + 1))
        {
            if (starts[next] > starts[column])
            {
                return starts[next];
            }
        }

        return line.Length;
    }

    private static string Cell(string line, int start, int end)
    {
        if (start < 0 || start >= line.Length)
        {
            return string.Empty;
        }

        return line[start..Math.Min(end, line.Length)].Trim();
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

/// <summary>Matches the winget list to the installed programs by name.</summary>
public static class WingetMatcher
{
    public static IReadOnlyDictionary<string, WingetPackage> Match(IEnumerable<InstalledProgram> programs, IReadOnlyList<WingetPackage> packages)
    {
        var byName = packages
            .Where(p => p.Source is not null)
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, WingetPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in programs)
        {
            if (byName.TryGetValue(program.Name.Trim(), out var package))
            {
                result[program.RegistryKeyPath] = package;
            }
        }

        return result;
    }
}

/// <summary>Runs a console program and returns what it printed.</summary>
public interface IConsoleRunner
{
    Task<ScriptResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class ConsoleRunner : IConsoleRunner
{
    public async Task<ScriptResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ScriptResult(-1, $"{Path.GetFileName(fileName)} could not be started.");
            }

            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                return new ScriptResult(process.ExitCode, (await output).Trim());
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Win32Exception ex)
        {
            return new ScriptResult(-1, $"{Path.GetFileName(fileName)} could not be started: {ex.Message}");
        }
    }
}

public interface IWingetService
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<WingetPackage>> ListAsync(CancellationToken cancellationToken);

    Task<ProcessRunResult> UninstallAsync(string id, CancellationToken cancellationToken);

    Task<ProcessRunResult> UpgradeAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Talks to winget. Ids are checked so nothing odd can reach the command line.</summary>
public sealed class WingetService : IWingetService
{
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9._\-+]*$", RegexOptions.Compiled);

    private readonly IConsoleRunner _console;
    private readonly IProcessRunner _runner;
    private readonly IActionLog _log;
    private readonly string _wingetPath;

    public WingetService(IConsoleRunner console, IProcessRunner runner, IActionLog log, string wingetPath)
    {
        _console = console;
        _runner = runner;
        _log = log;
        _wingetPath = wingetPath;
    }

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    public bool IsAvailable => File.Exists(_wingetPath);

    public async Task<IReadOnlyList<WingetPackage>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _console.RunAsync(_wingetPath, new[] { "list", "--accept-source-agreements", "--disable-interactivity" }, cancellationToken);
        var packages = WingetTableParser.Parse(result.Output);
        _log.Info($"winget list: {packages.Count} packages ({packages.Count(p => p.Available is not null)} with updates).");
        return packages;
    }

    public Task<ProcessRunResult> UninstallAsync(string id, CancellationToken cancellationToken) =>
        Run("uninstall", id, "--exact --accept-source-agreements", cancellationToken);

    public Task<ProcessRunResult> UpgradeAsync(string id, CancellationToken cancellationToken) =>
        Run("upgrade", id, "--exact --accept-source-agreements --accept-package-agreements", cancellationToken);

    private Task<ProcessRunResult> Run(string verb, string id, string options, CancellationToken cancellationToken)
    {
        if (!SafeId.IsMatch(id))
        {
            return Task.FromResult(new ProcessRunResult(false, -1, false, $"Not a valid winget id: {id}"));
        }

        _log.Info($"Running winget {verb} for {id}");
        return _runner.RunAsync(_wingetPath, $"{verb} --id {id} {options}", cancellationToken);
    }
}
