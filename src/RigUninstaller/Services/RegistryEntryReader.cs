using System.Security;
using Microsoft.Win32;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Reads raw Uninstall entries. Kept behind an interface so the scanner is testable.</summary>
public interface IRegistryEntryReader
{
    IReadOnlyList<RegistryEntry> ReadAll();
}

/// <summary>Read-only access to the three Uninstall locations. Never writes to the registry.</summary>
public sealed class RegistryEntryReader : IRegistryEntryReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, string Label, string Path)[] Locations =
    {
        (RegistryHive.LocalMachine, "HKLM", UninstallPath),
        (RegistryHive.LocalMachine, "HKLM", UninstallPath32),
        (RegistryHive.CurrentUser, "HKCU", UninstallPath),
    };

    private readonly IActionLog _log;

    public RegistryEntryReader(IActionLog log)
    {
        _log = log;
    }

    public IReadOnlyList<RegistryEntry> ReadAll()
    {
        var entries = new List<RegistryEntry>();

        foreach (var (hive, label, path) in Locations)
        {
            // Registry64 view on purpose: the 32-bit list is reached by its explicit WOW6432Node path.
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(path);
            if (root is null)
            {
                _log.Info($"Uninstall location not present: {label}\\{path}");
                continue;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                var entry = TryReadEntry(root, subKeyName, $@"{label}\{path}\{subKeyName}");
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private RegistryEntry? TryReadEntry(RegistryKey root, string subKeyName, string fullPath)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyName);
            if (key is null)
            {
                return null;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var valueName in UninstallEntryParser.RequiredValueNames)
            {
                values[valueName] = key.GetValue(valueName);
            }

            return new RegistryEntry(fullPath, values);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // One unreadable key must not stop the whole scan.
            _log.Warn($"Could not read {fullPath}: {ex.Message}");
            return null;
        }
    }
}
