using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC6Tests
{
    [Theory]
    [InlineData("explicit", NearbyUserMode.ForegroundEmergency)]
    [InlineData("explicit", NearbyUserMode.ChargingHub)]
    [InlineData("platform", NearbyUserMode.ForegroundEmergency)]
    [InlineData("platform", NearbyUserMode.ChargingHub)]
    [InlineData("deadline", NearbyUserMode.ForegroundEmergency)]
    [InlineData("dispose", NearbyUserMode.ForegroundEmergency)]
    [InlineData("dispose", NearbyUserMode.ChargingHub)]
    [InlineData("caller", NearbyUserMode.ForegroundEmergency)]
    [InlineData("caller", NearbyUserMode.ChargingHub)]
    public async Task ThrowingProviderCancellationCallbackCannotOrphanRadio(
        string trigger,
        NearbyUserMode mode)
    {
        var environment = new C6Environment();
        environment.Intent.BlockActive = true;
        environment.Radio.ThrowFromCancellationCallback = true;
        var coordinator = environment.CreateCoordinator();
        using var callerCancellation = new CancellationTokenSource();
        var starting = coordinator.StartAsync(
            mode,
            cancellationToken: callerCancellation.Token);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<Exception?>? triggerTask = null;
        Exception? synchronousTriggerFailure = null;
        switch (trigger)
        {
            case "explicit":
                triggerTask = Record.ExceptionAsync(() => coordinator.StopAsync());
                break;
            case "platform":
                environment.Platform.Set(
                    environment.Platform.Snapshot with { IsForeground = false });
                break;
            case "deadline":
                environment.Clock.Advance(
                    NearbyPolicyConstants.DefaultEmergencyDuration);
                break;
            case "dispose":
                triggerTask = Record.ExceptionAsync(() =>
                    coordinator.DisposeAsync().AsTask());
                break;
            case "caller":
                synchronousTriggerFailure = Record.Exception(
                    callerCancellation.Cancel);
                break;
        }

        var physicalStopObserved = false;
        try
        {
            await environment.Radio.StopEntered.Task.WaitAsync(
                TimeSpan.FromMilliseconds(750));
            physicalStopObserved = true;
        }
        finally
        {
            environment.Intent.ReleaseActive();
        }

        var startFailure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));
        if (triggerTask is not null)
        {
            synchronousTriggerFailure = await triggerTask.WaitAsync(
                TimeSpan.FromSeconds(2));
        }

        if (trigger is "platform" or "deadline")
        {
            synchronousTriggerFailure = await Record.ExceptionAsync(() =>
                coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        else if (trigger is "explicit" or "caller")
        {
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(physicalStopObserved);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(new NearbyModeIntent(NearbyUserMode.Off, null),
            environment.Intent.Saved);
        Assert.DoesNotContain("provider-secret",
            synchronousTriggerFailure?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-secret",
            startFailure?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        if (trigger == "caller")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(startFailure);
        }
        else
        {
            Assert.Null(startFailure);
        }

        if (trigger != "dispose")
        {
            environment.Platform.Set(environment.Platform.Snapshot with
            {
                IsForeground = true
            });
            await coordinator.DrainAsync();
            await coordinator.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("drain")]
    [InlineData("dispose")]
    public async Task SubscriptionLeaseReentrancyFailsFastAndCleanupCanRetry(
        string callback)
    {
        var environment = new C6Environment();
        var coordinator = environment.CreateCoordinator();
        Exception? callbackFailure = null;
        environment.Platform.OnDispose = () =>
        {
            callbackFailure = callback == "drain"
                ? Record.Exception(() =>
                    coordinator.DrainAsync().GetAwaiter().GetResult())
                : Record.Exception(() =>
                {
                    var nested = coordinator.DisposeAsync().AsTask();
                    if (!nested.Wait(TimeSpan.FromMilliseconds(250)))
                    {
                        throw new TimeoutException("self-deadlock");
                    }

                    nested.GetAwaiter().GetResult();
                });
        };
        environment.Platform.ThrowOnDispose = true;

        var firstFailure = await Record.ExceptionAsync(() =>
            coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsType<NearbyCoordinatorReentrancyException>(callbackFailure);
        var transition = Assert.IsType<NearbyRadioTransitionException>(firstFailure);
        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        environment.Platform.OnDispose = null;
        environment.Platform.ThrowOnDispose = false;
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        Assert.Equal(0, environment.Platform.HandlerCount);
    }

    private sealed class C6Environment
    {
        public C6Radio Radio { get; } = new();
        public C6Platform Platform { get; } = new();
        public C6Clock Clock { get; } = new();
        public C6Intent Intent { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C6Radio : INearbyRadioAdapter
    {
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");
        public bool ThrowFromCancellationCallback { get; set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (ThrowFromCancellationCallback)
            {
                _ = cancellationToken.Register(() =>
                    throw new InvalidOperationException("provider-secret"));
            }

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

    private sealed class C6Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;

        public Action? OnDispose { get; set; }
        public bool ThrowOnDispose { get; set; }
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
            changed += handler;
            subscription = new NearbyPlatformSubscription(() =>
            {
                OnDispose?.Invoke();
                if (ThrowOnDispose)
                {
                    throw new InvalidOperationException("lease-secret");
                }

                changed -= handler;
            });
            return true;
        }

        public void Set(NearbyPlatformSnapshot snapshot)
        {
            Snapshot = snapshot;
            changed?.Invoke(this, snapshot);
        }
    }

    private sealed class C6Clock : INearbyClock
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

    private sealed class C6Intent : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockActive { get; set; }
        public NearbyModeIntent? Saved { get; private set; }
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

            Saved = intent;
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
    }
}
