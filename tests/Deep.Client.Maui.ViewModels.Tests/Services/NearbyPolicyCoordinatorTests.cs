using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCoordinatorTests
{
    [Fact]
    public async Task DefaultRestartAndLifecycleSignalsNeverRestoreSavedIntent()
    {
        var environment = new TestEnvironment();
        environment.IntentStore.Saved = new NearbyModeIntent(
            NearbyUserMode.ForegroundEmergency,
            NearbyPolicyConstants.DefaultEmergencyDuration);

        await using var coordinator = environment.CreateCoordinator();
        for (var signal = 0; signal < 4; signal++)
        {
            environment.Platform.Set(environment.Platform.Snapshot);
        }

        await coordinator.DrainAsync();

        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        Assert.Equal(0, environment.Radio.StartCalls);
    }

    [Fact]
    public async Task ForegroundEmergencyStartsStopsAndExpiresAtMonotonicDeadline()
    {
        var environment = new TestEnvironment();
        await using var coordinator = environment.CreateCoordinator();

        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);

        Assert.Equal(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);
        Assert.True(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        Assert.Equal(
            environment.Clock.MonotonicNow + NearbyPolicyConstants.DefaultEmergencyDuration,
            environment.Radio.LastSession!.EmergencyDeadline);

        environment.Clock.RollUtcBack(TimeSpan.FromHours(2));
        environment.Clock.Advance(NearbyPolicyConstants.DefaultEmergencyDuration);
        await coordinator.DrainAsync();

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyStopReason.DeadlineExpired, coordinator.Snapshot.StopReason);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Theory]
    [InlineData("background")]
    [InlineData("permission")]
    [InlineData("bluetooth")]
    [InlineData("location")]
    [InlineData("battery")]
    [InlineData("thermal")]
    public async Task UnsafePlatformTransitionStopsExactlyOnce(string transition)
    {
        var environment = new TestEnvironment();
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);

        environment.Platform.Set(transition switch
        {
            "background" => environment.Platform.Snapshot with { IsForeground = false },
            "permission" => environment.Platform.Snapshot with { HasRequiredPermission = false },
            "bluetooth" => environment.Platform.Snapshot with { IsBluetoothEnabled = false },
            "location" => environment.Platform.Snapshot with { IsLocationAvailable = false },
            "battery" => environment.Platform.Snapshot with { BatteryPercent = 15 },
            "thermal" => environment.Platform.Snapshot with
            {
                ThermalState = NearbyThermalState.Severe
            },
            _ => throw new ArgumentOutOfRangeException(nameof(transition))
        });
        await coordinator.DrainAsync();
        environment.Platform.Set(environment.Platform.Snapshot);
        await coordinator.DrainAsync();

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task ChargingHubRequiresPowerAndStopsOnUnplug()
    {
        var environment = new TestEnvironment();
        await using var coordinator = environment.CreateCoordinator();

        environment.Platform.Set(environment.Platform.Snapshot with { IsCharging = false });
        await Assert.ThrowsAsync<NearbyPolicyDeniedException>(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        Assert.Equal(0, environment.Radio.StartCalls);

        environment.Platform.Set(environment.Platform.Snapshot with { IsCharging = true });
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        Assert.Equal(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);

        environment.Platform.Set(environment.Platform.Snapshot with { IsCharging = false });
        await coordinator.DrainAsync();
        Assert.Equal(NearbyStopReason.PowerLost, coordinator.Snapshot.StopReason);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Theory]
    [InlineData(20, NearbyThermalState.Nominal, NearbyRadioSupport.Supported, NearbyPolicyDenialReason.BatteryAdmissionDenied)]
    [InlineData(80, NearbyThermalState.Unknown, NearbyRadioSupport.Supported, NearbyPolicyDenialReason.ThermalUnknown)]
    [InlineData(80, NearbyThermalState.Nominal, NearbyRadioSupport.Unsupported, NearbyPolicyDenialReason.Unsupported)]
    public async Task AdmissionGatesFailClosed(
        int battery,
        NearbyThermalState thermal,
        NearbyRadioSupport support,
        NearbyPolicyDenialReason expected)
    {
        var environment = new TestEnvironment();
        environment.Platform.Set(environment.Platform.Snapshot with
        {
            BatteryPercent = battery,
            ThermalState = thermal
        });
        environment.Radio.CapabilityValue = new NearbyRadioCapability(
            support,
            "unavailable");
        await using var coordinator = environment.CreateCoordinator();

        var denied = await Assert.ThrowsAsync<NearbyPolicyDeniedException>(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));

        Assert.Equal(expected, denied.Reason);
        Assert.Equal(0, environment.Radio.StartCalls);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
    }

    [Fact]
    public async Task LateObsoleteStartCannotReactivateAfterConcurrentStop()
    {
        var environment = new TestEnvironment();
        environment.Radio.BlockStart = true;
        environment.Radio.IgnoreStartCancellation = true;
        await using var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await environment.Radio.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(NearbyEffectiveState.Starting, coordinator.Snapshot.EffectiveState);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        var stopping = coordinator.StopAsync();
        environment.Radio.ReleaseStart();
        await Task.WhenAll(starting, stopping);

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        Assert.Equal(1, environment.Radio.StartCalls);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(1, environment.Radio.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task CallerCancellationStopsAttemptAndDoesNotPersistActivity()
    {
        var environment = new TestEnvironment();
        environment.Radio.BlockStart = true;
        await using var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency,
            cancellationToken: cancellation.Token);
        await environment.Radio.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await starting);
        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task CapabilityChangeStopsActiveAttemptFailClosed()
    {
        var environment = new TestEnvironment();
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);

        environment.Radio.CapabilityValue = new NearbyRadioCapability(
            NearbyRadioSupport.DisabledPendingReview,
            "review required");
        await coordinator.RefreshAsync();

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyStopReason.CapabilityChanged, coordinator.Snapshot.StopReason);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AdapterExceptionsLeaveObservableStateStopped(
        bool throwOnStart,
        bool throwOnStop)
    {
        var environment = new TestEnvironment();
        environment.Radio.ThrowOnStart = throwOnStart;
        environment.Radio.ThrowOnStop = throwOnStop;
        await using var coordinator = environment.CreateCoordinator();

        if (throwOnStart)
        {
            await Assert.ThrowsAsync<NearbyRadioTransitionException>(() =>
                coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        }
        else
        {
            await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
            await coordinator.StopAsync();
        }

        Assert.Equal(NearbyEffectiveState.Stopped, coordinator.Snapshot.EffectiveState);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
    }

    [Fact]
    public async Task SettingsPersistValidatedIntentOnlyWithoutRuntimeState()
    {
        var environment = new TestEnvironment();
        await using var coordinator = environment.CreateCoordinator();

        await Assert.ThrowsAsync<NearbyPolicyDeniedException>(() =>
            coordinator.StartAsync(
                NearbyUserMode.ForegroundEmergency,
                TimeSpan.FromMinutes(16)));
        Assert.Null(environment.IntentStore.Saved);

        await coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency,
            TimeSpan.FromMinutes(30));

        Assert.Equal(
            new NearbyModeIntent(
                NearbyUserMode.ForegroundEmergency,
                TimeSpan.FromMinutes(30)),
            environment.IntentStore.Saved);
        Assert.DoesNotContain(
            environment.IntentStore.Saved!.GetType().GetProperties(),
            property => property.Name.Contains("Deadline", StringComparison.Ordinal));
        Assert.DoesNotContain(
            environment.IntentStore.Saved.GetType().GetProperties(),
            property => property.Name.Contains("Active", StringComparison.Ordinal));

        await coordinator.StopAsync();
        await coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency,
            NearbyPolicyConstants.MaximumEmergencyDuration);
        Assert.Equal(
            NearbyPolicyConstants.MaximumEmergencyDuration,
            environment.Radio.LastSession!.EmergencyDeadline -
            environment.Clock.MonotonicNow);
    }

    [Fact]
    public async Task DisabledPlatformShellsFailClosedAndHaveNoRadioDependencies()
    {
        var android = new DisabledAndroidNearbyRadioAdapter();
        var windows = new UnsupportedWindowsNearbyRadioAdapter();
        var request = new NearbyRadioSession(
            NearbyUserMode.ForegroundEmergency,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(15));

        Assert.True(typeof(DisabledAndroidNearbyRadioAdapter).IsSealed);
        Assert.Equal(
            NearbyRadioSupport.DisabledPendingReview,
            android.Capability.Support);
        Assert.Equal(NearbyRadioSupport.Unsupported, windows.Capability.Support);
        await Assert.ThrowsAsync<NearbyRadioUnavailableException>(() =>
            android.StartForegroundAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<NearbyRadioUnavailableException>(() =>
            windows.StartForegroundAsync(request, CancellationToken.None));
        await android.StopAsync(NearbyStopReason.User, CancellationToken.None);
        await windows.StopAsync(NearbyStopReason.User, CancellationToken.None);

        var dependencies = typeof(DisabledAndroidNearbyRadioAdapter).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty);
        Assert.DoesNotContain(
            dependencies,
            dependency => dependency.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                          dependency.Contains("Wifi", StringComparison.OrdinalIgnoreCase) ||
                          dependency.Contains("Android", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NearbyPolicySurfaceHasNoWalletBillingOrPayloadIdentifiers()
    {
        var forbidden = new[]
        {
            "Wallet", "Billing", "XPNT", "Entitlement", "SessionId",
            "AccountId", "Contact", "Recovery", "Payload", "RadioIdentifier"
        };
        var surface = typeof(NearbyPolicyCoordinator).Assembly.GetTypes()
            .Where(type => type.Namespace == "Deep.Client.Maui.Core.Services" &&
                           type.Name.Contains("Nearby", StringComparison.Ordinal))
            .SelectMany(type => type.GetMembers())
            .Select(static member => member.Name)
            .ToArray();

        Assert.DoesNotContain(
            surface,
            name => forbidden.Any(value =>
                name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class TestEnvironment
    {
        public FakeClock Clock { get; } = new();
        public FakePlatform Platform { get; } = new();
        public FakeRadio Radio { get; } = new();
        public FakeIntentStore IntentStore { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, IntentStore);
    }

    private sealed class FakeIntentStore : INearbyModeIntentStore
    {
        public NearbyModeIntent? Saved { get; set; }

        public Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = intent;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : INearbyPlatformState
    {
        public NearbyPlatformSnapshot Snapshot { get; private set; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);

        public event EventHandler<NearbyPlatformSnapshot>? Changed;

        public void Set(NearbyPlatformSnapshot value)
        {
            Snapshot = value;
            Changed?.Invoke(this, value);
        }
    }

    private sealed class FakeClock : INearbyClock
    {
        private readonly object sync = new();
        private readonly List<DelayWaiter> waiters = [];

        public DateTimeOffset UtcNow { get; private set; } =
            DateTimeOffset.Parse("2026-07-19T00:00:00Z");

        public TimeSpan MonotonicNow { get; private set; }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            lock (sync)
            {
                var waiter = new DelayWaiter(
                    MonotonicNow + delay,
                    new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                waiter.Cancellation = cancellationToken.Register(
                    () => waiter.Completion.TrySetCanceled(cancellationToken));
                waiters.Add(waiter);
                return waiter.Completion.Task;
            }
        }

        public void RollUtcBack(TimeSpan amount) => UtcNow -= amount;

        public void Advance(TimeSpan amount)
        {
            List<DelayWaiter> ready;
            lock (sync)
            {
                MonotonicNow += amount;
                UtcNow += amount;
                ready = waiters
                    .Where(waiter => waiter.Deadline <= MonotonicNow)
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

    private sealed class FakeRadio : INearbyRadioAdapter
    {
        private readonly TaskCompletionSource releaseStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrentCalls;
        private int maximumConcurrentCalls;

        public NearbyRadioCapability CapabilityValue { get; set; } =
            new(NearbyRadioSupport.Supported, "test");

        public NearbyRadioCapability Capability => CapabilityValue;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int MaximumConcurrentCalls => Volatile.Read(
            ref maximumConcurrentCalls);
        public bool BlockStart { get; set; }
        public bool IgnoreStartCancellation { get; set; }
        public bool ThrowOnStart { get; set; }
        public bool ThrowOnStop { get; set; }
        public NearbyRadioSession? LastSession { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            EnterCall();
            StartCalls++;
            LastSession = request;
            StartEntered.TrySetResult();
            try
            {
                if (ThrowOnStart)
                {
                    throw new InvalidOperationException("radio start failed");
                }

                if (BlockStart)
                {
                    if (IgnoreStartCancellation)
                    {
                        await releaseStart.Task;
                    }
                    else
                    {
                        await releaseStart.Task.WaitAsync(cancellationToken);
                    }
                }
            }
            finally
            {
                ExitCall();
            }
        }

        public Task StopAsync(
            NearbyStopReason reason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                StopCalls++;
                return ThrowOnStop
                    ? Task.FromException(
                        new InvalidOperationException("radio stop failed"))
                    : Task.CompletedTask;
            }
            finally
            {
                ExitCall();
            }
        }

        public void ReleaseStart() => releaseStart.TrySetResult();

        private void EnterCall()
        {
            var current = Interlocked.Increment(ref concurrentCalls);
            while (true)
            {
                var maximum = Volatile.Read(ref maximumConcurrentCalls);
                if (current <= maximum ||
                    Interlocked.CompareExchange(
                        ref maximumConcurrentCalls,
                        current,
                        maximum) == maximum)
                {
                    return;
                }
            }
        }

        private void ExitCall() => Interlocked.Decrement(ref concurrentCalls);
    }
}
