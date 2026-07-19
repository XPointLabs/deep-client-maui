using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC2Tests
{
    [Fact]
    public async Task PhysicalStopDoesNotWaitForBlockedActiveIntentCommit()
    {
        var environment = new C2Environment();
        environment.IntentStore.BlockActive = true;
        var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await environment.IntentStore.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = coordinator.StopAsync();
        var stopCompletedBeforeSave = false;
        try
        {
            await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            await stopping.WaitAsync(TimeSpan.FromMilliseconds(500));
            stopCompletedBeforeSave = true;
        }
        finally
        {
            environment.IntentStore.ReleaseActive();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopCompletedBeforeSave);
        Assert.Equal(new NearbyModeIntent(NearbyUserMode.Off, null), environment.IntentStore.Saved);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task FalliblePostStartReadsFailStopWithSanitizedError()
    {
        var environment = new C2Environment();
        environment.Clock.ThrowMonotonicAfterRadioStart = true;
        var coordinator = environment.CreateCoordinator();

        var failure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));

        Assert.Equal(typeof(NearbyRadioTransitionException), failure?.GetType());
        Assert.DoesNotContain("clock-secret", failure?.ToString() ?? string.Empty);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.NotEqual(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);
        environment.Clock.ThrowMonotonicAfterRadioStart = false;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task RefreshReadFailureFailStopsWithSanitizedError()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Platform.ThrowSnapshot = true;

        var failure = await Record.ExceptionAsync(() => coordinator.RefreshAsync());

        Assert.Equal(typeof(NearbyRadioTransitionException), failure?.GetType());
        Assert.DoesNotContain("platform-secret", failure?.ToString() ?? string.Empty);
        Assert.Equal(1, environment.Radio.StopCalls);
        environment.Platform.ThrowSnapshot = false;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DrainClockReadFailureFailStopsWithSanitizedError()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Clock.ThrowMonotonicAfterRadioStart = true;

        var failure = await Record.ExceptionAsync(() => coordinator.DrainAsync());

        Assert.Equal(typeof(NearbyRadioTransitionException), failure?.GetType());
        Assert.DoesNotContain("clock-secret", failure?.ToString() ?? string.Empty);
        Assert.Equal(1, environment.Radio.StopCalls);
        environment.Clock.ThrowMonotonicAfterRadioStart = false;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StopCompletionSurvivesExternalGetterFailureAndClearsTransition()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Radio.OnStop = () =>
        {
            environment.Platform.ThrowSnapshot = true;
            environment.Radio.ThrowCapability = true;
            return Task.CompletedTask;
        };

        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        environment.Radio.OnStop = null;
        environment.Platform.ThrowSnapshot = false;
        environment.Radio.ThrowCapability = false;
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeDoesNotReportSuccessWhenPhysicalStopIsUnknownAndAllowsRetry()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Radio.ThrowStop = true;

        var failure = await Record.ExceptionAsync(() => coordinator.DisposeAsync().AsTask());

        Assert.Equal(typeof(NearbyRadioTransitionException), failure?.GetType());
        Assert.Equal(NearbyEffectiveState.StopFailed, coordinator.Snapshot.EffectiveState);
        environment.Radio.ThrowStop = false;
        await coordinator.StopAsync();
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AdapterStopCallingDrainFailsFastInsteadOfJoiningItsOwnStop()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        Exception? callbackFailure = null;
        environment.Radio.OnStop = async () =>
        {
            callbackFailure = await Record.ExceptionAsync(() =>
                coordinator.DrainAsync().WaitAsync(TimeSpan.FromMilliseconds(250)));
        };

        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(typeof(NearbyCoordinatorReentrancyException), callbackFailure?.GetType());
        environment.Radio.OnStop = null;
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntentStoreCallbacksCannotDeadlockCoordinator(bool savingOff)
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        Exception? callbackFailure = null;
        environment.IntentStore.OnSave = async intent =>
        {
            if ((intent.Mode == NearbyUserMode.Off) == savingOff)
            {
                environment.IntentStore.OnSave = null;
                callbackFailure = await Record.ExceptionAsync(() =>
                    coordinator.StopAsync().WaitAsync(TimeSpan.FromMilliseconds(250)));
            }
        };

        if (savingOff)
        {
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency)
                .WaitAsync(TimeSpan.FromSeconds(2));
            environment.IntentStore.OnSave = null;
            await coordinator.StopAsync();
        }

        Assert.Equal(typeof(NearbyCoordinatorReentrancyException), callbackFailure?.GetType());
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ActiveIntentCallbackCannotDisposeCoordinatorReentrantly()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        Exception? callbackFailure = null;
        environment.IntentStore.OnSave = async intent =>
        {
            if (intent.Mode != NearbyUserMode.Off)
            {
                environment.IntentStore.OnSave = null;
                callbackFailure = await Record.ExceptionAsync(() =>
                    coordinator.DisposeAsync().AsTask());
            }
        };

        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(typeof(NearbyCoordinatorReentrancyException), callbackFailure?.GetType());
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task OffPersistenceIsSerializedAgainstStartAndJoinedByDispose()
    {
        var environment = new C2Environment();
        environment.IntentStore.BlockOff = true;
        var coordinator = environment.CreateCoordinator();

        var stopping = coordinator.StopAsync();
        await environment.IntentStore.OffEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var startFailure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        var disposing = coordinator.DisposeAsync().AsTask();
        await Task.Delay(100);
        var disposeJoinedOffSave = !disposing.IsCompleted;
        environment.IntentStore.ReleaseOff();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(typeof(NearbyCoordinatorBusyException), startFailure?.GetType());
        Assert.True(disposeJoinedOffSave);
        Assert.Equal(new NearbyModeIntent(NearbyUserMode.Off, null), environment.IntentStore.Saved);
    }

    [Fact]
    public async Task DetachedOffPersistenceFailureIsTypedAndObservable()
    {
        var environment = new C2Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.IntentStore.ThrowOff = true;

        await coordinator.StopAsync();
        await coordinator.DrainAsync();

        var property = coordinator.Snapshot.GetType().GetProperty("IntentPersistenceState");
        Assert.NotNull(property);
        Assert.Equal("Failed", property!.GetValue(coordinator.Snapshot)?.ToString());
        environment.IntentStore.ThrowOff = false;
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    private sealed class C2Environment
    {
        public C2Radio Radio { get; } = new();
        public C2Platform Platform { get; } = new();
        public C2Clock Clock { get; }
        public C2IntentStore IntentStore { get; } = new();

        public C2Environment()
        {
            Clock = new C2Clock(Radio);
        }

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, IntentStore);
    }

    private sealed class C2Radio : INearbyRadioAdapter
    {
        private readonly NearbyRadioCapability capability =
            new(NearbyRadioSupport.Supported, "test");

        public bool Started { get; private set; }
        public bool ThrowStop { get; set; }
        public bool ThrowCapability { get; set; }
        public int StopCalls { get; private set; }
        public Func<Task>? OnStop { get; set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyRadioCapability Capability => ThrowCapability
            ? throw new InvalidOperationException("capability-secret")
            : capability;

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return Task.CompletedTask;
        }

        public async Task StopAsync(
            NearbyStopReason reason,
            CancellationToken cancellationToken)
        {
            _ = reason;
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            StopEntered.TrySetResult();
            if (OnStop is not null)
            {
                await OnStop();
            }

            if (ThrowStop)
            {
                throw new InvalidOperationException("stop-secret");
            }

            Started = false;
        }
    }

    private sealed class C2Platform : INearbyPlatformState
    {
        private NearbyPlatformSnapshot snapshot = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);

        public bool ThrowSnapshot { get; set; }
        public NearbyPlatformSnapshot Snapshot => ThrowSnapshot
            ? throw new InvalidOperationException("platform-secret")
            : snapshot;

        private event EventHandler<NearbyPlatformSnapshot>? Changed;

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            Changed += handler;
            subscription = new NearbyPlatformSubscription(
                () => Changed -= handler);
            return true;
        }

        public void Set(NearbyPlatformSnapshot value)
        {
            snapshot = value;
            Changed?.Invoke(this, value);
        }
    }

    private sealed class C2Clock(C2Radio radio) : INearbyClock
    {
        public bool ThrowMonotonicAfterRadioStart { get; set; }
        public DateTimeOffset UtcNow { get; } =
            DateTimeOffset.Parse("2026-07-19T00:00:00Z");
        public TimeSpan MonotonicNow => ThrowMonotonicAfterRadioStart && radio.Started
            ? throw new InvalidOperationException("clock-secret")
            : TimeSpan.Zero;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = delay;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class C2IntentStore : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource offRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockActive { get; set; }
        public bool BlockOff { get; set; }
        public bool ThrowOff { get; set; }
        public NearbyModeIntent? Saved { get; private set; }
        public Func<NearbyModeIntent, Task>? OnSave { get; set; }
        public TaskCompletionSource ActiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OffEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (OnSave is not null)
            {
                await OnSave(intent);
            }

            if (intent.Mode == NearbyUserMode.Off)
            {
                OffEntered.TrySetResult();
                if (BlockOff)
                {
                    await offRelease.Task;
                }

                if (ThrowOff)
                {
                    throw new InvalidOperationException("intent-secret");
                }
            }
            else
            {
                ActiveEntered.TrySetResult();
                if (BlockActive)
                {
                    await activeRelease.Task;
                }
            }

            Saved = intent;
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
        public void ReleaseOff() => offRelease.TrySetResult();
    }
}
