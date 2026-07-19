using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC13Tests
{
    [Theory]
    [InlineData("suppressed")]
    [InlineData("unsafe")]
    [InlineData("thread")]
    public async Task UnsubscribeCanAwaitFlowlessNestedCoordinatorDispose(
        string dispatch)
    {
        var platform = new C13Platform();
        var coordinator = new NearbyPolicyCoordinator(
            new C13Radio(),
            platform,
            new C13Clock());
        Task<Exception?>? nestedDispose = null;
        var nestedReturnedInsideCallback = false;
        platform.OnUnsubscribe = () =>
        {
            var completion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            nestedDispose = completion.Task;
            void InvokeNested() => completion.TrySetResult(
                Record.Exception(() =>
                    coordinator.DisposeAsync().AsTask()
                        .GetAwaiter().GetResult()));

            switch (dispatch)
            {
                case "suppressed":
                    using (ExecutionContext.SuppressFlow())
                    {
                        _ = Task.Run(InvokeNested);
                    }

                    break;
                case "unsafe":
                    ThreadPool.UnsafeQueueUserWorkItem(
                        _ => InvokeNested(),
                        state: null);
                    break;
                case "thread":
                    var thread = new Thread(InvokeNested);
                    using (ExecutionContext.SuppressFlow())
                    {
                        thread.Start();
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(dispatch));
            }

            nestedReturnedInsideCallback = completion.Task.Wait(
                TimeSpan.FromMilliseconds(500));
        };

        await Task.Run(async () =>
            await coordinator.DisposeAsync()).WaitAsync(
            TimeSpan.FromSeconds(2));
        var nestedFailure = await nestedDispose!.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.True(nestedReturnedInsideCallback);
        Assert.Null(nestedFailure);
        Assert.Equal(1, platform.UnsubscribeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDisposeReturnsWhileOnlyOwnerObservesFailure(
        bool failOwner)
    {
        var platform = new C13Platform();
        var coordinator = new NearbyPolicyCoordinator(
            new C13Radio(),
            platform,
            new C13Clock());
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        platform.OnUnsubscribe = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (failOwner && platform.UnsubscribeCalls == 1)
            {
                throw new InvalidOperationException(
                    "coordinator-dispose-secret");
            }
        };

        var owner = Task.Run(async () =>
            await Record.ExceptionAsync(() =>
                coordinator.DisposeAsync().AsTask()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var concurrent = coordinator.DisposeAsync().AsTask();
        Exception? concurrentFailure;
        try
        {
            concurrentFailure = await Record.ExceptionAsync(() =>
                concurrent.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            release.TrySetResult();
        }

        var ownerFailure = await owner.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(concurrentFailure);
        if (failOwner)
        {
            var transition =
                Assert.IsType<NearbyRadioTransitionException>(ownerFailure);
            Assert.Equal(
                NearbyRadioTransitionError.StateReadFailed,
                transition.Error);
            Assert.DoesNotContain(
                "coordinator-dispose-secret",
                transition.ToString(),
                StringComparison.OrdinalIgnoreCase);
            platform.OnUnsubscribe = null;
            await coordinator.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(2, platform.UnsubscribeCalls);
        }
        else
        {
            Assert.Null(ownerFailure);
            Assert.Equal(1, platform.UnsubscribeCalls);
        }
    }

    [Fact]
    public async Task SubscriptionClosesBeforeBlockedTransitionDrain()
    {
        var platform = new C13Platform();
        var radio = new C13Radio();
        var coordinator = new NearbyPolicyCoordinator(
            radio,
            platform,
            new C13Clock());
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        platform.BlockSnapshot = true;
        platform.RaiseChanged();
        await platform.SnapshotEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var disposing = coordinator.DisposeAsync().AsTask();
        Exception? boundedFailure;
        try
        {
            boundedFailure = await Record.ExceptionAsync(async () =>
            {
                await radio.StopEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
                await platform.UnsubscribeEntered.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(500));
            });
        }
        finally
        {
            platform.ReleaseSnapshot();
        }

        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(boundedFailure);
        Assert.Equal(1, radio.StopCalls);
        Assert.Equal(1, platform.UnsubscribeCalls);
    }

    [Fact]
    public async Task ContinuousStaleEventsCannotStarveFinalDrain()
    {
        var platform = new C13Platform();
        var coordinator = new NearbyPolicyCoordinator(
            new C13Radio(),
            platform,
            new C13Clock());
        var hookProperty = typeof(NearbyPolicyCoordinator).GetProperty(
            "DrainBeforeStabilityCheckForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hookProperty);
        using var stopFlood = new ManualResetEventSlim();
        using var floodStarted = new ManualResetEventSlim();
        Thread? flood = null;
        var injectedEvents = 0;
        var stabilityChecks = 0;
        hookProperty.SetValue(coordinator, new Action(() =>
        {
            Interlocked.Increment(ref stabilityChecks);
            if (flood is not null)
            {
                return;
            }

            flood = new Thread(() =>
            {
                while (!stopFlood.IsSet)
                {
                    platform.RaiseCaptured();
                    Interlocked.Increment(ref injectedEvents);
                    floodStarted.Set();
                    Thread.Sleep(1);
                }
            });
            using (ExecutionContext.SuppressFlow())
            {
                flood.Start();
            }

            Assert.True(floodStarted.Wait(TimeSpan.FromSeconds(1)));
        }));

        try
        {
            await coordinator.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            stopFlood.Set();
            Assert.True(flood?.Join(TimeSpan.FromSeconds(1)) ?? true);
        }

        Assert.InRange(stabilityChecks, 1, 2);
        Assert.InRange(injectedEvents, 1, 2_000);
        Assert.Equal(1, platform.UnsubscribeCalls);
    }

    [Fact]
    public async Task CapturedEventsAfterDisposeAreIgnored()
    {
        var platform = new C13Platform();
        var coordinator = new NearbyPolicyCoordinator(
            new C13Radio(),
            platform,
            new C13Clock());
        var transitionField = typeof(NearbyPolicyCoordinator).GetField(
            "lastPlatformTransition",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(transitionField);

        await coordinator.DisposeAsync();
        var transition = transitionField.GetValue(coordinator);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            platform.RaiseCaptured();
        }

        Assert.Same(transition, transitionField.GetValue(coordinator));
        Assert.Equal(0, platform.SnapshotReads);
    }

    private sealed class C13Radio : INearbyRadioAdapter
    {
        public int StopCalls { get; private set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            StopCalls++;
            StopEntered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class C13Platform : INearbyPlatformState
    {
        private readonly ManualResetEventSlim snapshotRelease = new();
        private EventHandler<NearbyPlatformSnapshot>? changed;
        private EventHandler<NearbyPlatformSnapshot>? captured;

        public Action? OnUnsubscribe { get; set; }
        public bool BlockSnapshot { get; set; }
        public int SnapshotReads { get; private set; }
        public int UnsubscribeCalls { get; private set; }
        public TaskCompletionSource SnapshotEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnsubscribeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyPlatformSnapshot Snapshot
        {
            get
            {
                SnapshotReads++;
                if (BlockSnapshot)
                {
                    SnapshotEntered.TrySetResult();
                    snapshotRelease.Wait();
                }

                return Current;
            }
        }

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            changed += handler;
            captured = handler;
            subscription = new NearbyPlatformSubscription(() =>
            {
                UnsubscribeCalls++;
                UnsubscribeEntered.TrySetResult();
                OnUnsubscribe?.Invoke();
                changed -= handler;
            });
            return true;
        }

        public void RaiseChanged() => changed?.Invoke(this, Current);

        public void RaiseCaptured() => captured?.Invoke(this, Current);

        public void ReleaseSnapshot() => snapshotRelease.Set();

        private static NearbyPlatformSnapshot Current { get; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);
    }

    private sealed class C13Clock : INearbyClock
    {
        public DateTimeOffset UtcNow =>
            DateTimeOffset.Parse("2026-07-20T00:00:00Z");
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
