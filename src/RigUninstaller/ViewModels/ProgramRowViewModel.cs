using System.Globalization;
using System.Windows.Media;
using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>One row of the program list: the program plus display text, tags and its (lazily loaded) icon.</summary>
public sealed class ProgramRowViewModel : ViewModelBase
{
    // Muted tile colours that keep white letters readable in light and dark themes.
    private static readonly string[] TilePalette =
    {
        "#3E6A9E", "#6F4F8F", "#2F7A66", "#A0561A", "#9A3F55", "#4F7030", "#2F6F8F", "#7D6215",
    };

    private readonly Action? _onChanged;
    private ImageSource? _icon;
    private bool _isChecked;
    private bool _isKept;
    private bool _isBloat;
    private ProgramInsight? _insight;
    private string _wingetText = string.Empty;

    public ProgramRowViewModel(InstalledProgram program, Action? onChanged = null)
    {
        Program = program;
        _onChanged = onChanged;
        Initial = FindInitial(program.Name);
        TileColor = TilePalette[StableIndex(program.Name, TilePalette.Length)];
        Risk = RiskAdvisor.Assess(program);
    }

    public InstalledProgram Program { get; }

    /// <summary>Why removing this program can lag or crash the PC (drivers, runtimes, security software...), or null.</summary>
    public RiskInfo? Risk { get; }

    public bool HasRisk => Risk is not null;

    public bool IsDanger => Risk?.Level == RiskLevel.Danger;

    public string RiskLabel => Risk?.Label ?? string.Empty;

    public string RiskReason => Risk?.Reason ?? string.Empty;

    public string Name => Program.Name;

    public string Publisher => Program.Publisher ?? string.Empty;

    public string Version => Program.Version ?? string.Empty;

    public string SizeText => SizeFormatter.Format(Program.SizeBytes);

    /// <summary>"C:", "F:"... or a dash when the drive cannot be told.</summary>
    public string DriveText => Program.Drive == DiskUsage.UnknownDrive ? SizeFormatter.Unknown : Program.Drive;

    public string InstallDateText =>
        Program.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? SizeFormatter.Unknown;

    /// <summary>Letter shown on the fallback tile when the program has no icon.</summary>
    public string Initial { get; }

    public string TileColor { get; }

    public bool HasIcon => _icon is not null;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    /// <summary>Ticked for a batch uninstall.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public bool IsKept
    {
        get => _isKept;
        set
        {
            if (SetProperty(ref _isKept, value))
            {
                OnPropertyChanged(nameof(CanTick));
                OnTagsChanged();
            }
        }
    }

    /// <summary>Programs marked Keep cannot be ticked for a batch.</summary>
    public bool CanTick => !_isKept;

    public bool IsBloat
    {
        get => _isBloat;
        set
        {
            if (SetProperty(ref _isBloat, value))
            {
                OnTagsChanged();
            }
        }
    }

    /// <summary>Filled in after the scan by a background pass (signature, last used, orphan check).</summary>
    public ProgramInsight? Insight
    {
        get => _insight;
        set
        {
            if (SetProperty(ref _insight, value))
            {
                OnPropertyChanged(nameof(SignedByText));
                OnPropertyChanged(nameof(IsUnsigned));
                OnPropertyChanged(nameof(LastUsedText));
                OnPropertyChanged(nameof(OrphanReason));
                OnTagsChanged();
            }
        }
    }

    public string? OrphanReason => _insight?.OrphanReason;

    public bool IsOrphan => OrphanReason is not null;

    public string SignedByText => _insight?.Signature switch
    {
        null => string.Empty,
        { State: SignatureState.Signed } s => s.Signer ?? "Signed",
        { State: SignatureState.Invalid } s => $"{s.Signer} (not trusted)",
        { State: SignatureState.Unsigned } => "Unsigned",
        var s => s.Signer ?? string.Empty,
    };

    /// <summary>Unsigned or untrusted uninstallers are highlighted.</summary>
    public bool IsUnsigned => _insight?.Signature.State is SignatureState.Unsigned or SignatureState.Invalid;

    /// <summary>The date, with a "~" when it only comes from the file's access time (other programs and scanners touch files too).</summary>
    public string LastUsedText
    {
        get
        {
            if (_insight is null)
            {
                return string.Empty;
            }

            if (_insight.LastUsed.When is not { } when)
            {
                return SizeFormatter.Unknown;
            }

            var exact = _insight.LastUsed.Source.Contains("Prefetch", StringComparison.OrdinalIgnoreCase);
            return (exact ? string.Empty : "~") + when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    public string LastUsedSource => _insight?.LastUsed.Source ?? string.Empty;

    /// <summary>"Update 1.2 to 1.3", "winget" or empty. Filled by the winget check.</summary>
    public string WingetText
    {
        get => _wingetText;
        set => SetProperty(ref _wingetText, value);
    }

    public string Tags => string.Join(", ", TagList());

    private IEnumerable<string> TagList()
    {
        if (Risk is { } risk) yield return risk.Label;
        if (IsKept) yield return "Keep";
        if (IsBloat) yield return "Bloatware";
        if (IsOrphan) yield return "Broken entry";
        if (IsUnsigned) yield return "Unsigned";
    }

    private void OnTagsChanged()
    {
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(IsOrphan));
    }

    private static string FindInitial(string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }
        }

        return "?";
    }

    // string.GetHashCode changes between runs, so a tile would change colour on every start.
    private static int StableIndex(string text, int length)
    {
        var sum = 0;
        foreach (var c in text.ToLowerInvariant())
        {
            sum = unchecked(sum * 31 + c);
        }

        return Math.Abs(sum % length);
    }
}
