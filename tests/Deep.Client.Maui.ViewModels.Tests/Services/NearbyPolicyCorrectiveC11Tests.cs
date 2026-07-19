using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC11Tests
{
    [Fact]
    public void SameThreadReentrantDisposeJoinsThePublishedAttempt()
    {
        NearbyPlatformSubscription? subscription = null;
        var unsubscribeCalls = 0;
        subscription = new NearbyPlatformSubscription(() =>
        {
            if (Interlocked.Increment(ref unsubscribeCalls) == 1)
            {
                subscription!.Dispose();
            }
        });

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, unsubscribeCalls);
    }

    [Fact]
    public async Task CrossThreadDisposeSpawnedByUnsubscribeCannotDeadlock()
    {
        NearbyPlatformSubscription? subscription = null;
        Task? nestedDispose = null;
        var nestedReturnedInsideCallback = false;
        var unsubscribeCalls = 0;
        subscription = new NearbyPlatformSubscription(() =>
        {
            if (Interlocked.Increment(ref unsubscribeCalls) == 1)
            {
                nestedDispose = Task.Run(subscription!.Dispose);
                nestedReturnedInsideCallback = nestedDispose.Wait(
                    TimeSpan.FromMilliseconds(500));
            }
        });

        subscription.Dispose();
        await nestedDispose!.WaitAsync(TimeSpan.FromSeconds(2));
        subscription.Dispose();

        Assert.True(nestedReturnedInsideCallback);
        Assert.Equal(1, unsubscribeCalls);
    }

    [Fact]
    public async Task ConcurrentDisposeCallersJoinOneSuccessfulUnsubscribe()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeCalls = 0;
        var subscription = new NearbyPlatformSubscription(() =>
        {
            Interlocked.Increment(ref unsubscribeCalls);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });

        var owner = Task.Run(subscription.Dispose);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(subscription.Dispose))
            .ToArray();
        await Task.Delay(50);
        Assert.All(callers, caller => Assert.False(caller.IsCompleted));

        release.TrySetResult();
        await Task.WhenAll(callers.Append(owner)).WaitAsync(
            TimeSpan.FromSeconds(2));
        subscription.Dispose();

        Assert.Equal(1, unsubscribeCalls);
    }

    [Fact]
    public async Task FailedConcurrentAttemptIsSanitizedAndRetryable()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callersJoined = new CountdownEvent(4);
        var unsubscribeCalls = 0;
        var subscription = new NearbyPlatformSubscription(() =>
        {
            if (Interlocked.Increment(ref unsubscribeCalls) == 1)
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("unsubscribe-secret");
            }
        });
        var joinedHook = typeof(NearbyPlatformSubscription).GetProperty(
            "DisposeAttemptJoinedForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(joinedHook);
        joinedHook.SetValue(
            subscription,
            new Action(() => callersJoined.Signal()));

        var owner = Task.Run(() => Record.Exception(subscription.Dispose));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var callers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
                Record.Exception(subscription.Dispose)))
            .ToArray();
        Assert.True(callersJoined.Wait(TimeSpan.FromSeconds(2)));
        releaseFirst.TrySetResult();

        var failures = await Task.WhenAll(callers.Prepend(owner))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(failures, failure =>
        {
            var transition =
                Assert.IsType<NearbyRadioTransitionException>(failure);
            Assert.Equal(
                NearbyRadioTransitionError.StateReadFailed,
                transition.Error);
            Assert.DoesNotContain(
                "unsubscribe-secret",
                transition.ToString(),
                StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(1, unsubscribeCalls);

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(2, unsubscribeCalls);
    }

    [Fact]
    public async Task CoordinatorDisposeUsesRetryableCrossThreadSafeLease()
    {
        var platform = new C11Platform
        {
            ThrowFirstUnsubscribe = true
        };
        var coordinator = new NearbyPolicyCoordinator(
            new C11Radio(),
            platform,
            new C11Clock());

        var firstFailure = await Record.ExceptionAsync(() =>
            coordinator.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2)));

        var transition =
            Assert.IsType<NearbyRadioTransitionException>(firstFailure);
        Assert.Equal(
            NearbyRadioTransitionError.StateReadFailed,
            transition.Error);
        Assert.DoesNotContain(
            "coordinator-unsubscribe-secret",
            transition.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(platform.NestedDisposeReturned);
        Assert.Equal(1, platform.HandlerCount);
        Assert.Equal(1, platform.UnsubscribeCalls);

        await coordinator.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();

        Assert.Equal(0, platform.HandlerCount);
        Assert.Equal(2, platform.UnsubscribeCalls);
    }

    private sealed class C11Radio : INearbyRadioAdapter
    {
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(
            NearbyStopReason reason,
            CancellationToken cancellationToken)
        {
            _ = reason;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class C11Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;
        private NearbyPlatformSubscription? subscription;

        public bool ThrowFirstUnsubscribe { get; init; }
        public bool NestedDisposeReturned { get; private set; }
        public int UnsubscribeCalls { get; private set; }
        public int HandlerCount =>
            changed?.GetInvocationList().Length ?? 0;
        public NearbyPlatformSnapshot Snapshot { get; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? lease)
        {
            changed += handler;
            subscription = new NearbyPlatformSubscription(() =>
            {
                UnsubscribeCalls++;
                if (UnsubscribeCalls == 1)
                {
                    var nested = Task.Run(subscription!.Dispose);
                    NestedDisposeReturned = nested.Wait(
                        TimeSpan.FromMilliseconds(500));
                    if (ThrowFirstUnsubscribe)
                    {
                        throw new InvalidOperationException(
                            "coordinator-unsubscribe-secret");
                    }
                }

                changed -= handler;
            });
            lease = subscription;
            return true;
        }
    }

    private sealed class C11Clock : INearbyClock
    {
        public DateTimeOffset UtcNow =>
            DateTimeOffset.Parse("2026-07-20T00:00:00Z");
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
