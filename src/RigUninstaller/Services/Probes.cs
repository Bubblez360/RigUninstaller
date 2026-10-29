using System.Security;
using Microsoft.Win32;

namespace RigUninstaller.Services;

/// <summary>Read-only look at files and folders. Behind an interface so the scan can be tested.</summary>
public interface IFileSystemProbe
{
    /// <summary>Full paths of the sub folders. Empty when the folder is missing or cannot be read.</summary>
    IReadOnlyList<string> GetDirectories(string folder);

    IReadOnlyList<string> GetFiles(string folder);

    /// <summary>True for junctions and symbolic links, which are never followed or deleted. Unreadable paths count as true.</summary>
    bool IsReparsePoint(string path);

    bool Exists(string path);
}

public sealed class FileSystemProbe : IFileSystemProbe
{
    public IReadOnlyList<string> GetDirectories(string folder) =>
        Enumerate(() => Directory.EnumerateDirectories(folder));

    public IReadOnlyList<string> GetFiles(string folder) =>
        Enumerate(() => Directory.EnumerateFiles(folder));

    public bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Cannot even read its attributes: do not touch it.
            return true;
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static IReadOnlyList<string> Enumerate(Func<IEnumerable<string>> source)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Missing or protected folders simply have nothing for us to list.
            return Array.Empty<string>();
        }
    }
}

/// <summary>Read-only look at registry keys.</summary>
public interface IRegistryProbe
{
    /// <summary>Names of the sub keys of a full key path such as HKCU\Software. Empty when missing or unreadable.</summary>
    IReadOnlyList<string> GetSubKeyNames(string keyPath);
}

public sealed class RegistryProbe : IRegistryProbe
{
    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        if (!RegistryPath.TryParse(keyPath, out var hive, out var subPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>Splits "HKLM\SOFTWARE\Acme" into the hive and the path below it. Only HKLM and HKCU are understood.</summary>
internal static class RegistryPath
{
    public static bool TryParse(string fullPath, out RegistryHive hive, out string subPath)
    {
        hive = default;
        subPath = string.Empty;

        var split = fullPath.Split('\\', 2);
        if (split.Length < 2 || split[1].Length == 0)
        {
            return false;
        }

        switch (split[0].ToUpperInvariant())
        {
            case "HKLM" or "HKEY_LOCAL_MACHINE":
                hive = RegistryHive.LocalMachine;
                break;
            case "HKCU" or "HKEY_CURRENT_USER":
                hive = RegistryHive.CurrentUser;
                break;
            default:
                return false;
        }

        subPath = split[1];
        return true;
    }
}
