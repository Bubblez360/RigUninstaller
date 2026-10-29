namespace RigUninstaller.Models;

/// <summary>Why a registry entry was left out of the program list.</summary>
public enum SkipReason
{
    None,
    NoName,
    SystemComponent,
    Update,
}

/// <summary>Result of parsing one entry: either a program or the reason it was skipped.</summary>
public sealed record ParseResult(InstalledProgram? Program, SkipReason Skip);

/// <summary>Programs found plus counts that explain what was left out.</summary>
public sealed record ScanResult(
    IReadOnlyList<InstalledProgram> Programs,
    int TotalEntries,
    int SkippedNoName,
    int SkippedSystemComponent,
    int SkippedUpdate,
    int DuplicatesRemoved);
