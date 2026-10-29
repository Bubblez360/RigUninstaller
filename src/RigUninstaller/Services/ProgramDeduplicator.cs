using RigUninstaller.Models;

namespace RigUninstaller.Services;

public sealed record DedupeResult(IReadOnlyList<InstalledProgram> Programs, int Removed);

/// <summary>
/// The same program often shows up in more than one Uninstall location (64-bit, 32-bit, per-user).
/// Entries with the same name, version and publisher are treated as one program.
/// </summary>
public static class ProgramDeduplicator
{
    public static DedupeResult Deduplicate(IEnumerable<InstalledProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);

        // Insertion order of the first occurrence of each key is kept.
        var order = new List<string>();
        var best = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        foreach (var program in programs)
        {
            total++;
            var key = KeyOf(program);
            if (!best.TryGetValue(key, out var current))
            {
                order.Add(key);
                best[key] = program;
            }
            else if (Score(program) > Score(current))
            {
                best[key] = program;
            }
        }

        var kept = order.Select(key => best[key]).ToList();
        return new DedupeResult(kept, total - kept.Count);
    }

    private static string KeyOf(InstalledProgram p) =>
        string.Join('\u001F', p.Name.Trim(), (p.Version ?? string.Empty).Trim(), (p.Publisher ?? string.Empty).Trim());

    /// <summary>Prefers the entry that can actually be uninstalled and knows where it lives.</summary>
    private static int Score(InstalledProgram p) =>
        (p.UninstallString is null ? 0 : 2) + (p.InstallLocation is null ? 0 : 1);
}
