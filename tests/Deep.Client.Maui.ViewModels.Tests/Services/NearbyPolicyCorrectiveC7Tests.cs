using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC7Tests
{
    [Theory]
    [InlineData(NearbyUserMode.ForegroundEmergency)]
    [InlineData(NearbyUserMode.ChargingHub)]
    public async Task CancellationAtPhysicalStartCompletionCannotReturnSuccess(
        NearbyUserMode mode)
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var environment = new C7Environment();
            environment.Radio.BlockStart = true;
            var coordinator = environment.CreateCoordinator();
            using var cancellation = new CancellationTokenSource();

            var starting = coordinator.StartAsync(
                mode,
                cancellationToken: cancellation.Token);
            await environment.Radio.StartEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            environment.Radio.ReleaseStart();

            var failure = await Record.ExceptionAsync(() =>
                starting.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(1, environment.Radio.StopCalls);
            await coordinator.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(NearbyUserMode.ForegroundEmergency)]
    [InlineData(NearbyUserMode.ChargingHub)]
    public async Task CancellationBeforeFinalContinuationCannotReturnSuccess(
        NearbyUserMode mode)
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var environment = new C7Environment();
            environment.Intent.BlockActive = true;
            environment.Intent.IgnoreCancellation = true;
            var coordinator = environment.CreateCoordinator();
            using var cancellation = new CancellationTokenSource();
            var starting = coordinator.StartAsync(
                mode,
                cancellationToken: cancellation.Token);
            await environment.Intent.ActiveEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            environment.Intent.ReleaseActive();
            var failure = await Record.ExceptionAsync(() =>
                starting.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(1, environment.Radio.StopCalls);
            await coordinator.DisposeAsync();
        }
    }

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
    public async Task BlockedProviderCancellationCannotBlockStopOrDisposeResourcesEarly(
        string trigger,
        NearbyUserMode mode)
    {
        var environment = new C7Environment();
        environment.Radio.BlockCancellationCallback = true;
        environment.Intent.BlockActive = true;
        var coordinator = environment.CreateCoordinator();
        using var callerCancellation = new CancellationTokenSource();
        var starting = coordinator.StartAsync(
            mode,
            cancellationToken: callerCancellation.Token);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var attemptCancellation = GetAttemptCancellation(coordinator);

        Task publicLifecycle = trigger switch
        {
            "explicit" => coordinator.StopAsync(),
            "platform" => TriggerPlatformStopAsync(environment, coordinator),
            "deadline" => TriggerDeadlineStopAsync(environment, coordinator),
            "dispose" => coordinator.DisposeAsync().AsTask(),
            "caller" => CancelCallerAsync(callerCancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger))
        };

        Exception? lifecycleFailure = null;
        Exception? disposedBeforeCallbackCompleted = null;
        try
        {
            await environment.Radio.CancellationCallbackEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lifecycleFailure = await Record.ExceptionAsync(() =>
                publicLifecycle.WaitAsync(TimeSpan.FromSeconds(2)));
            disposedBeforeCallbackCompleted = Record.Exception(
                () => _ = attemptCancellation.Token);
        }
        catch (Exception exception)
        {
            lifecycleFailure = exception;
        }
        finally
        {
            environment.Radio.ReleaseCancellationCallback();
            environment.Intent.ReleaseActive();
        }

        var startFailure = await Record.ExceptionAsync(() =>
            starting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(lifecycleFailure);
        Assert.Equal(1, environment.Radio.StopCalls);
        if (trigger == "caller")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(startFailure);
        }
        else
        {
            Assert.Null(startFailure);
        }

        Assert.Null(disposedBeforeCallbackCompleted);
        await WaitUntilDisposedAsync(attemptCancellation);
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

    private static CancellationTokenSource GetAttemptCancellation(
        NearbyPolicyCoordinator coordinator)
    {
        var attempt = typeof(NearbyPolicyCoordinator).GetField(
            "currentAttempt",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);
        Assert.NotNull(attempt);
        return Assert.IsType<CancellationTokenSource>(
            attempt!.GetType().GetProperty(
                "Cancellation",
                BindingFlags.Instance | BindingFlags.Public)!.GetValue(attempt));
    }

    private static async Task TriggerPlatformStopAsync(
        C7Environment environment,
        NearbyPolicyCoordinator coordinator)
    {
        environment.Platform.Set(environment.Platform.Snapshot with
        {
            IsForeground = false
        });
        await coordinator.DrainAsync();
    }

    private static async Task TriggerDeadlineStopAsync(
        C7Environment environment,
        NearbyPolicyCoordinator coordinator)
    {
        environment.Clock.Advance(NearbyPolicyConstants.DefaultEmergencyDuration);
        await coordinator.DrainAsync();
    }

    private static Task CancelCallerAsync(CancellationTokenSource cancellation)
    {
        return Task.Run(cancellation.Cancel);
    }

    private static async Task WaitUntilDisposedAsync(
        CancellationTokenSource cancellation)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Record.Exception(() => _ = cancellation.Token) is
                ObjectDisposedException)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Attempt cancellation source was not disposed after callback completion.");
    }

    private sealed class C7Environment
    {
        public C7Radio Radio { get; } = new();
        public C7Platform Platform { get; } = new();
        public C7Clock Clock { get; } = new();
        public C7Intent Intent { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C7Radio : INearbyRadioAdapter
    {
        private readonly TaskCompletionSource startRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");
        public bool BlockStart { get; set; }
        public bool BlockCancellationCallback { get; set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationCallbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (BlockCancellationCallback)
            {
                _ = cancellationToken.Register(() =>
                {
                    CancellationCallbackEntered.TrySetResult();
                    cancellationRelease.Task.GetAwaiter().GetResult();
                });
            }

            StartEntered.TrySetResult();
            await Task.Yield();
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
        public void ReleaseCancellationCallback() =>
            cancellationRelease.TrySetResult();
    }

    private sealed class C7Platform : INearbyPlatformState
    {
        private readonly TaskCompletionSource postStartReadRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<NearbyPlatformSnapshot>? changed;
        private int snapshotReads;
        private NearbyPlatformSnapshot snapshot = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);

        public bool BlockPostStartRead { get; set; }
        public TaskCompletionSource PostStartReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NearbyPlatformSnapshot Snapshot
        {
            get
            {
                var read = Interlocked.Increment(ref snapshotReads);
                if (BlockPostStartRead && read == 2)
                {
                    PostStartReadEntered.TrySetResult();
                    postStartReadRelease.Task.GetAwaiter().GetResult();
                }

                return snapshot;
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

        public void Set(NearbyPlatformSnapshot value)
        {
            snapshot = value;
            changed?.Invoke(this, value);
        }

        public void ReleasePostStartRead() =>
            postStartReadRelease.TrySetResult();
    }

    private sealed class C7Clock : INearbyClock
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

    private sealed class C7Intent : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockActive { get; set; }
        public bool IgnoreCancellation { get; set; }
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
                if (IgnoreCancellation)
                {
                    await activeRelease.Task;
                }
                else
                {
                    await activeRelease.Task.WaitAsync(cancellationToken);
                }
            }
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
    }
}
