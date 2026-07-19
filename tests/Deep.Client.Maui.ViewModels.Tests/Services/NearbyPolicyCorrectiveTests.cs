using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveTests
{
    [Fact]
    public async Task NewStartIsBusyUntilPriorPhysicalStopCompletes()
    {
        var environment = new CorrectiveEnvironment();
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Radio.BlockStop = true;

        var stopping = coordinator.StopAsync();
        await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStart = coordinator.StartAsync(NearbyUserMode.ChargingHub);
        var observed = await Record.ExceptionAsync(
            () => secondStart.WaitAsync(TimeSpan.FromMilliseconds(200)));

        environment.Radio.ReleaseStop();
        await stopping;
        try
        {
            await secondStart;
        }
        catch
        {
        }

        Assert.Equal("NearbyCoordinatorBusyException", observed?.GetType().Name);
        Assert.Equal(["start", "stop"], environment.Radio.Calls);

        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        Assert.Equal(["start", "stop", "start"], environment.Radio.Calls);
    }

    [Fact]
    public async Task DeniedAndBusyStartsDoNotOverwriteActiveSnapshotOrIntent()
    {
        var environment = new CorrectiveEnvironment();
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        var active = coordinator.Snapshot;
        var saved = environment.IntentStore.Saved;

        var busy = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        Assert.Equal("NearbyCoordinatorBusyException", busy?.GetType().Name);
        Assert.Equal(active, coordinator.Snapshot);
        Assert.Equal(saved, environment.IntentStore.Saved);

        environment.Platform.Set(environment.Platform.Snapshot with
        {
            BatteryPercent = NearbyPolicyConstants.StartBatteryPercent
        }, raiseChanged: false);
        var deniedWhileBusy = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        Assert.Equal(
            "NearbyCoordinatorBusyException",
            deniedWhileBusy?.GetType().Name);
        Assert.Equal(active, coordinator.Snapshot);
        Assert.Equal(saved, environment.IntentStore.Saved);
    }

    [Fact]
    public async Task AdapterIgnoringCallerCancellationCannotPromoteActive()
    {
        var environment = new CorrectiveEnvironment();
        environment.Radio.BlockStart = true;
        environment.Radio.IgnoreStartCancellation = true;
        await using var coordinator = environment.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var starting = coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency,
            cancellationToken: cancellation.Token);
        await environment.Radio.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        environment.Radio.ReleaseStart();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await starting);
        Assert.NotEqual(
            NearbyEffectiveState.Active,
            coordinator.Snapshot.EffectiveState);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task ConcurrentDisposeDrainsQueuedStopAndIsIdempotent()
    {
        var environment = new CorrectiveEnvironment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Radio.BlockStop = true;
        environment.Platform.Set(environment.Platform.Snapshot with
        {
            IsForeground = false
        });
        await environment.Radio.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => coordinator.DisposeAsync().AsTask())
            .ToArray();
        bool allJoinedPhysicalStop;
        Exception?[] errors;
        try
        {
            await Task.Delay(100);
            allJoinedPhysicalStop = disposals.All(
                disposal => !disposal.IsCompleted);
        }
        finally
        {
            environment.Radio.ReleaseStop();
            errors = await Task.WhenAll(disposals.Select(async disposal =>
                await Record.ExceptionAsync(() =>
                    disposal.WaitAsync(TimeSpan.FromSeconds(2)))));
        }

        Assert.True(allJoinedPhysicalStop);
        Assert.All(errors, Assert.Null);
        Assert.Equal(1, environment.Radio.StopCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
    }

    [Fact]
    public async Task AdapterStartReentrancyFailsFastWithoutDeadlock()
    {
        var environment = new CorrectiveEnvironment();
        var coordinator = environment.CreateCoordinator();
        Exception? callbackError = null;
        environment.Radio.OnStart = async () =>
        {
            callbackError = await Record.ExceptionAsync(() =>
                coordinator.StopAsync());
        };

        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            "NearbyCoordinatorReentrancyException",
            callbackError?.GetType().Name);
        Assert.Equal(NearbyEffectiveState.Active, coordinator.Snapshot.EffectiveState);
        environment.Radio.OnStart = null;
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AdapterStopReentrancyFailsFastWithoutDeadlock()
    {
        var environment = new CorrectiveEnvironment();
        var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        Exception? callbackError = null;
        environment.Radio.OnStop = async () =>
        {
            callbackError = await Record.ExceptionAsync(() =>
                coordinator.StartAsync(NearbyUserMode.ChargingHub));
        };

        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            "NearbyCoordinatorReentrancyException",
            callbackError?.GetType().Name);
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
        environment.Radio.OnStop = null;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public void UndefinedModeAndThermalValuesFailClosed()
    {
        var environment = new CorrectiveEnvironment();

        var mode = NearbyPolicyEvaluator.Evaluate(
            (NearbyUserMode)999,
            environment.Platform.Snapshot,
            environment.Radio.Capability,
            isActive: false);
        var thermal = NearbyPolicyEvaluator.Evaluate(
            NearbyUserMode.ForegroundEmergency,
            environment.Platform.Snapshot with
            {
                ThermalState = (NearbyThermalState)999
            },
            environment.Radio.Capability,
            isActive: false,
            NearbyPolicyConstants.DefaultEmergencyDuration);

        Assert.False(mode.MayStart);
        Assert.False(thermal.MayStart);
        Assert.Equal(NearbyPolicyDenialReason.Unsupported, mode.DenialReason);
        Assert.Equal(NearbyPolicyDenialReason.ThermalUnknown, thermal.DenialReason);
    }

    [Fact]
    public async Task IntentCommitsOnlyAfterSuccessfulAuthoritativeStart()
    {
        var environment = new CorrectiveEnvironment();
        environment.Radio.BlockStart = true;
        await using var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await environment.Radio.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(environment.IntentStore.Saved);
        environment.Radio.ThrowAfterStartRelease = true;
        environment.Radio.ReleaseStart();

        await Assert.ThrowsAsync<NearbyRadioTransitionException>(
            async () => await starting);
        Assert.Null(environment.IntentStore.Saved);
        Assert.NotEqual(
            NearbyEffectiveState.Active,
            coordinator.Snapshot.EffectiveState);
    }

    [Fact]
    public async Task StopDuringIntentCommitRollsBackBeforeTransitionCompletes()
    {
        var environment = new CorrectiveEnvironment();
        environment.IntentStore.BlockActiveSave = true;
        await using var coordinator = environment.CreateCoordinator();

        var starting = coordinator.StartAsync(
            NearbyUserMode.ForegroundEmergency);
        await environment.IntentStore.SaveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        var stopping = coordinator.StopAsync();
        environment.IntentStore.ReleaseSave();
        await Task.WhenAll(starting, stopping);

        Assert.Equal(
            new NearbyModeIntent(NearbyUserMode.Off, null),
            environment.IntentStore.Saved);
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task DeadlineSchedulerFaultForcesBoundedStop()
    {
        var environment = new CorrectiveEnvironment();
        environment.Clock.DelayException =
            new InvalidOperationException("scheduler detail must stay private");
        await using var coordinator = environment.CreateCoordinator();

        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(
            NearbyEffectiveState.Active,
            coordinator.Snapshot.EffectiveState);
        Assert.Equal(
            "DeadlineFailure",
            coordinator.Snapshot.StopReason?.ToString());
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task RefreshEnforcesExpiredMonotonicDeadlineWhenDelayNeverCompletes()
    {
        var environment = new CorrectiveEnvironment();
        environment.Clock.DelayNeverCompletes = true;
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);

        environment.Clock.Advance(
            NearbyPolicyConstants.DefaultEmergencyDuration +
            TimeSpan.FromSeconds(1));
        await coordinator.RefreshAsync();

        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
        Assert.Equal(NearbyStopReason.DeadlineExpired, coordinator.Snapshot.StopReason);
        Assert.Equal(1, environment.Radio.StopCalls);
    }

    [Fact]
    public async Task PublicTransitionExceptionDoesNotExposeAdapterException()
    {
        var environment = new CorrectiveEnvironment();
        environment.Radio.StartException =
            new InvalidOperationException("account payload radio-id secret");
        await using var coordinator = environment.CreateCoordinator();

        var failure = await Assert.ThrowsAsync<NearbyRadioTransitionException>(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));

        var error = failure.GetType().GetProperty("Error")?.GetValue(failure);
        Assert.Equal("AdapterStartFailed", error?.ToString());
        Assert.Null(failure.InnerException);
        Assert.Equal(
            "Nearby radio start failed; effective state is not active.",
            failure.Message);
        Assert.DoesNotContain("account", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PhysicalStopFailureIsReportedAsUncertainNotStopped()
    {
        var environment = new CorrectiveEnvironment();
        await using var coordinator = environment.CreateCoordinator();
        await coordinator.StartAsync(NearbyUserMode.ForegroundEmergency);
        environment.Radio.StopException =
            new InvalidOperationException("physical stop failed");

        await coordinator.StopAsync();

        Assert.Equal(
            "StopFailed",
            coordinator.Snapshot.EffectiveState.ToString());
        Assert.Equal(
            "StopFailed",
            coordinator.Snapshot.StopReason?.ToString());
        Assert.Equal(NearbyUserMode.Off, coordinator.Snapshot.DesiredMode);
        Assert.False(coordinator.Snapshot.Polling.SuppressManagedNetworkPolling);

        var blocked = await Record.ExceptionAsync(() =>
            coordinator.StartAsync(NearbyUserMode.ForegroundEmergency));
        Assert.Equal(
            "NearbyCoordinatorBusyException",
            blocked?.GetType().Name);

        environment.Radio.StopException = null;
        await coordinator.StopAsync();
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
    }

    private sealed class CorrectiveEnvironment
    {
        public CorrectiveRadio Radio { get; } = new();
        public CorrectivePlatform Platform { get; } = new();
        public CorrectiveClock Clock { get; } = new();
        public CorrectiveIntentStore IntentStore { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, IntentStore);
    }

    private sealed class CorrectiveIntentStore : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource saveRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyModeIntent? Saved { get; private set; }
        public bool BlockActiveSave { get; set; }
        public TaskCompletionSource SaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockActiveSave && intent.Mode != NearbyUserMode.Off)
            {
                SaveEntered.TrySetResult();
                await saveRelease.Task;
            }

            Saved = intent;
        }

        public void ReleaseSave() => saveRelease.TrySetResult();
    }

    private sealed class CorrectivePlatform : INearbyPlatformState
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

        public void Set(
            NearbyPlatformSnapshot value,
            bool raiseChanged = true)
        {
            Snapshot = value;
            if (raiseChanged)
            {
                Changed?.Invoke(this, value);
            }
        }
    }

    private sealed class CorrectiveClock : INearbyClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            DateTimeOffset.Parse("2026-07-19T00:00:00Z");

        public TimeSpan MonotonicNow { get; private set; }
        public Exception? DelayException { get; set; }
        public bool DelayNeverCompletes { get; set; }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            if (DelayException is not null)
            {
                return Task.FromException(DelayException);
            }

            if (DelayNeverCompletes)
            {
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Advance(TimeSpan amount)
        {
            MonotonicNow += amount;
            UtcNow += amount;
        }
    }

    private sealed class CorrectiveRadio : INearbyRadioAdapter
    {
        private readonly TaskCompletionSource startRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");

        public List<string> Calls { get; } = [];
        public int StopCalls { get; private set; }
        public bool BlockStart { get; set; }
        public bool BlockStop { get; set; }
        public bool IgnoreStartCancellation { get; set; }
        public bool ThrowAfterStartRelease { get; set; }
        public Exception? StartException { get; set; }
        public Exception? StopException { get; set; }
        public Func<Task>? OnStart { get; set; }
        public Func<Task>? OnStop { get; set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Calls.Add("start");
            StartEntered.TrySetResult();
            if (OnStart is not null)
            {
                await OnStart();
            }

            if (StartException is not null)
            {
                throw StartException;
            }

            if (BlockStart)
            {
                if (IgnoreStartCancellation)
                {
                    await startRelease.Task;
                }
                else
                {
                    await startRelease.Task.WaitAsync(cancellationToken);
                }
            }

            if (ThrowAfterStartRelease)
            {
                throw new InvalidOperationException("late start failure");
            }
        }

        public async Task StopAsync(
            NearbyStopReason reason,
            CancellationToken cancellationToken)
        {
            _ = reason;
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("stop");
            StopCalls++;
            StopEntered.TrySetResult();
            if (OnStop is not null)
            {
                await OnStop();
            }

            if (BlockStop)
            {
                await stopRelease.Task;
            }

            if (StopException is not null)
            {
                throw StopException;
            }
        }

        public void ReleaseStart() => startRelease.TrySetResult();

        public void ReleaseStop() => stopRelease.TrySetResult();
    }
}
