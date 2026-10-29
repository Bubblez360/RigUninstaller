using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_runs_the_delegate()
    {
        var ran = false;
        var command = new AsyncRelayCommand(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await command.ExecuteAsync(null);

        Assert.True(ran);
    }

    [Fact]
    public async Task Command_cannot_run_again_while_it_is_running()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var command = new AsyncRelayCommand(async _ =>
        {
            calls++;
            await gate.Task;
        });

        var first = command.ExecuteAsync(null);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync(null); // ignored while the first is still running
        gate.SetResult();
        await first;

        Assert.Equal(1, calls);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task CanExecuteChanged_fires_when_running_starts_and_ends()
    {
        var fired = 0;
        var command = new AsyncRelayCommand(_ => Task.CompletedTask);
        command.CanExecuteChanged += (_, _) => fired++;

        await command.ExecuteAsync(null);

        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task Exception_resets_running_state_and_propagates()
    {
        var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("fail"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(null));

        Assert.True(command.CanExecute(null));
    }
}
