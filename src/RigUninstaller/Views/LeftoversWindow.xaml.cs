using System.Windows;
using RigUninstaller.ViewModels;

namespace RigUninstaller.Views;

public partial class LeftoversWindow : Window
{
    public LeftoversWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is LeftoversViewModel viewModel)
            {
                viewModel.Accepted += () => DialogResult = true;
                viewModel.Closed += () => DialogResult = false;
            }
        };
    }
}
