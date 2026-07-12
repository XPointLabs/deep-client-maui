using Deep.Client.Maui.Core.Commands;

namespace Deep.Client.Maui.ViewModels.Tests.Commands;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task ExecuteAsyncUsesSameGateAsICommandExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var command = new AsyncCommand(async cancellationToken =>
        {
            Interlocked.Increment(ref executions);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });

        var first = command.ExecuteAsync();
        await entered.Task;
        await command.ExecuteAsync();
        release.TrySetResult();
        await first;

        Assert.Equal(1, executions);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsyncReleasesGateWhenOperationFails()
    {
        var attempts = 0;
        var command = new AsyncCommand(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("failure"))
                : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync());
        await command.ExecuteAsync();

        Assert.Equal(2, attempts);
        Assert.True(command.CanExecute(null));
    }
}
