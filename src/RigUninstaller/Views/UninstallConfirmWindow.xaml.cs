using System.Windows;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Views;

public partial class UninstallConfirmWindow : Window
{
    public UninstallConfirmWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is UninstallConfirmViewModel viewModel)
            {
                viewModel.Accepted += () => DialogResult = true;
                viewModel.Cancelled += () => DialogResult = false;
            }
        };
    }
}
