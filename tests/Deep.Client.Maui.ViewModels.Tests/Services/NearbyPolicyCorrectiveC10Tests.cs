using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC10Tests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task StopOrDisposeWinsAfterPublicationBeforeQueuedLaunch(
        string trigger)
    {
        var environment = new C10Environment();
        var coordinator = environment.CreateCoordinator();
        var seam = typeof(NearbyPolicyCoordinator).GetProperty(
            "StartPublishedBeforeLaunchForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(seam);
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        seam.SetValue(coordinator, new Action(() =>
        {
            published.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }));

        var starting = Task.Run(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var winner = trigger == "stop"
            ? Task.Run(() => coordinator.StopAsync())
            : Task.Run(() => coordinator.DisposeAsync().AsTask());
        var winnerFailure = await Record.ExceptionAsync(() =>
            winner.WaitAsync(TimeSpan.FromSeconds(2)));
        release.TrySetResult();
        var startFailure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Null(winnerFailure);
        Assert.Null(startFailure);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(0, environment.Radio.StopCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        if (trigger == "stop")
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChangedRaisedByGuardedAdmissionGetterCannotPoisonDrain()
    {
        var environment = new C10Environment();
        var coordinator = environment.CreateCoordinator();
        environment.Platform.RaiseChangedFromGetter = true;

        var startFailure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        var drainFailure = await Record.ExceptionAsync(() =>
            coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        var snapshot = coordinator.Snapshot;
        await coordinator.DisposeAsync();

        Assert.IsType<NearbyCoordinatorBusyException>(startFailure);
        Assert.Null(drainFailure);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(0, environment.Radio.StopCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, snapshot.EffectiveState);
    }

    [Fact]
    public async Task ChangedHandlerNeverReadsExternalStateInline()
    {
        var environment = new C10Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        environment.Platform.RaiseChangedFromGetter = true;

        environment.Platform.RaiseChanged();
        var drainFailure = await Record.ExceptionAsync(() =>
            coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        await coordinator.DisposeAsync();

        Assert.Null(drainFailure);
        Assert.False(environment.Platform.GetterObservedInsideChangedDispatch);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task CrossThreadChangedFromGetterCannotDeadlockEventSerialization()
    {
        var environment = new C10Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        environment.Platform.RaiseChangedCrossThreadFromGetter = true;

        environment.Platform.RaiseChanged();
        var drainFailure = await Record.ExceptionAsync(() =>
            coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        await coordinator.DisposeAsync();

        Assert.Null(drainFailure);
        Assert.True(environment.Platform.CrossThreadChangedReturned);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task SynchronouslyBlockingIntentStoreCannotHoldCoordinatorLocks(
        string trigger)
    {
        var environment = new C10Environment
        {
            Intent = new BlockingC10Intent()
        };
        var coordinator = environment.CreateCoordinator();
        var starting = Task.Run(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        await environment.Intent.ActiveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var lifecycle = trigger == "stop"
            ? Task.Run(() => coordinator.StopAsync())
            : Task.Run(() => coordinator.DisposeAsync().AsTask());
        Exception? boundedFailure;
        try
        {
            boundedFailure = await Record.ExceptionAsync(async () =>
            {
                await environment.Radio.StopEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
                if (trigger == "stop")
                {
                    await lifecycle.WaitAsync(TimeSpan.FromSeconds(2));
                }
            });
        }
        finally
        {
            environment.Intent.ReleaseActive();
        }

        var startFailure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));
        var lifecycleFailure = await Record.ExceptionAsync(() =>
            lifecycle.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Null(boundedFailure);
        Assert.Null(startFailure);
        Assert.Null(lifecycleFailure);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
        if (trigger == "stop")
        {
            await coordinator.DisposeAsync();
        }
    }

    private sealed class C10Environment
    {
        public C10Radio Radio { get; } = new();
        public C10Platform Platform { get; } = new();
        public C10Clock Clock { get; } = new();
        public BlockingC10Intent? Intent { get; init; }

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C10Radio : INearbyRadioAdapter
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
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

    private sealed class C10Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;
        private int changedDispatchDepth;

        public bool RaiseChangedFromGetter { get; set; }
        public bool RaiseChangedCrossThreadFromGetter { get; set; }
        public bool GetterObservedInsideChangedDispatch { get; private set; }
        public bool CrossThreadChangedReturned { get; private set; }

        public NearbyPlatformSnapshot Snapshot
        {
            get
            {
                GetterObservedInsideChangedDispatch |=
                    Volatile.Read(ref changedDispatchDepth) > 0;
                if (RaiseChangedFromGetter)
                {
                    RaiseChangedFromGetter = false;
                    RaiseChanged();
                }

                if (RaiseChangedCrossThreadFromGetter)
                {
                    RaiseChangedCrossThreadFromGetter = false;
                    var raised = Task.Run(RaiseChanged);
                    CrossThreadChangedReturned = raised.Wait(
                        TimeSpan.FromMilliseconds(500));
                }

                return Current;
            }
        }

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            changed += handler;
            subscription = new NearbyPlatformSubscription(
                () => changed -= handler);
            return true;
        }

        public void RaiseChanged()
        {
            Interlocked.Increment(ref changedDispatchDepth);
            try
            {
                changed?.Invoke(this, Current);
            }
            finally
            {
                Interlocked.Decrement(ref changedDispatchDepth);
            }
        }

        private static NearbyPlatformSnapshot Current { get; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);
    }

    private sealed class C10Clock : INearbyClock
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

    private sealed class BlockingC10Intent : INearbyModeIntentStore
    {
        private readonly ManualResetEventSlim activeRelease = new();

        public TaskCompletionSource ActiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (intent.Mode != NearbyUserMode.Off)
            {
                ActiveEntered.TrySetResult();
                activeRelease.Wait();
            }

            return Task.CompletedTask;
        }

        public void ReleaseActive() => activeRelease.Set();
    }
}
