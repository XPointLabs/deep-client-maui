using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC5Tests
{
    [Theory]
    [InlineData(NearbyUserMode.ForegroundEmergency)]
    [InlineData(NearbyUserMode.ChargingHub)]
    public async Task CallerCancellationWinsOverLateIntentStoreFailure(
        NearbyUserMode mode)
    {
        var environment = new C5Environment();
        environment.Intent.BlockActive = true;
        environment.Intent.ThrowActiveAfterRelease = true;
        var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            mode,
            cancellationToken: cancellation.Token);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        environment.Intent.ReleaseActive();

        var failure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.DoesNotContain("intent-secret", failure?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        await coordinator.DrainAsync();
        Assert.Equal(NearbyIntentPersistenceState.Consistent,
            coordinator.Snapshot.IntentPersistenceState);
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("platform")]
    [InlineData("deadline")]
    public async Task ExternalStopThenCallerCancellationCannotHangStart(
        string trigger)
    {
        var environment = new C5Environment();
        environment.Intent.BlockActive = true;
        var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency,
            cancellationToken: cancellation.Token);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        switch (trigger)
        {
            case "explicit":
                await coordinator.StopAsync();
                break;
            case "platform":
                environment.Platform.Set(
                    environment.Platform.Snapshot with { IsForeground = false });
                break;
            case "deadline":
                environment.Clock.Advance(
                    NearbyPolicyConstants.DefaultEmergencyDuration);
                break;
        }

        await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        environment.Intent.ReleaseActive();

        var failure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        environment.Platform.Set(environment.Platform.Snapshot with
        {
            IsForeground = true
        });
        await coordinator.DrainAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public void AtomicRejectedSubscriptionCannotRetainHandler()
    {
        var environment = new C5Environment();
        environment.Platform.AddBeforeThrow = true;
        environment.Platform.ThrowOnAdd = true;

        var failure = Record.Exception(() => environment.CreateCoordinator());

        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.Equal(0, environment.Platform.HandlerCount);
        Assert.DoesNotContain("subscription-secret", transition.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisposedCancellationSourceTokenStillSupportsRegistration()
    {
        var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();
        var callbackCalled = false;

        var failure = Record.Exception(() =>
        {
            using var registration = token.Register(() => callbackCalled = true);
        });

        Assert.Null(failure);
        Assert.False(callbackCalled);
    }

    [Fact]
    public async Task CanceledDisposedTokenNeverStartsPhysicalRadio()
    {
        var environment = new C5Environment();
        var coordinator = environment.CreateCoordinator();
        var source = new CancellationTokenSource();
        source.Cancel();
        var token = source.Token;
        source.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.StartAsync(
                NearbyUserMode.ChargingHub,
                cancellationToken: token));

        Assert.Equal(0, environment.Radio.StartCalls);
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(NearbyUserMode.ForegroundEmergency)]
    [InlineData(NearbyUserMode.ChargingHub)]
    public async Task CancellationInterleavedWithPhysicalStartStopsGeneration(
        NearbyUserMode mode)
    {
        var environment = new C5Environment();
        environment.Radio.BlockStart = true;
        var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            mode,
            cancellationToken: cancellation.Token);
        await environment.Radio.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        environment.Radio.ReleaseStart();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.NotEqual(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    private sealed class C5Environment
    {
        public C5Radio Radio { get; } = new();
        public C5Platform Platform { get; } = new();
        public C5Clock Clock { get; } = new();
        public C5Intent Intent { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C5Radio : INearbyRadioAdapter
    {
        private readonly TaskCompletionSource startRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");
        public bool BlockStart { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            _ = cancellationToken;
            StartCalls++;
            StartEntered.TrySetResult();
            if (BlockStart)
            {
                await startRelease.Task;
            }
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

        public void ReleaseStart() => startRelease.TrySetResult();
    }

    private sealed class C5Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;

        public bool ThrowOnAdd { get; set; }
        public bool AddBeforeThrow { get; set; }
        public bool ThrowOnRemove { get; set; }
        public int HandlerCount => changed?.GetInvocationList().Length ?? 0;
        public NearbyPlatformSnapshot Snapshot { get; private set; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            if (ThrowOnAdd)
            {
                subscription = null;
                if (AddBeforeThrow)
                {
                    throw new InvalidOperationException("subscription-secret");
                }

                return false;
            }

            changed += handler;
            subscription = new NearbyPlatformSubscription(
                () => changed -= handler);
            return true;
        }

        public void Set(NearbyPlatformSnapshot value)
        {
            Snapshot = value;
            changed?.Invoke(this, value);
        }
    }

    private sealed class C5Clock : INearbyClock
    {
        private readonly object sync = new();
        private readonly List<DelayWaiter> waiters = [];
        private TimeSpan monotonicNow;

        public DateTimeOffset UtcNow =>
            DateTimeOffset.Parse("2026-07-19T00:00:00Z") + monotonicNow;
        public TimeSpan MonotonicNow => monotonicNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                var waiter = new DelayWaiter(
                    monotonicNow + delay,
                    new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                waiter.Cancellation = cancellationToken.Register(
                    () => waiter.Completion.TrySetCanceled(cancellationToken));
                waiters.Add(waiter);
                return waiter.Completion.Task;
            }
        }

        public void Advance(TimeSpan amount)
        {
            List<DelayWaiter> ready;
            lock (sync)
            {
                monotonicNow += amount;
                ready = waiters.Where(waiter => waiter.Deadline <= monotonicNow)
                    .ToList();
                waiters.RemoveAll(waiter => ready.Contains(waiter));
            }

            foreach (var waiter in ready)
            {
                waiter.Cancellation.Dispose();
                waiter.Completion.TrySetResult();
            }
        }

        private sealed record DelayWaiter(
            TimeSpan Deadline,
            TaskCompletionSource Completion)
        {
            public CancellationTokenRegistration Cancellation { get; set; }
        }
    }

    private sealed class C5Intent : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockActive { get; set; }
        public bool ThrowActiveAfterRelease { get; set; }
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
                if (ThrowActiveAfterRelease)
                {
                    throw new InvalidOperationException("intent-secret");
                }
            }
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
    }
}
