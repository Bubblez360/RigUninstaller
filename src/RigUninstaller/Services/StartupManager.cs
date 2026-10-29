using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum StartupKind
{
    RunKey,
    StartupFolder,
    ScheduledTask,
}

/// <param name="Id">Stable text that identifies the item for changes and undo.</param>
/// <param name="Location">Where it lives, e.g. "Run (current user)".</param>
public sealed record StartupItem(string Id, string Name, string Command, string Location, StartupKind Kind, bool IsEnabled);

public sealed record StartupChangeResult(bool Success, string Message);

/// <summary>How Windows records "this startup item is turned off" without deleting it (the Task Manager way).</summary>
public static class StartupApproved
{
    private const int Length = 12;

    /// <summary>A missing value, or one whose first byte is even, means enabled.</summary>
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 1) == 0;

    public static byte[] Create(bool enabled, DateTime now)
    {
        var data = new byte[Length];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
        {
            BitConverter.GetBytes(now.ToFileTimeUtc()).CopyTo(data, 4);
        }

        return data;
    }
}

/// <summary>Reads where a .lnk shortcut points (Windows Script Host does the parsing).</summary>
public static class ShortcutResolver
{
    public static (string Target, string Arguments)? Resolve(string shortcutPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return null;
            }

            object? shell = null;
            object? link = null;
            try
            {
                shell = Activator.CreateInstance(type)!;
                dynamic shellObject = shell;
                link = shellObject.CreateShortcut(shortcutPath);
                dynamic linkObject = link!;
                string target = linkObject.TargetPath;
                string arguments = linkObject.Arguments;
                return string.IsNullOrWhiteSpace(target) ? null : (target, arguments.Trim());
            }
            finally
            {
                // Release the COM objects now instead of waiting for the garbage collector.
                if (link is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure reading the shortcut (COM, binder, missing file) means "cannot tell where it points".
            return null;
        }
    }
}

public enum StartupHive
{
    CurrentUser,
    LocalMachine,
}

/// <summary>Registry access for startup items. Behind an interface so tests never touch real startup entries.</summary>
public interface IStartupRegistry
{
    IReadOnlyList<(string Name, string Command)> GetRunValues(StartupHive hive, bool wow64);

    byte[]? GetApproved(StartupHive hive, string approvedKey, string name);

    void SetApproved(StartupHive hive, string approvedKey, string name, byte[] data);

    IReadOnlyList<string> GetStartupFolderFiles(bool allUsers);

    /// <summary>Adds (or replaces) a value under the Run key of the given hive.</summary>
    void SetRunValue(StartupHive hive, string name, string command);
}

public sealed class StartupRegistry : IStartupRegistry
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public IReadOnlyList<(string Name, string Command)> GetRunValues(StartupHive hive, bool wow64)
    {
        try
        {
            using var key = Open(hive).OpenSubKey(wow64 ? RunPath32 : RunPath);
            return key?.GetValueNames()
                       .Where(n => n.Length > 0)
                       .Select(n => (n, key.GetValue(n)?.ToString() ?? string.Empty))
                       .ToList()
                   ?? new List<(string, string)>();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new List<(string, string)>();
        }
    }

    public byte[]? GetApproved(StartupHive hive, string approvedKey, string name)
    {
        try
        {
            using var key = Open(hive).OpenSubKey(ApprovedPath + approvedKey);
            return key?.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void SetApproved(StartupHive hive, string approvedKey, string name, byte[] data)
    {
        using var key = Open(hive).CreateSubKey(ApprovedPath + approvedKey);
        key.SetValue(name, data, RegistryValueKind.Binary);
    }

    public void SetRunValue(StartupHive hive, string name, string command)
    {
        using var key = Open(hive).CreateSubKey(RunPath);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public IReadOnlyList<string> GetStartupFolderFiles(bool allUsers)
    {
        var folder = Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static RegistryKey Open(StartupHive hive) =>
        RegistryKey.OpenBaseKey(hive == StartupHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
}

public interface IStartupManager
{
    Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Turns an item on or off. Never deletes it; the old state is journaled.</summary>
    Task<StartupChangeResult> SetEnabledAsync(StartupItem item, bool enable, CancellationToken cancellationToken);

    /// <summary>Same as <see cref="SetEnabledAsync"/> but by id, for undo. Nothing is journaled.</summary>
    Task<StartupChangeResult> RestoreAsync(string itemId, bool enable, CancellationToken cancellationToken);

    /// <summary>Makes a program start with Windows for the current user (a value under HKCU Run). Nothing is deleted.</summary>
    Task<StartupChangeResult> AddAsync(string programPath, CancellationToken cancellationToken);
}

/// <summary>
/// Lists startup items from the Run keys, the Startup folders and logon tasks in Task Scheduler,
/// and turns them off and on again. Every change is logged and journaled so it can be undone.
/// </summary>
public sealed class StartupManager : IStartupManager
{
    private const string EnableTaskScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $full = $env:RIG_TASK_PATH
            $name = Split-Path $full -Leaf
            $path = (Split-Path $full -Parent) + '\'
            if ($env:RIG_TASK_ENABLE -eq '1') { Enable-ScheduledTask -TaskPath $path -TaskName $name | Out-Null }
            else { Disable-ScheduledTask -TaskPath $path -TaskName $name | Out-Null }
            Write-Output 'OK'
        } catch { Write-Output ('FAIL: ' + $_.Exception.Message); exit 2 }
        """;

    private const string ListTasksScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $items = foreach ($t in Get-ScheduledTask) {
            if ($t.TaskPath -like '\Microsoft\Windows\*') { continue }
            if (-not ($t.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' })) { continue }
            $a = $t.Actions | Select-Object -First 1
            [pscustomobject]@{ Path = ($t.TaskPath + $t.TaskName); Name = $t.TaskName; Enabled = [bool]($t.State -ne 'Disabled'); Command = (($a.Execute + ' ' + $a.Arguments).Trim()) }
        }
        if ($null -eq $items) { Write-Output '[]' } else { ConvertTo-Json -InputObject @($items) -Compress }
        """;

    private readonly IStartupRegistry _registry;
    private readonly IPowerShellRunner _shell;
    private readonly IBackupJournal _journal;
    private readonly IActionLog _log;

    public StartupManager(IStartupRegistry registry, IPowerShellRunner shell, IBackupJournal journal, IActionLog log)
    {
        _registry = registry;
        _shell = shell;
        _journal = journal;
        _log = log;
    }

    public async Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<StartupItem>();
        AddRunKeys(items, StartupHive.CurrentUser, false, "Run", "Run (current user)");
        AddRunKeys(items, StartupHive.LocalMachine, false, "Run", "Run (all users)");
        AddRunKeys(items, StartupHive.LocalMachine, true, "Run32", "Run (all users, 32-bit)");
        AddFolder(items, allUsers: false, StartupHive.CurrentUser, "Startup folder (current user)");
        AddFolder(items, allUsers: true, StartupHive.LocalMachine, "Startup folder (all users)");
        items.AddRange(await ListTasksAsync(cancellationToken));

        _log.Info($"Startup items found: {items.Count} ({items.Count(i => i.IsEnabled)} enabled).");
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<StartupChangeResult> SetEnabledAsync(StartupItem item, bool enable, CancellationToken cancellationToken)
    {
        var result = await ApplyAsync(item.Id, item.Name, enable, cancellationToken);
        if (result.Success)
        {
            // The old state is what undo needs.
            _journal.Add(new BackupEntry(DateTime.Now, BackupKinds.StartupChange, item.Name, item.Id, null, item.IsEnabled ? "enabled" : "disabled"));
        }

        return result;
    }

    public Task<StartupChangeResult> RestoreAsync(string itemId, bool enable, CancellationToken cancellationToken) =>
        ApplyAsync(itemId, itemId, enable, cancellationToken);

    // Off the UI thread: reading a shortcut and checking the file can stall on an offline mapped drive.
    public Task<StartupChangeResult> AddAsync(string programPath, CancellationToken cancellationToken) =>
        Task.Run(() => AddCore(programPath), cancellationToken);

    private Task<StartupChangeResult> AddCore(string programPath)
    {
        var program = programPath.Trim().Trim('"');
        var arguments = string.Empty;
        if (Path.GetExtension(program).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (ShortcutResolver.Resolve(program) is not { } target)
            {
                return Task.FromResult(new StartupChangeResult(false, "I could not read where that shortcut points. Pick the program's .exe file instead."));
            }

            (program, arguments) = target;
        }

        if (CheckProgram(program) is { } problem)
        {
            return Task.FromResult(new StartupChangeResult(false, problem));
        }

        var name = SafeValueName(Path.GetFileNameWithoutExtension(program));
        var existing = _registry.GetRunValues(StartupHive.CurrentUser, false)
            .Concat(_registry.GetRunValues(StartupHive.LocalMachine, false))
            .Concat(_registry.GetRunValues(StartupHive.LocalMachine, true))
            .FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 || v.Command.Contains(program, StringComparison.OrdinalIgnoreCase));
        if (existing.Name is not null)
        {
            return Task.FromResult(new StartupChangeResult(false, $"It already starts with Windows (startup item \"{existing.Name}\"). Turn it on in the list if it is off."));
        }

        var command = arguments.Length > 0 ? $"\"{program}\" {arguments}" : $"\"{program}\"";
        try
        {
            // A turned-off item with the same name may be remembered by Windows; make sure this one is on.
            // Done first: if the Run value then fails, nothing half-added is left behind (an "on" mark alone does nothing).
            SetApproved(StartupHive.CurrentUser, "Run", name, enable: true);
            _registry.SetRunValue(StartupHive.CurrentUser, name, command);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Error($"Could not add {program} to startup", ex);
            return Task.FromResult(new StartupChangeResult(false, ex.Message));
        }

        _log.Info($"Added {program} to startup for the current user (Run key value \"{name}\"). It can be turned off in the Startup tab.");
        return Task.FromResult(new StartupChangeResult(true, $"{name} will start with Windows. You can turn it off in this list at any time."));
    }

    private static readonly string[] ProgramTypes = { ".exe", ".bat", ".cmd", ".com" };

    private static string? CheckProgram(string program)
    {
        // A drive letter and a backslash: no network paths, no "C:file.exe" (drive-relative) or "\file.exe" forms.
        if (!System.Text.RegularExpressions.Regex.IsMatch(program, @"^[A-Za-z]:\\"))
        {
            return "Pick a program on one of this PC's drives.";
        }

        if (!File.Exists(program))
        {
            return "That file does not exist.";
        }

        return ProgramTypes.Contains(Path.GetExtension(program), StringComparer.OrdinalIgnoreCase)
            ? null
            : "Pick a program (.exe) or a script (.bat, .cmd) or a shortcut to one.";
    }

    private static string SafeValueName(string name)
    {
        var cleaned = new string(name.Where(c => c != '\\' && !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "Program";
    }

    private async Task<StartupChangeResult> ApplyAsync(string id, string displayName, bool enable, CancellationToken cancellationToken)
    {
        var parts = id.Split('|', 4);
        try
        {
            switch (parts[0])
            {
                case "RUN" when parts.Length == 4:
                    SetApproved(ParseHive(parts[1]), parts[2], parts[3], enable);
                    break;
                case "FOLDER" when parts.Length == 3:
                    SetApproved(ParseHive(parts[1]), "StartupFolder", parts[2], enable);
                    break;
                case "TASK" when parts.Length == 2:
                    return await SetTaskAsync(parts[1], displayName, enable, cancellationToken);
                default:
                    return new StartupChangeResult(false, $"Unknown startup item: {id}");
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Error($"Could not change startup item {displayName}", ex);
            return new StartupChangeResult(false, ex.Message);
        }

        _log.Info($"Startup item {displayName} {(enable ? "enabled" : "disabled")} (it was not deleted).");
        return new StartupChangeResult(true, enable ? "Enabled." : "Disabled.");
    }

    private void SetApproved(StartupHive hive, string approvedKey, string name, bool enable) =>
        _registry.SetApproved(hive, approvedKey, name, StartupApproved.Create(enable, DateTime.Now));

    private async Task<StartupChangeResult> SetTaskAsync(string taskPath, string displayName, bool enable, CancellationToken cancellationToken)
    {
        var result = await _shell.RunAsync(
            EnableTaskScript,
            new Dictionary<string, string> { ["RIG_TASK_PATH"] = taskPath, ["RIG_TASK_ENABLE"] = enable ? "1" : "0" },
            cancellationToken);
        if (result.ExitCode == 0 && result.Output.Contains("OK", StringComparison.Ordinal))
        {
            _log.Info($"Scheduled task {taskPath} {(enable ? "enabled" : "disabled")} (it was not deleted).");
            return new StartupChangeResult(true, enable ? "Enabled." : "Disabled.");
        }

        var message = result.Output.StartsWith("FAIL:", StringComparison.Ordinal) ? result.Output[5..].Trim() : result.Output;
        _log.Error($"Could not change scheduled task {taskPath}: {message}");
        return new StartupChangeResult(false, message.Length > 0 ? message : $"Windows PowerShell stopped (exit code {result.ExitCode}).");
    }

    private void AddRunKeys(List<StartupItem> items, StartupHive hive, bool wow64, string approvedKey, string location)
    {
        foreach (var (name, command) in _registry.GetRunValues(hive, wow64))
        {
            var enabled = StartupApproved.IsEnabled(_registry.GetApproved(hive, approvedKey, name));
            items.Add(new StartupItem($"RUN|{hive}|{approvedKey}|{name}", name, command, location, StartupKind.RunKey, enabled));
        }
    }

    private void AddFolder(List<StartupItem> items, bool allUsers, StartupHive hive, string location)
    {
        foreach (var file in _registry.GetStartupFolderFiles(allUsers))
        {
            var fileName = Path.GetFileName(file);
            var enabled = StartupApproved.IsEnabled(_registry.GetApproved(hive, "StartupFolder", fileName));
            items.Add(new StartupItem($"FOLDER|{hive}|{fileName}", Path.GetFileNameWithoutExtension(fileName), file, location, StartupKind.StartupFolder, enabled));
        }
    }

    private async Task<IReadOnlyList<StartupItem>> ListTasksAsync(CancellationToken cancellationToken)
    {
        var result = await _shell.RunAsync(ListTasksScript, new Dictionary<string, string>(), cancellationToken);
        if (result.ExitCode != 0 || !result.Output.TrimStart().StartsWith('['))
        {
            _log.Warn($"Could not list logon tasks: {result.Output}");
            return Array.Empty<StartupItem>();
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.EnumerateArray()
                .Select(e => new StartupItem(
                    "TASK|" + e.GetProperty("Path").GetString(),
                    e.GetProperty("Name").GetString() ?? string.Empty,
                    e.GetProperty("Command").GetString() ?? string.Empty,
                    "Task Scheduler (at logon)",
                    StartupKind.ScheduledTask,
                    e.GetProperty("Enabled").GetBoolean()))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.Warn($"Could not read the logon task list: {ex.Message}");
            return Array.Empty<StartupItem>();
        }
    }

    private static StartupHive ParseHive(string text) => Enum.Parse<StartupHive>(text);
}
