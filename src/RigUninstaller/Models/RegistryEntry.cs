namespace RigUninstaller.Models;

/// <summary>Raw values of one Uninstall subkey, before any filtering. Value names are case-insensitive.</summary>
public sealed class RegistryEntry
{
    public RegistryEntry(string keyPath, IReadOnlyDictionary<string, object?> values)
    {
        KeyPath = keyPath ?? throw new ArgumentNullException(nameof(keyPath));
        Values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Full path including hive, e.g. HKLM\SOFTWARE\...\Uninstall\{GUID}.</summary>
    public string KeyPath { get; }

    public IReadOnlyDictionary<string, object?> Values { get; }
}
