namespace RigUninstaller.Models;

/// <summary>One program as listed in an Uninstall registry key. Immutable.</summary>
public sealed record InstalledProgram
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    public DateTime? InstallDate { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Registry "EstimatedSize" value, which Windows stores in kilobytes. Null when unknown.</summary>
    public long? EstimatedSizeKb { get; init; }

    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    /// <summary>Full key path, e.g. HKLM\SOFTWARE\...\Uninstall\{GUID}. Used later for backup and force remove.</summary>
    public required string RegistryKeyPath { get; init; }

    /// <summary>Last part of <see cref="RegistryKeyPath"/>, often a product GUID.</summary>
    public required string KeyName { get; init; }

    public long? SizeBytes => EstimatedSizeKb is { } kb ? kb * 1024 : null;

    public bool IsPerUser => RegistryKeyPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase);

    // Kept outside the record: a record compares all its fields, so a cached field would change equality and hash codes
    // the first time Drive is read, and lookups keyed by program (the row cache) would stop finding it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InstalledProgram, string> DriveCache = new();

    /// <summary>The drive the program is installed on, e.g. "C:" ("Unknown" or "Network" when it cannot be told). Worked out once.</summary>
    public string Drive => DriveCache.GetValue(this, Services.DiskUsage.DriveOf);
}
