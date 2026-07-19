using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC9Tests
{
    [Theory]
    [InlineData("platform", "start")]
    [InlineData("platform", "stop")]
    [InlineData("platform", "refresh")]
    [InlineData("platform", "drain")]
    [InlineData("platform", "dispose")]
    [InlineData("capability", "start")]
    [InlineData("capability", "stop")]
    [InlineData("capability", "refresh")]
    [InlineData("capability", "drain")]
    [InlineData("capability", "dispose")]
    [InlineData("utc", "start")]
    [InlineData("utc", "stop")]
    [InlineData("utc", "refresh")]
    [InlineData("utc", "drain")]
    [InlineData("utc", "dispose")]
    [InlineData("monotonic", "start")]
    [InlineData("monotonic", "stop")]
    [InlineData("monotonic", "refresh")]
    [InlineData("monotonic", "drain")]
    [InlineData("monotonic", "dispose")]
    public async Task AdmissionGetterReentryFailsFastBeforeASecondGeneration(
        string getter,
        string operation)
    {
        var environment = new C9Environment();
        var coordinator = environment.CreateCoordinator();
        Exception? callbackFailure = null;
        environment.SetGetterCallback(getter, () =>
        {
            environment.ClearGetterCallbacks();
            callbackFailure = InvokeLifecycle(coordinator, operation);
        });

        var outerFailure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        var activeSnapshot = coordinator.Snapshot;
        var disposeFailure = await Record.ExceptionAsync(
            () => coordinator.DisposeAsync().AsTask());

        Assert.Null(outerFailure);
        Assert.IsType<NearbyCoordinatorReentrancyException>(callbackFailure);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(NearbyUserMode.ForegroundEmergency, activeSnapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Active, activeSnapshot.EffectiveState);
        Assert.Null(disposeFailure);
    }

    [Fact]
    public async Task AdmissionGuardFlowsIntoAsynchronousGetterWork()
    {
        var environment = new C9Environment();
        var coordinator = environment.CreateCoordinator();
        Task<Exception?> callbackFailure = Task.FromResult<Exception?>(null);
        environment.Platform.OnSnapshot = () =>
        {
            environment.ClearGetterCallbacks();
            callbackFailure = Task.Run(async () =>
                (Exception?)await Record.ExceptionAsync(() =>
                    coordinator.StartAsync(NearbyUserMode.ChargingHub)));
        };

        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        var failure = await callbackFailure.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshot = coordinator.Snapshot;
        await coordinator.DisposeAsync();

        Assert.IsType<NearbyCoordinatorReentrancyException>(failure);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(NearbyEffectiveState.Active, snapshot.EffectiveState);
    }

    [Fact]
    public async Task UnhandledGetterReentryIsSanitizedWithoutPublishingGeneration()
    {
        var environment = new C9Environment();
        var coordinator = environment.CreateCoordinator();
        environment.Platform.OnSnapshot = () =>
        {
            environment.ClearGetterCallbacks();
            coordinator.StartAsync(NearbyUserMode.ChargingHub)
                .GetAwaiter().GetResult();
        };

        var failure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);

        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(0, environment.Radio.StopCalls);
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitStopInvalidatesBlockedAdmissionWithoutRadioWork()
    {
        var environment = new C9Environment();
        var coordinator = environment.CreateCoordinator();
        var getterEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var getterRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Platform.OnSnapshot = () =>
        {
            environment.ClearGetterCallbacks();
            getterEntered.TrySetResult();
            getterRelease.Task.GetAwaiter().GetResult();
        };
        var starting = Task.Run(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        await getterEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopFailure = await Record.ExceptionAsync(() =>
            coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        getterRelease.TrySetResult();
        var startFailure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Null(stopFailure);
        Assert.IsType<NearbyCoordinatorBusyException>(startFailure);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(0, environment.Radio.StopCalls);
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData("utc")]
    [InlineData("monotonic")]
    public async Task AdmissionClockOverflowIsSanitizedAndNeverStartsRadio(
        string overflow)
    {
        var environment = new C9Environment();
        if (overflow == "utc")
        {
            environment.Clock.Utc = DateTimeOffset.MaxValue;
        }
        else
        {
            environment.Clock.Monotonic = TimeSpan.MaxValue;
        }

        var coordinator = environment.CreateCoordinator();
        var failure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);

        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.DoesNotContain(
            nameof(ArgumentOutOfRangeException),
            transition.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(0, environment.Radio.StopCalls);
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    private static Exception? InvokeLifecycle(
        NearbyPolicyCoordinator coordinator,
        string operation) =>
        operation switch
        {
            "start" => Record.Exception(() =>
                coordinator.StartAsync(NearbyUserMode.ChargingHub)
                    .GetAwaiter().GetResult()),
            "stop" => Record.Exception(() =>
                coordinator.StopAsync().GetAwaiter().GetResult()),
            "refresh" => Record.Exception(() =>
                coordinator.RefreshAsync().GetAwaiter().GetResult()),
            "drain" => Record.Exception(() =>
                coordinator.DrainAsync().GetAwaiter().GetResult()),
            "dispose" => Record.Exception(() =>
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private sealed class C9Environment
    {
        public C9Radio Radio { get; } = new();
        public C9Platform Platform { get; } = new();
        public C9Clock Clock { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock);

        public void SetGetterCallback(string getter, Action callback)
        {
            switch (getter)
            {
                case "platform":
                    Platform.OnSnapshot = callback;
                    break;
                case "capability":
                    Radio.OnCapability = callback;
                    break;
                case "utc":
                    Clock.OnUtc = callback;
                    break;
                case "monotonic":
                    Clock.OnMonotonic = callback;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(getter));
            }
        }

        public void ClearGetterCallbacks()
        {
            Platform.OnSnapshot = null;
            Radio.OnCapability = null;
            Clock.OnUtc = null;
            Clock.OnMonotonic = null;
        }
    }

    private sealed class C9Radio : INearbyRadioAdapter
    {
        public Action? OnCapability { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public NearbyRadioCapability Capability
        {
            get
            {
                OnCapability?.Invoke();
                return new NearbyRadioCapability(
                    NearbyRadioSupport.Supported,
                    "test");
            }
        }

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
            return Task.CompletedTask;
        }
    }

    private sealed class C9Platform : INearbyPlatformState
    {
        public Action? OnSnapshot { get; set; }

        public NearbyPlatformSnapshot Snapshot
        {
            get
            {
                OnSnapshot?.Invoke();
                return new NearbyPlatformSnapshot(
                    IsForeground: true,
                    HasRequiredPermission: true,
                    IsBluetoothEnabled: true,
                    IsLocationAvailable: true,
                    IsCharging: true,
                    BatteryPercent: 80,
                    ThermalState: NearbyThermalState.Nominal);
            }
        }

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            _ = handler;
            subscription = new NearbyPlatformSubscription(() => { });
            return true;
        }
    }

    private sealed class C9Clock : INearbyClock
    {
        public Action? OnUtc { get; set; }
        public Action? OnMonotonic { get; set; }
        public DateTimeOffset Utc { get; set; } =
            DateTimeOffset.Parse("2026-07-19T00:00:00Z");
        public TimeSpan Monotonic { get; set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                OnUtc?.Invoke();
                return Utc;
            }
        }

        public TimeSpan MonotonicNow
        {
            get
            {
                OnMonotonic?.Invoke();
                return Monotonic;
            }
        }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
