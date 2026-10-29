using RigUninstaller.Models;

namespace RigUninstaller.Services;

public interface IProgramScanner
{
    ScanResult Scan();
}

/// <summary>Reads the Uninstall keys, drops what should not be listed, removes duplicates, sorts by name.</summary>
public sealed class ProgramScanner : IProgramScanner
{
    private readonly IRegistryEntryReader _reader;
    private readonly IActionLog _log;

    public ProgramScanner(IRegistryEntryReader reader, IActionLog log)
    {
        _reader = reader;
        _log = log;
    }

    public ScanResult Scan()
    {
        var entries = _reader.ReadAll();

        var programs = new List<InstalledProgram>();
        int noName = 0, system = 0, update = 0;
        foreach (var entry in entries)
        {
            var parsed = UninstallEntryParser.Parse(entry);
            switch (parsed.Skip)
            {
                case SkipReason.NoName: noName++; break;
                case SkipReason.SystemComponent: system++; break;
                case SkipReason.Update: update++; break;
                default: programs.Add(parsed.Program!); break;
            }
        }

        var deduped = ProgramDeduplicator.Deduplicate(programs);
        var sorted = deduped.Programs
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new ScanResult(sorted, entries.Count, noName, system, update, deduped.Removed);
        _log.Info(
            $"Scan finished: {result.Programs.Count} programs from {result.TotalEntries} registry entries " +
            $"(skipped: {noName} no name, {system} system component, {update} update; {result.DuplicatesRemoved} duplicates merged)");
        return result;
    }
}
