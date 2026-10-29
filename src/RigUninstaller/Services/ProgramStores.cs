using System.Text.Json;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Programs the user marked "Keep": they cannot be uninstalled or force-removed by accident.</summary>
public interface IKeepStore
{
    bool IsKept(InstalledProgram program);

    void SetKept(InstalledProgram program, bool kept);
}

/// <summary>Keeps the "Keep" tags in a small JSON file, keyed by the program's registry key.</summary>
public sealed class KeepStore : IKeepStore
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly IActionLog _log;
    private HashSet<string>? _kept;

    public KeepStore(string file, IActionLog log)
    {
        _file = file;
        _log = log;
    }

    public bool IsKept(InstalledProgram program)
    {
        lock (_gate)
        {
            return Load().Contains(program.RegistryKeyPath);
        }
    }

    public void SetKept(InstalledProgram program, bool kept)
    {
        lock (_gate)
        {
            var set = Load();
            var changed = kept ? set.Add(program.RegistryKeyPath) : set.Remove(program.RegistryKeyPath);
            if (!changed)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(set.Order(StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }));
            _log.Info($"{(kept ? "Marked" : "Unmarked")} {program.Name} as Keep.");
        }
    }

    private HashSet<string> Load()
    {
        if (_kept is not null)
        {
            return _kept;
        }

        _kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_file))
        {
            return _kept;
        }

        try
        {
            foreach (var key in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new List<string>())
            {
                _kept.Add(key);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn($"Could not read the Keep list ({_file}): {ex.Message}");
        }

        return _kept;
    }
}

public interface IBloatwareList
{
    string FilePath { get; }

    bool IsBloat(InstalledProgram program);

    /// <summary>Reads the JSON file again (after the user edited it).</summary>
    void Reload();
}

/// <summary>
/// A JSON file the user can edit: <c>names</c> and <c>publishers</c> are pieces of text. A program matches when
/// its name or publisher contains one of them (any case). A starter file is written the first time.
/// </summary>
public sealed class BloatwareList : IBloatwareList
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IActionLog _log;
    private List<string> _names = new();
    private List<string> _publishers = new();

    public BloatwareList(string file, IActionLog log)
    {
        FilePath = file;
        _log = log;
        Reload();
    }

    public string FilePath { get; }

    public bool IsBloat(InstalledProgram program) =>
        _names.Any(n => program.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
        || (program.Publisher is { } publisher && _publishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase)));

    public void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Starter, Options));
                _log.Info($"Wrote a starter bloatware list: {FilePath}");
            }

            var definition = JsonSerializer.Deserialize<BloatwareDefinition>(File.ReadAllText(FilePath), Options) ?? new BloatwareDefinition();
            _names = Clean(definition.Names);
            _publishers = Clean(definition.Publishers);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file must not stop the app: nothing is highlighted until it is fixed.
            _log.Warn($"Could not read the bloatware list ({FilePath}): {ex.Message}");
            _names = new List<string>();
            _publishers = new List<string>();
        }
    }

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? Array.Empty<string>()).Select(i => i.Trim()).Where(i => i.Length >= 3).ToList();

    private sealed class BloatwareDefinition
    {
        public List<string> Names { get; set; } = new();

        public List<string> Publishers { get; set; } = new();
    }

    private static readonly BloatwareDefinition Starter = new()
    {
        Names = new List<string>
        {
            "McAfee", "Norton", "WildTangent", "Candy Crush", "Booking.com", "ExpressVPN", "Dropbox promotion",
            "Spotify Music", "TikTok", "Disney+", "Amazon Prime", "Netflix", "Hulu", "Xbox Game Bar Plugin",
            "Dell SupportAssist", "HP Support Assistant", "Lenovo Vantage", "Candy", "Farmville", "Bubble Witch",
        },
        Publishers = new List<string> { "WildTangent", "Gameloft", "King.com" },
    };
}
