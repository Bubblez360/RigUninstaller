using RigUninstaller.Models;
using RigUninstaller.Services;

namespace RigUninstaller.ViewModels;

/// <summary>Backs the "Uninstall X?" dialog: shows the exact command and the options.</summary>
public sealed class UninstallConfirmViewModel : ViewModelBase
{
    private readonly UninstallPlans _plans;
    private bool _quiet;
    private bool _createRestorePoint = true;
    private bool _scanWithDefender;

    public UninstallConfirmViewModel(InstalledProgram program, UninstallPlans plans, bool defenderAvailable = false)
    {
        Program = program;
        _plans = plans;
        DefenderAvailable = defenderAvailable;

        // With only a quiet command available there is no choice to make.
        _quiet = plans.Normal is null;

        ConfirmCommand = new RelayCommand(_ => Accepted?.Invoke());
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
    }

    public event Action? Accepted;

    public event Action? Cancelled;

    public InstalledProgram Program { get; }

    public string Heading => $"Uninstall {Program.Name}?";

    public string Details => string.Join("  ·  ", new[] { Program.Version, Program.Publisher }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public bool CanToggleQuiet => _plans.Normal is not null && _plans.Quiet is not null;

    public bool QuietUnavailable => _plans.Quiet is null;

    public bool DefenderAvailable { get; }

    public bool Quiet
    {
        get => _quiet;
        set
        {
            if (SetProperty(ref _quiet, value))
            {
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => SetProperty(ref _createRestorePoint, value);
    }

    public bool ScanWithDefender
    {
        get => _scanWithDefender;
        set => SetProperty(ref _scanWithDefender, value);
    }

    public string CommandPreview => (Quiet ? _plans.Quiet : _plans.Normal ?? _plans.Quiet)?.DisplayCommand ?? string.Empty;

    public UninstallChoice Choice => new(Quiet, CreateRestorePoint, DefenderAvailable && ScanWithDefender);

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }
}

public sealed record PromptButton(
    string Label,
    string Key,
    bool IsPrimary = false,
    bool IsDefault = false,
    bool IsCancel = false,
    bool NeedsRequiredOptions = false)
{
    /// <summary>"primary" makes the button use the accent colour (see the Tag trigger in Controls.xaml).</summary>
    public string? StyleTag => IsPrimary ? "primary" : null;
}

/// <summary>A tick box in a prompt. A <see cref="Required"/> one must be ticked before a guarded button works.</summary>
public sealed record PromptOption(string Text, bool Required = false, bool IsChecked = false);

public sealed class PromptOptionViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _isChecked;

    public PromptOptionViewModel(PromptOption option, Action onChanged)
    {
        Text = option.Text;
        Required = option.Required;
        _isChecked = option.IsChecked;
        _onChanged = onChanged;
    }

    public string Text { get; }

    public bool Required { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onChanged();
            }
        }
    }
}

/// <summary>Backs the generic message / question window.</summary>
public sealed class PromptViewModel : ViewModelBase
{
    public PromptViewModel(
        string title,
        string heading,
        string body,
        IReadOnlyList<string> items,
        IReadOnlyList<PromptButton> buttons,
        IReadOnlyList<PromptOption>? options = null,
        bool isWarning = false)
    {
        Title = title;
        Heading = heading;
        Body = body;
        Items = items;
        Buttons = buttons;
        IsWarning = isWarning;
        Options = (options ?? Array.Empty<PromptOption>()).Select(o => new PromptOptionViewModel(o, OnOptionChanged)).ToList();
        CancelKey = (buttons.FirstOrDefault(b => b.IsCancel) ?? buttons[^1]).Key;
        ChooseCommand = new RelayCommand(
            key =>
            {
                if (key is string text)
                {
                    Chosen?.Invoke(text);
                }
            },
            key => CanChoose(key as string));
    }

    public event Action<string>? Chosen;

    public string Title { get; }

    public string Heading { get; }

    public string Body { get; }

    public IReadOnlyList<string> Items { get; }

    public IReadOnlyList<PromptButton> Buttons { get; }

    public IReadOnlyList<PromptOptionViewModel> Options { get; }

    public bool HasItems => Items.Count > 0;

    public bool HasOptions => Options.Count > 0;

    /// <summary>Draws the heading in the warning colour.</summary>
    public bool IsWarning { get; }

    /// <summary>The answer used when the window is closed with the X button.</summary>
    public string CancelKey { get; }

    public RelayCommand ChooseCommand { get; }

    private bool CanChoose(string? key)
    {
        var button = Buttons.FirstOrDefault(b => b.Key == key);
        return button is null || !button.NeedsRequiredOptions || Options.Where(o => o.Required).All(o => o.IsChecked);
    }

    private void OnOptionChanged() => ChooseCommand.RaiseCanExecuteChanged();
}
