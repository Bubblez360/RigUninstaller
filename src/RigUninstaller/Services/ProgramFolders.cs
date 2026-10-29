using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Works out which folders belong to an installed program, from the hints the registry gives.</summary>
public static class ProgramFolders
{
    /// <summary>
    /// Install location, the folder of the DisplayIcon file and the folder of the uninstaller, tidied.
    /// Folders the safety guard protects (Windows, Program Files itself...) are never returned.
    /// </summary>
    public static IReadOnlyList<string> For(InstalledProgram program, SafetyGuard guard)
    {
        var raw = new List<string?> { program.InstallLocation };

        if (DisplayIconParser.Parse(program.DisplayIcon) is { } icon)
        {
            raw.Add(Path.GetDirectoryName(icon.Path));
        }

        var plans = UninstallCommandParser.CreatePlans(program);
        if (plans.Normal is { IsMsi: false } normal)
        {
            raw.Add(Path.GetDirectoryName(normal.FileName));
        }

        return raw
            .Select(Tidy)
            .Where(folder => folder.Length > 0 && guard.CheckPath(folder).IsAllowed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Tidy(string? folder) =>
        (folder ?? string.Empty).Trim().Trim('"').TrimEnd('\\', '/');
}
