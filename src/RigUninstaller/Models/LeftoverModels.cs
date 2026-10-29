namespace RigUninstaller.Models;

public enum LeftoverKind
{
    Folder,
    File,
    RegistryKey,
}

/// <summary>How sure the scan is that an item belongs to the removed program.</summary>
public enum Confidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>A file, folder or registry key that probably belongs to a program that was uninstalled.</summary>
/// <param name="Path">A file path, or a full registry key path such as HKCU\Software\Acme.</param>
public sealed record LeftoverItem(LeftoverKind Kind, string Path, Confidence Confidence, string Reason);

/// <summary>The items the user ticked in the leftovers window.</summary>
public sealed record LeftoverChoice(IReadOnlyList<LeftoverItem> Items, bool CreateRestorePoint);

public sealed record CleanupFailure(LeftoverItem Item, string Error);

public sealed record CleanupResult(
    int Moved,
    int RegistryRemoved,
    int AlreadyGone,
    IReadOnlyList<CleanupFailure> Failures,
    bool WasCancelled);

public enum LeftoverFlowStatus
{
    NoneFound,
    OnlyLowConfidence,
    LeftInPlace,
    Cleaned,
    Cancelled,
}

public sealed record LeftoverFlowResult(
    LeftoverFlowStatus Status,
    int Found,
    int Cleaned,
    int Failed,
    IReadOnlyList<CleanupFailure> Failures,
    string Summary);

public sealed record RegistryExportResult(bool Success, string Message);

public static class BackupKinds
{
    public const string RegistryExport = "RegistryExport";
    public const string RecycledItem = "RecycledItem";
    public const string RestorePoint = "RestorePoint";
    public const string StartupChange = "StartupChange";

    /// <summary>A line that says an earlier entry (named in Detail) was restored.</summary>
    public const string Restored = "Restored";
}

/// <summary>One line of the backup journal: something the app saved or moved so it can be undone later.</summary>
/// <param name="Detail">Extra information: the old state of a startup item, or the key of a restored entry.</param>
public sealed record BackupEntry(DateTime Time, string Kind, string Program, string Target, string? BackupFile, string? Detail = null)
{
    /// <summary>Identifies this entry so a later "Restored" line can point at it.</summary>
    public string Key => $"{Time:O}|{Kind}|{Target}";
}
