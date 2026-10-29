using System.Text.RegularExpressions;

namespace RigUninstaller.Services;

/// <summary>Outcome of a safety check. Denied results always carry a human-readable reason.</summary>
public sealed record GuardResult(bool IsAllowed, string Reason)
{
    public static GuardResult Allow() => new(true, string.Empty);

    public static GuardResult Deny(string reason) => new(false, reason);
}

/// <summary>Thrown when code tries to touch something the guard forbids.</summary>
public sealed class SafetyViolationException : Exception
{
    public SafetyViolationException(string message) : base(message)
    {
    }
}

/// <summary>Well-known folders the guard protects. Injectable so tests do not depend on the machine.</summary>
public sealed record SafetyGuardRoots(
    string SystemDrive,
    string WindowsDir,
    string ProgramFiles,
    string ProgramFilesX86,
    string ProgramData,
    string UserProfile,
    string AppDataRoaming,
    string AppDataLocal)
{
    public static SafetyGuardRoots FromEnvironment()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new SafetyGuardRoots(
            SystemDrive: Path.GetPathRoot(windows) ?? @"C:\",
            WindowsDir: windows,
            ProgramFiles: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            UserProfile: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppDataRoaming: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataLocal: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }
}

/// <summary>
/// The single gate for every file delete and registry write.
/// Rule: if the guard says no, nothing touches that path or key. No override switch exists on purpose.
/// </summary>
public sealed class SafetyGuard
{
    private static readonly Regex DriveRooted = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    // Start Menu folders that belong to Windows, not to an installed program.
    private static readonly string[] StartMenuSystemFolders =
    {
        "Startup", "Accessories", "Administrative Tools", "System Tools", "Maintenance",
        "Windows PowerShell", "Windows Accessories", "Windows System", "Windows Ease of Access",
    };

    // Registry keys under Software\Microsoft that belong to Windows (besides anything starting with "Windows").
    private static readonly string[] CoreMicrosoftRegistryKeys = { "Cryptography", "SystemCertificates" };

    private readonly string[] _protectedTrees;
    private readonly string[] _protectedExact;
    private readonly string[] _programFilesRoots;
    private readonly string[] _startMenuProgramsDirs;

    public SafetyGuard() : this(SafetyGuardRoots.FromEnvironment())
    {
    }

    public SafetyGuard(SafetyGuardRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var pf = Normalize(roots.ProgramFiles);
        var pf86 = Normalize(roots.ProgramFilesX86);
        _programFilesRoots = new[] { pf, pf86 };

        var trees = new List<string>
        {
            Normalize(roots.WindowsDir),
            Combine(roots.ProgramData, @"Microsoft\Windows"),
            Combine(roots.ProgramData, @"Microsoft\Windows Defender"),
            Combine(roots.ProgramData, @"Microsoft\Crypto"),
            Combine(roots.AppDataRoaming, @"Microsoft\Windows"),
            Combine(roots.AppDataLocal, @"Microsoft\Windows"),
            Combine(roots.AppDataLocal, "Packages"),
        };
        foreach (var root in _programFilesRoots)
        {
            foreach (var name in new[] { "WindowsApps", "WindowsPowerShell", "Reference Assemblies", "Microsoft.NET" })
            {
                trees.Add(Combine(root, name));
            }

            trees.Add(Combine(root, @"Common Files\Microsoft Shared"));
            trees.Add(Combine(root, @"Common Files\System"));
        }

        _protectedTrees = trees.ToArray();

        // Protected as a folder itself only; its children (a program's own folder) stay allowed.
        var exact = new List<string>
        {
            Normalize(roots.SystemDrive),
            pf,
            pf86,
            Normalize(roots.ProgramData),
            Normalize(roots.UserProfile),
            Normalize(roots.AppDataRoaming),
            Normalize(roots.AppDataLocal),
            Combine(roots.ProgramData, "Microsoft"),
            Combine(roots.AppDataRoaming, "Microsoft"),
            Combine(roots.AppDataLocal, "Microsoft"),
            Combine(roots.UserProfile, @"AppData\LocalLow"),
        };
        AddIfNotNull(exact, Path.GetDirectoryName(Normalize(roots.UserProfile)));
        AddIfNotNull(exact, Path.GetDirectoryName(Normalize(roots.AppDataRoaming)));
        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos" })
        {
            exact.Add(Combine(roots.UserProfile, name));
        }

        foreach (var root in _programFilesRoots)
        {
            exact.Add(Combine(root, "Common Files"));
        }

        _protectedExact = exact.Select(TrimTrailingSeparator).ToArray();

        // Start Menu "Programs" folders: shortcuts of installed programs live here and are valid leftovers.
        _startMenuProgramsDirs = new[]
        {
            Combine(roots.ProgramData, @"Microsoft\Windows\Start Menu\Programs"),
            Combine(roots.AppDataRoaming, @"Microsoft\Windows\Start Menu\Programs"),
        };
    }

    /// <summary>Checks a file or folder path. Local, absolute paths only.</summary>
    public GuardResult CheckPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GuardResult.Deny("Path is empty.");
        }

        var raw = StripExtendedPrefix(path.Trim());
        if (raw.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return GuardResult.Deny("Network paths are never touched.");
        }

        if (!DriveRooted.IsMatch(raw))
        {
            return GuardResult.Deny("Only absolute local paths are allowed.");
        }

        string full;
        try
        {
            full = TrimTrailingSeparator(Path.GetFullPath(raw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Deny($"Path is not valid: {ex.Message}");
        }

        if (full.Length == 3)
        {
            return GuardResult.Deny("Drive roots are never touched.");
        }

        if (full.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Deny("Driver files (.sys) are never touched.");
        }

        // A program's own Start Menu entry is allowed, unless it is one of Windows' folders.
        foreach (var programs in _startMenuProgramsDirs)
        {
            if (IsStrictlyUnder(full, programs))
            {
                var child = full[(programs.Length + 1)..].Split('\\')[0];
                return StartMenuSystemFolders.Contains(child, StringComparer.OrdinalIgnoreCase)
                    ? GuardResult.Deny("Windows Start Menu folder.")
                    : GuardResult.Allow();
            }
        }

        foreach (var tree in _protectedTrees)
        {
            if (IsUnderOrEqual(full, tree))
            {
                return GuardResult.Deny($"Protected Windows location: {tree}");
            }
        }

        foreach (var root in _programFilesRoots)
        {
            if (IsStrictlyUnder(full, root) && full[(root.Length + 1)..].Split('\\')[0].StartsWith("Windows ", StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Deny("Windows component folder inside Program Files.");
            }
        }

        foreach (var exact in _protectedExact)
        {
            if (string.Equals(full, exact, StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Deny($"Protected system folder: {exact}");
            }
        }

        return GuardResult.Allow();
    }

    /// <summary>
    /// Checks a full registry key path such as <c>HKLM\SOFTWARE\Acme</c>.
    /// Only HKLM\SOFTWARE and HKCU\Software are ever allowed, and never the Windows core parts.
    /// </summary>
    public GuardResult CheckRegistryKey(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return GuardResult.Deny("Registry key is empty.");
        }

        var segments = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return GuardResult.Deny("Registry key is empty.");
        }

        var hive = segments[0].ToUpperInvariant();
        string softwareRoot;
        switch (hive)
        {
            case "HKLM" or "HKEY_LOCAL_MACHINE":
                softwareRoot = "SOFTWARE";
                break;
            case "HKCU" or "HKEY_CURRENT_USER":
                softwareRoot = "Software";
                break;
            case "HKCR" or "HKEY_CLASSES_ROOT" or "HKU" or "HKEY_USERS" or "HKCC" or "HKEY_CURRENT_CONFIG":
                return GuardResult.Deny("Only HKLM\\SOFTWARE and HKCU\\Software may be changed.");
            default:
                return GuardResult.Deny("Unknown registry hive.");
        }

        if (segments.Length < 2)
        {
            return GuardResult.Deny("Registry hive roots are never touched.");
        }

        if (!string.Equals(segments[1], softwareRoot, StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Deny($"Only the {softwareRoot} branch may be changed.");
        }

        // Relative path below Software; the 32-bit view (WOW6432Node) follows the same rules.
        var rel = segments.Skip(2).ToArray();
        if (rel.Length > 0 && string.Equals(rel[0], "WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel.Skip(1).ToArray();
        }

        if (rel.Length == 0)
        {
            return GuardResult.Deny("Software root keys are never touched.");
        }

        if (Is(rel[0], "Classes") || Is(rel[0], "Policies"))
        {
            return GuardResult.Deny("Classes and Policies keys are never touched.");
        }

        if (Is(rel[0], "Microsoft"))
        {
            if (rel.Length == 1)
            {
                return GuardResult.Deny("The Microsoft root key is never touched.");
            }

            var name = rel[1];
            var isCore = name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
                         || CoreMicrosoftRegistryKeys.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (isCore)
            {
                // A program's own Uninstall entry (one level below Uninstall) is the only allowed exception.
                var isUninstallEntry = rel.Length >= 5
                                       && Is(rel[1], "Windows")
                                       && Is(rel[2], "CurrentVersion")
                                       && Is(rel[3], "Uninstall");
                return isUninstallEntry
                    ? GuardResult.Allow()
                    : GuardResult.Deny("Windows core registry path.");
            }
        }

        return GuardResult.Allow();
    }

    /// <summary>Throws <see cref="SafetyViolationException"/> when the path is not allowed.</summary>
    public void EnsurePathAllowed(string? path)
    {
        var result = CheckPath(path);
        if (!result.IsAllowed)
        {
            throw new SafetyViolationException($"Blocked path '{path}': {result.Reason}");
        }
    }

    /// <summary>Throws <see cref="SafetyViolationException"/> when the registry key is not allowed.</summary>
    public void EnsureRegistryKeyAllowed(string? keyPath)
    {
        var result = CheckRegistryKey(keyPath);
        if (!result.IsAllowed)
        {
            throw new SafetyViolationException($"Blocked registry key '{keyPath}': {result.Reason}");
        }
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderOrEqual(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(path, folder);

    private static bool IsStrictlyUnder(string path, string folder) =>
        path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);

    private static string StripExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;

    private static string TrimTrailingSeparator(string path)
    {
        // Keep "C:\" intact; trim everything else.
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length <= 2 ? trimmed + @"\" : trimmed;
    }

    private static string Normalize(string path) => TrimTrailingSeparator(Path.GetFullPath(path));

    private static string Combine(string root, string relative) => Normalize(Path.Combine(root, relative));

    private static void AddIfNotNull(List<string> list, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            list.Add(value);
        }
    }
}
