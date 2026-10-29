namespace RigUninstaller.Services;

/// <summary>All app-owned folders live under one Data folder next to the exe.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "Logs");

    /// <summary>.reg exports and the backup journal live here.</summary>
    public static string BackupDirectory { get; } = Path.Combine(DataDirectory, "Backups");
}
