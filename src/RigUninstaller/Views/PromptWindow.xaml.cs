using System.Windows;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Views;

public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is PromptViewModel viewModel)
            {
                viewModel.Chosen += OnChosen;
            }
        };
    }

    /// <summary>The key of the button that was clicked; null when the window was closed with X.</summary>
    public string? ResultKey { get; private set; }

    private void OnChosen(string key)
    {
        ResultKey = key;
        DialogResult = true;
    }
}
