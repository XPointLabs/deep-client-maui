using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC4Tests
{
    [Theory]
    [InlineData(NearbyUserMode.ForegroundEmergency)]
    [InlineData(NearbyUserMode.ChargingHub)]
    public async Task CallerCancellationStopsRadioWhileIntentSaveIgnoresToken(
        NearbyUserMode mode)
    {
        var environment = new C4Environment();
        environment.Intent.BlockActive = true;
        var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            mode,
            cancellationToken: cancellation.Token);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var stoppedBeforeStoreRelease = false;
        try
        {
            await environment.Radio.StopEntered.Task.WaitAsync(
                TimeSpan.FromMilliseconds(500));
            stoppedBeforeStoreRelease = true;
            Assert.NotEqual(
                NearbyEffectiveState.Active,
                coordinator.Snapshot.EffectiveState);
        }
        finally
        {
            environment.Intent.ReleaseActive();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await starting.WaitAsync(TimeSpan.FromSeconds(2)));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stoppedBeforeStoreRelease);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyIntentPersistenceState.Consistent,
            coordinator.Snapshot.IntentPersistenceState);
        var priorStops = environment.Radio.StopCalls;

        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        await Task.Delay(50);
        Assert.Equal(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);
        Assert.Equal(priorStops, environment.Radio.StopCalls);
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorSanitizesChangedAddFailureAndUndoesPartialAdd(
        bool addBeforeThrow)
    {
        var environment = new C4Environment();
        environment.Platform.ThrowOnAdd = true;
        environment.Platform.AddBeforeThrow = addBeforeThrow;

        var failure = Record.Exception(() => environment.CreateCoordinator());

        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.DoesNotContain("add-secret", transition.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, environment.Platform.HandlerCount);
        Assert.Equal(0, environment.Radio.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeSanitizesChangedRemoveFailureAndRemainsRetryable(
        bool removeBeforeThrow)
    {
        var environment = new C4Environment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        environment.Platform.ThrowOnRemove = true;
        environment.Platform.RemoveBeforeThrow = removeBeforeThrow;

        var failure = await Record.ExceptionAsync(() =>
            coordinator.DisposeAsync().AsTask());

        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.DoesNotContain("remove-secret", transition.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, environment.Radio.StopCalls);

        environment.Platform.ThrowOnRemove = false;
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
        Assert.Equal(0, environment.Platform.HandlerCount);
    }

    private sealed class C4Environment
    {
        public C4Radio Radio { get; } = new();
        public C4Platform Platform { get; } = new();
        public C4Clock Clock { get; } = new();
        public C4Intent Intent { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C4Radio : INearbyRadioAdapter
    {
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class C4Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;

        public bool ThrowOnAdd { get; set; }
        public bool AddBeforeThrow { get; set; }
        public bool ThrowOnRemove { get; set; }
        public bool RemoveBeforeThrow { get; set; }
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

        public event EventHandler<NearbyPlatformSnapshot>? Changed
        {
            add
            {
                if (ThrowOnAdd && !AddBeforeThrow)
                {
                    throw new InvalidOperationException("add-secret");
                }

                changed += value;
                if (ThrowOnAdd)
                {
                    throw new InvalidOperationException("add-secret");
                }
            }
            remove
            {
                if (ThrowOnRemove && !RemoveBeforeThrow)
                {
                    throw new InvalidOperationException("remove-secret");
                }

                changed -= value;
                if (ThrowOnRemove)
                {
                    throw new InvalidOperationException("remove-secret");
                }
            }
        }
    }

    private sealed class C4Clock : INearbyClock
    {
        public DateTimeOffset UtcNow { get; } =
            DateTimeOffset.Parse("2026-07-19T00:00:00Z");
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = delay;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class C4Intent : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockActive { get; set; }
        public TaskCompletionSource ActiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (BlockActive && intent.Mode != NearbyUserMode.Off)
            {
                ActiveEntered.TrySetResult();
                await activeRelease.Task;
            }
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
    }
}
