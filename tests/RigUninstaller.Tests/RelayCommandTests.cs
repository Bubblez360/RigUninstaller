using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class RelayCommandTests
{
    [Fact]
    public void Execute_runs_action_with_parameter()
    {
        object? received = null;
        var command = new RelayCommand(p => received = p);

        command.Execute("hello");

        Assert.Equal("hello", received);
    }

    [Fact]
    public void CanExecute_defaults_to_true_without_predicate()
    {
        var command = new RelayCommand(_ => { });

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void CanExecute_uses_predicate()
    {
        var command = new RelayCommand(_ => { }, _ => false);

        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public void RaiseCanExecuteChanged_fires_event()
    {
        var command = new RelayCommand(_ => { });
        var fired = false;
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.True(fired);
    }
}

public class ViewModelBaseTests
{
    private sealed class Sample : ViewModelBase
    {
        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    [Fact]
    public void SetProperty_raises_PropertyChanged_when_value_changes()
    {
        var sample = new Sample();
        string? changed = null;
        sample.PropertyChanged += (_, e) => changed = e.PropertyName;

        sample.Name = "x";

        Assert.Equal("Name", changed);
    }

    [Fact]
    public void SetProperty_does_not_raise_when_value_is_same()
    {
        var sample = new Sample { Name = "x" };
        var count = 0;
        sample.PropertyChanged += (_, _) => count++;

        sample.Name = "x";

        Assert.Equal(0, count);
    }
}
