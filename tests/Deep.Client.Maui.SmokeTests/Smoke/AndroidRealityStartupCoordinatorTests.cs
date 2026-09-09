using System.Collections.Concurrent;

namespace Deep.Client.Maui.SmokeTests.Smoke;

[CollectionDefinition("Reality startup coordinator", DisableParallelization = true)]
public sealed class RealityStartupCoordinatorCollection;

[Collection("Reality startup coordinator")]
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

    [Fact]
    public async Task NetworkInvalidationDoesNotStartBackgroundWorkButForcesNextForegroundStart()
    {
        var starts = new List<bool>();
        await using var coordinator = new RealityStartupCoordinator(
            (restart, _) =>
            {
                starts.Add(restart);
                return Task.CompletedTask;
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => Task.CompletedTask);

        await coordinator.EnsureStartedAsync();
        Assert.True(coordinator.TryInvalidateReadiness());

        Assert.Equal(new[] { false }, starts);

        await coordinator.EnsureStartedAsync();
        Assert.Equal(new[] { false, true }, starts);
    }

    [Fact]
    public async Task NetworkInvalidationForcesRestartBeforeReusingAnOtherwiseHealthyListener()
    {
        var starts = new List<bool>();
        await using var coordinator = new RealityStartupCoordinator(
            (restart, _) =>
            {
                starts.Add(restart);
                return Task.CompletedTask;
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            delayAsync: static (_, _) => Task.CompletedTask);

        await coordinator.EnsureStartedAsync();
        Assert.True(coordinator.TryInvalidateReadiness());

        await coordinator.WaitUntilReadyAsync(17892, CancellationToken.None);

        Assert.Equal(new[] { false, true }, starts);
    }

    [Fact]
    public async Task BackgroundProbeUsesOnlyAnAlreadyHealthyListenerAndNeverStartsRecovery()
    {
        var starts = 0;
        await using var coordinator = new RealityStartupCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref starts);
                return Task.CompletedTask;
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1));

        Assert.False(await coordinator.TryUseExistingListenerAsync(17892, CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref starts));

        await coordinator.EnsureStartedAsync();
        Assert.True(await coordinator.TryUseExistingListenerAsync(17892, CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref starts));

        Assert.True(coordinator.TryInvalidateReadiness());
        Assert.False(await coordinator.TryUseExistingListenerAsync(17892, CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task DisposalIsBoundedWhenStartupIgnoresCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RealityStartupCoordinator(
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            static (_, _) => Task.FromResult(false),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            disposalWaitTimeout: TimeSpan.FromMilliseconds(20));

        var startup = coordinator.EnsureStartedAsync();
        await entered.Task;
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
    }

    [Fact]
    public async Task InvalidationRacingDisposalNeverThrowsFromPlatformCallbackPath()
    {
        var coordinator = new RealityStartupCoordinator(
            static (_, _) => Task.CompletedTask,
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1));
        await coordinator.EnsureStartedAsync();

        var invalidations = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                _ = coordinator.TryInvalidateReadiness();
            }
        });
        await Task.WhenAll(invalidations, coordinator.DisposeAsync().AsTask());

        Assert.False(coordinator.TryInvalidateReadiness());
    }

    [Fact]
    public void NativeStartupErrorIsNeverCopiedIntoPersistedExceptionMaterial()
    {
        const string raw = "connect 203.0.113.25; id=00000000-0000-4000-8000-000000000001; password=secret";

        var exception = RealityTransportFailure.CreateSanitizedStartupException(raw);

        Assert.DoesNotContain("203.0.113.25", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("00000000-0000-4000-8000-000000000001", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundLifecycleCancelsOldGenerationAcrossPauseResumeRace()
    {
        using var lifecycle = new RealityForegroundLifecycle();
        lifecycle.SetForeground(true);
        using var oldLease = lifecycle.Capture(CancellationToken.None);

        lifecycle.SetForeground(false);
        lifecycle.SetForeground(true);

        Assert.True(oldLease.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() =>
            lifecycle.Validate(oldLease.Generation, CancellationToken.None));
        using var currentLease = lifecycle.Capture(CancellationToken.None);
        lifecycle.Validate(currentLease.Generation, CancellationToken.None);
    }

    [Fact]
    public async Task PausingRecoveryCancelsNonCooperativeAttemptAndPreventsLateSuccessPublication()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var coordinator = new RealityStartupCoordinator(
            async (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1));

        var first = coordinator.EnsureStartedAsync();
        await firstStarted.Task;
        Assert.True(coordinator.TrySetRecoveryEnabled(false));
        releaseFirst.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.EnsureStartedAsync());
        Assert.Equal(1, Volatile.Read(ref attempts));

        Assert.True(coordinator.TrySetRecoveryEnabled(true));
        await coordinator.EnsureStartedAsync();
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task BackgroundInvalidationWinsWhenNativeStartReturnsBeforeCancellationIsDelivered()
    {
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        CancellationTokenSource? cancellationDelivered = null;
        await using var coordinator = new RealityStartupCoordinator(
            async (_, _) =>
            {
                nativeStarted.TrySetResult();
                await releaseNative.Task;
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            invalidatedSuccessCleanupAsync: () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                cleanupStarted.TrySetResult();
                return cancellationIssued.Task;
            },
            cancelAttempt: source =>
            {
                cancellationDelivered = source;
                releaseNative.TrySetResult();
                cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                source.Cancel();
                cancellationIssued.TrySetResult();
            });

        var startup = coordinator.EnsureStartedAsync();
        await nativeStarted.Task;

        Assert.True(coordinator.TrySetRecoveryEnabled(false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.NotNull(cancellationDelivered);
        Assert.True(cancellationDelivered!.IsCancellationRequested);
        Assert.Equal(1, Volatile.Read(ref cleanupCalls));
    }

    [Fact]
    public async Task RepeatingEnabledStateDoesNotInvalidateActiveStartupGeneration()
    {
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        await using var coordinator = new RealityStartupCoordinator(
            async (_, _) =>
            {
                nativeStarted.TrySetResult();
                await releaseNative.Task;
            },
            static (_, _) => Task.FromResult(true),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maximumRetryDelay: TimeSpan.FromMilliseconds(4),
            listenerPollInterval: TimeSpan.FromMilliseconds(1),
            invalidatedSuccessCleanupAsync: () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                return Task.CompletedTask;
            });

        var startup = coordinator.EnsureStartedAsync();
        await nativeStarted.Task;

        Assert.True(coordinator.TrySetRecoveryEnabled(true));
        releaseNative.TrySetResult();

        await startup;
        Assert.Equal(0, Volatile.Read(ref cleanupCalls));
    }
}
