using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC3Tests
{
    [Fact]
    public async Task DeadlineStopsPhysicalRadioWhileActiveIntentSaveIsHung()
    {
        var environment = new C3Environment();
        environment.Intent.BlockActive = true;
        var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        environment.Clock.Advance(NearbyPolicyConstants.DefaultEmergencyDuration);
        var stoppedBeforeIntentReleased = false;
        try
        {
            await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            stoppedBeforeIntentReleased = true;
        }
        finally
        {
            environment.Intent.ReleaseActive();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stoppedBeforeIntentReleased);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeStopsPhysicalRadioBeforeDrainingHungActiveIntentSave()
    {
        var environment = new C3Environment();
        environment.Intent.BlockActive = true;
        var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await environment.Intent.ActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposing = coordinator.DisposeAsync().AsTask();
        var stoppedBeforeIntentReleased = false;
        try
        {
            await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            stoppedBeforeIntentReleased = true;
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            environment.Intent.ReleaseActive();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stoppedBeforeIntentReleased);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task ConstructorUsesInfallibleOffSnapshotWithoutExternalGetters()
    {
        var environment = new C3Environment
        {
            ThrowPlatform = true,
            ThrowCapability = true
        };

        var failure = Record.Exception(() => environment.CreateCoordinator());

        Assert.Null(failure);
        var coordinator = environment.Coordinator!;
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        environment.ThrowPlatform = false;
        environment.ThrowCapability = false;
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("capability")]
    [InlineData("utc")]
    [InlineData("monotonic")]
    public async Task PreStartAdmissionReadFailureIsSanitizedAndDoesNotStartRadio(
        string failingRead)
    {
        var environment = new C3Environment();
        var coordinator = environment.CreateCoordinator();
        environment.ThrowPlatform = failingRead == "platform";
        environment.ThrowCapability = failingRead == "capability";
        environment.Clock.ThrowUtc = failingRead == "utc";
        environment.Clock.ThrowMonotonic = failingRead == "monotonic";

        var failure = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));

        var transition = Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(NearbyRadioTransitionError.StateReadFailed, transition.Error);
        Assert.DoesNotContain("secret", transition.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        environment.ThrowPlatform = false;
        environment.ThrowCapability = false;
        environment.Clock.ThrowUtc = false;
        environment.Clock.ThrowMonotonic = false;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CompletedOffStopPublishesClearTransitionBeforeImmediateStart()
    {
        var environment = new C3Environment();
        var coordinator = environment.CreateCoordinator();
        var stopTaskField = typeof(NearbyPolicyCoordinator).GetField(
            "stopTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stopTaskField);

        for (var iteration = 0; iteration < 250; iteration++)
        {
            await coordinator.StopAsync();
            Assert.Null(stopTaskField!.GetValue(coordinator));
            Assert.NotEqual(
                NearbyIntentPersistenceState.Pending,
                coordinator.Snapshot.IntentPersistenceState);

            await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
            await coordinator.StopAsync();
        }

        await coordinator.DisposeAsync();
    }

    private sealed class C3Environment
    {
        private readonly C3Platform platform = new();
        private readonly C3Radio radio = new();

        public C3Environment()
        {
            Platform = new PlatformProxy(this, platform);
            Radio = new RadioProxy(this, radio);
            Clock = new C3Clock();
        }

        public bool ThrowPlatform { get; set; }
        public bool ThrowCapability { get; set; }
        public PlatformProxy Platform { get; }
        public RadioProxy Radio { get; }
        public C3Clock Clock { get; }
        public C3Intent Intent { get; } = new();
        public NearbyPolicyCoordinator? Coordinator { get; private set; }

        public NearbyPolicyCoordinator CreateCoordinator()
        {
            Coordinator = new NearbyPolicyCoordinator(Radio, Platform, Clock, Intent);
            return Coordinator;
        }

        public sealed class PlatformProxy(
            C3Environment owner,
            C3Platform inner) : INearbyPlatformState
        {
            public NearbyPlatformSnapshot Snapshot => owner.ThrowPlatform
                ? throw new InvalidOperationException("platform-secret")
                : inner.Snapshot;

            public event EventHandler<NearbyPlatformSnapshot>? Changed
            {
                add => inner.Changed += value;
                remove => inner.Changed -= value;
            }
        }

        public sealed class RadioProxy(
            C3Environment owner,
            C3Radio inner) : INearbyRadioAdapter
        {
            public NearbyRadioCapability Capability => owner.ThrowCapability
                ? throw new InvalidOperationException("capability-secret")
                : inner.Capability;
            public int StartCalls => inner.StartCalls;
            public int StopCalls => inner.StopCalls;
            public TaskCompletionSource StopEntered => inner.StopEntered;

            public Task StartForegroundAsync(
                NearbyRadioSession request,
                CancellationToken cancellationToken) =>
                inner.StartForegroundAsync(request, cancellationToken);

            public Task StopAsync(
                NearbyStopReason reason,
                CancellationToken cancellationToken) =>
                inner.StopAsync(reason, cancellationToken);
        }
    }

    private sealed class C3Platform : INearbyPlatformState
    {
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
            add { }
            remove { }
        }
    }

    private sealed class C3Radio : INearbyRadioAdapter
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

    private sealed class C3Clock : INearbyClock
    {
        private readonly object sync = new();
        private readonly List<DelayWaiter> waiters = [];
        private TimeSpan monotonicNow;

        public bool ThrowUtc { get; set; }
        public bool ThrowMonotonic { get; set; }
        public DateTimeOffset UtcNow => ThrowUtc
            ? throw new InvalidOperationException("utc-secret")
            : DateTimeOffset.Parse("2026-07-19T00:00:00Z") + monotonicNow;
        public TimeSpan MonotonicNow => ThrowMonotonic
            ? throw new InvalidOperationException("monotonic-secret")
            : monotonicNow;

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

    private sealed class C3Intent : INearbyModeIntentStore
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
