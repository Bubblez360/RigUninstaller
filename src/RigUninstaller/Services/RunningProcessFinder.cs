using System.ComponentModel;
using System.Diagnostics;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>A list of running programs with their file paths. Behind an interface so matching is testable.</summary>
public interface IProcessSnapshot
{
    IReadOnlyList<RunningProcessInfo> GetProcesses();
}

public interface IRunningProcessFinder
{
    IReadOnlyList<RunningProcessInfo> FindRunning(InstalledProgram program);
}

public sealed class ProcessSnapshot : IProcessSnapshot
{
    public IReadOnlyList<RunningProcessInfo> GetProcesses()
    {
        var result = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } path)
                {
                    result.Add(new RunningProcessInfo(process.Id, process.ProcessName, path));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // System and protected processes refuse to show their path. They are never an app's own, so skip.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}

/// <summary>
/// Finds programs that are still running from a program's install folder, so the user can close them
/// before uninstalling. Folders that belong to Windows are never used to match.
/// </summary>
public sealed class RunningProcessFinder : IRunningProcessFinder
{
    private readonly IProcessSnapshot _snapshot;
    private readonly SafetyGuard _guard;

    public RunningProcessFinder(IProcessSnapshot snapshot, SafetyGuard guard)
    {
        _snapshot = snapshot;
        _guard = guard;
    }

    public IReadOnlyList<RunningProcessInfo> FindRunning(InstalledProgram program)
    {
        var folders = CandidateFolders(program).ToList();
        if (folders.Count == 0)
        {
            return Array.Empty<RunningProcessInfo>();
        }

        return _snapshot.GetProcesses()
            .Where(p => p.Id != Environment.ProcessId)
            .Where(p => folders.Any(folder => p.Path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(p => p.Id)
            .Select(group => group.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> CandidateFolders(InstalledProgram program) => ProgramFolders.For(program, _guard);
}
