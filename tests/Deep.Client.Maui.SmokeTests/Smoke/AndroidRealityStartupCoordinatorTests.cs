using System.Collections.Concurrent;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class AndroidRealityStartupCoordinatorTests
{
    [Fact]
    public async Task FailedStartupCanRetryWithBoundedExponentialBackoff()
    {
        var attempts = 0;
        var restartFlags = new List<bool>();
        var delays = new List<TimeSpan>();
        await using var coordinator = new RealityStartupCoordinator(
            (restart, _) =>
            {
                restartFlags.Add(restart);
                attempts++;
                return attempts < 5
                    ? Task.FromException(new InvalidOperationException($"attempt {attempts}"))
                    : Task.CompletedTask;
            },
            static (_, _) => Task.FromResult(false),
            initialRetryDelay: TimeSpan.FromMilliseconds(10),
            maximumRetryDelay: TimeSpan.FromMilliseconds(25),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureStartedAsync());
        }

        await coordinator.EnsureStartedAsync();

        Assert.Equal(new[] { false, true, true, true, true }, restartFlags);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(25)
            },
            delays);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneStartupAttempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var coordinator = new RealityStartupCoordinator(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref attempts);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
            static (_, _) => Task.FromResult(false),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => Task.CompletedTask);

        var first = coordinator.EnsureStartedAsync();
        await started.Task;
        var second = coordinator.EnsureStartedAsync();
        var third = coordinator.EnsureStartedAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, Volatile.Read(ref attempts));

        release.SetResult();
        await Task.WhenAll(first, second, third);
    }

    [Fact]
    public async Task ReadinessProbesOnlyTheRequestedListener()
    {
        var probedPorts = new List<int>();
        var probeCount = 0;
        var startupAttempts = 0;
        await using var coordinator = new RealityStartupCoordinator(
            (_, _) =>
            {
                startupAttempts++;
                return Task.CompletedTask;
            },
            (port, _) =>
            {
                probedPorts.Add(port);
                probeCount++;
                return Task.FromResult(probeCount == 2);
            },
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => throw new InvalidOperationException("Polling was not expected."));

        await coordinator.WaitUntilReadyAsync(17892, CancellationToken.None);

        Assert.Equal(1, startupAttempts);
        Assert.Equal(new[] { 17892, 17892 }, probedPorts);
    }

    [Fact]
    public async Task CachedListenerFailureRestartsExactlyOnceWithinTheSameReadinessOperation()
    {
        var listenerReady = true;
        var restartFlags = new List<bool>();
        await using var coordinator = new RealityStartupCoordinator(
            (restart, _) =>
            {
                restartFlags.Add(restart);
                listenerReady = true;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult(listenerReady),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => Task.CompletedTask);

        await coordinator.EnsureStartedAsync();
        listenerReady = false;

        await coordinator.WaitUntilReadyAsync(17892, CancellationToken.None);

        Assert.Equal(new[] { false, true }, restartFlags);
    }

    [Fact]
    public async Task ConcurrentReadinessRecoverySharesOneBoundedRestart()
    {
        var listenerReady = 1;
        var restartCount = 0;
        var restartStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new RealityStartupCoordinator(
            async (restart, cancellationToken) =>
            {
                if (!restart)
                {
                    return;
                }

                Interlocked.Increment(ref restartCount);
                restartStarted.TrySetResult();
                await releaseRestart.Task.WaitAsync(cancellationToken);
                Volatile.Write(ref listenerReady, 1);
            },
            (_, _) => Task.FromResult(Volatile.Read(ref listenerReady) != 0),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, cancellationToken) => Task.Delay(1, cancellationToken));

        await coordinator.EnsureStartedAsync();
        Volatile.Write(ref listenerReady, 0);

        var first = coordinator.WaitUntilReadyAsync(17892, CancellationToken.None);
        var second = coordinator.WaitUntilReadyAsync(17892, CancellationToken.None);
        await restartStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref restartCount));
        releaseRestart.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref restartCount));
    }

    [Theory]
    [InlineData("http://127.0.0.1:17892/api/session/rpc", 17892, true)]
    [InlineData("http://localhost:17892/api/session/rpc", 17892, true)]
    [InlineData("http://127.0.0.1:17891/api/session/rpc", 17892, false)]
    [InlineData("http://192.0.2.10:17892/api/session/rpc", 17892, false)]
    [InlineData("https://127.0.0.1:17892/api/session/rpc", 17892, false)]
    public void RouteMatchingRequiresTheExactLoopbackListener(string url, int port, bool expected)
    {
        Assert.Equal(expected, RealityStartupCoordinator.TargetsListener(new Uri(url), port));
    }

    [Fact]
    public async Task ConcurrentRestartRequestsCoalesceAfterActiveStartup()
    {
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartFlags = new ConcurrentQueue<bool>();
        var attempts = 0;
        await using var coordinator = new RealityStartupCoordinator(
            async (restart, cancellationToken) =>
            {
                restartFlags.Enqueue(restart);
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    normalStarted.TrySetResult();
                    await releaseNormal.Task.WaitAsync(cancellationToken);
                    return;
                }

                restartStarted.TrySetResult();
                await releaseRestart.Task.WaitAsync(cancellationToken);
            },
            static (_, _) => Task.FromResult(false),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => Task.CompletedTask);

        var initialStartup = coordinator.EnsureStartedAsync();
        await normalStarted.Task;
        var firstRestart = coordinator.RestartAsync();
        var secondRestart = coordinator.RestartAsync();

        releaseNormal.SetResult();
        await restartStarted.Task;

        Assert.Equal(2, Volatile.Read(ref attempts));
        releaseRestart.SetResult();
        await Task.WhenAll(initialStartup, firstRestart, secondRestart);
        Assert.Equal(new[] { false, true }, restartFlags);
    }

    [Fact]
    public async Task DisposalCancelsSharedStartupAndRejectsFurtherWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RealityStartupCoordinator(
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            static (_, _) => Task.FromResult(false),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));

        var startup = coordinator.EnsureStartedAsync();
        await started.Task;
        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = coordinator.EnsureStartedAsync();
        });
    }
}
