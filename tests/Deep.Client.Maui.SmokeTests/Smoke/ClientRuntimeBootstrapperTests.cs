using Deep.Client.Maui.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ClientRuntimeBootstrapperTests
{
    [Fact]
    public async Task FailedInitializationCanBeRetried()
    {
        var attempts = 0;
        await using var bootstrapper = new ClientRuntimeBootstrapper(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Task.FromException<ClientRuntime>(new InvalidOperationException("first attempt"));
            }

            return Task.FromResult(ClientRuntime.CreateStubbed());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrapper.InitializeAsync());
        Assert.Equal(ClientRuntimeBootstrapState.Failed, bootstrapper.State);
        Assert.True(bootstrapper.IsRetryable);

        var runtime = await bootstrapper.RetryAsync();

        Assert.Same(runtime, bootstrapper.GetRequiredRuntime());
        Assert.Equal(ClientRuntimeBootstrapState.Ready, bootstrapper.State);
        Assert.False(bootstrapper.IsRetryable);
    }

    [Fact]
    public async Task CancellationDisposesRuntimeReturnedAfterCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdRuntime = ClientRuntime.CreateStubbed();
        await using var bootstrapper = new ClientRuntimeBootstrapper(async _ =>
        {
            started.SetResult();
            await release.Task;
            return createdRuntime;
        });

        var attempt = bootstrapper.InitializeAsync();
        await started.Task;
        bootstrapper.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(ClientRuntimeBootstrapState.Cancelled, bootstrapper.State);
        Assert.True(createdRuntime.IsDisposed);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedInitialization()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bootstrapper = new ClientRuntimeBootstrapper(async cancellationToken =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ClientRuntime.CreateStubbed();
        });
        using var firstCaller = new CancellationTokenSource();

        var first = bootstrapper.InitializeAsync(firstCaller.Token);
        await started.Task;
        var second = bootstrapper.InitializeAsync();
        firstCaller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(ClientRuntimeBootstrapState.Initializing, bootstrapper.State);
        Assert.False(second.IsCompleted);

        release.SetResult();
        var runtime = await second;

        Assert.Same(runtime, bootstrapper.GetRequiredRuntime());
        Assert.Equal(ClientRuntimeBootstrapState.Ready, bootstrapper.State);
    }

    [Fact]
    public async Task DisposalDuringInitializationCannotPublishOrLeakRuntime()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdRuntime = ClientRuntime.CreateStubbed();
        var bootstrapper = new ClientRuntimeBootstrapper(async _ =>
        {
            started.SetResult();
            await release.Task;
            return createdRuntime;
        });

        var attempt = bootstrapper.InitializeAsync();
        await started.Task;
        var disposal = bootstrapper.DisposeAsync().AsTask();
        release.SetResult();

        await disposal;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.True(createdRuntime.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = bootstrapper.InitializeAsync();
        });
    }
}
