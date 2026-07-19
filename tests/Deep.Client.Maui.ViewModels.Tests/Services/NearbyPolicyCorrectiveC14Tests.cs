using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC14Tests
{
    [Fact]
    public async Task FailedDurabilityAfterUnsubscribeKeepsNormalOperationsClosed()
    {
        var radio = new C14Radio();
        var platform = new C14Platform();
        var intent = new C14IntentStore();
        var coordinator = new NearbyPolicyCoordinator(
            radio,
            platform,
            new C14Clock(),
            intent);
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        intent.FailOff = true;

        var failure = await Record.ExceptionAsync(() =>
            coordinator.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2)));
        var transition =
            Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(
            NearbyRadioTransitionError.IntentCommitFailed,
            transition.Error);
        Assert.Equal(1, radio.StopCalls);
        Assert.Equal(1, platform.UnsubscribeCalls);
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
        Assert.Equal(
            NearbyIntentPersistenceState.Failed,
            coordinator.Snapshot.IntentPersistenceState);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.RefreshAsync());
        Assert.Equal(1, radio.StartCalls);

        intent.FailOff = false;
        await coordinator.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(
            new NearbyModeIntent(NearbyUserMode.Off, null),
            intent.Saved);
        Assert.Equal(
            NearbyIntentPersistenceState.Consistent,
            coordinator.Snapshot.IntentPersistenceState);
        Assert.Equal(1, platform.UnsubscribeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.StartAsync(NearbyUserMode.ChargingHub));
    }

    [Fact]
    public async Task AdmittedEventCannotBeOvertakenByDisposeBeforeQueueAppend()
    {
        var platform = new C14Platform();
        var coordinator = new NearbyPolicyCoordinator(
            new C14Radio(),
            platform,
            new C14Clock());
        var hookProperty = typeof(NearbyPolicyCoordinator).GetProperty(
            "PlatformEventAdmittedBeforeEnqueueForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hookProperty);
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        hookProperty.SetValue(coordinator, new Action(() =>
        {
            admitted.Set();
            release.Wait();
        }));

        var raiseThread = new Thread(platform.RaiseCaptured);
        using (ExecutionContext.SuppressFlow())
        {
            raiseThread.Start();
        }
        Assert.True(admitted.Wait(TimeSpan.FromSeconds(1)));

        var disposing = Task.Run(async () =>
            await coordinator.DisposeAsync());
        await Task.Delay(100);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, platform.UnsubscribeCalls);

        release.Set();
        Assert.True(raiseThread.Join(TimeSpan.FromSeconds(1)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        var transitionField = typeof(NearbyPolicyCoordinator).GetField(
            "lastPlatformTransition",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(transitionField);
        var transition = Assert.IsAssignableFrom<Task>(
            transitionField.GetValue(coordinator));
        Assert.True(transition.IsCompletedSuccessfully);
        Assert.Equal(1, platform.UnsubscribeCalls);
    }

    private sealed class C14Radio : INearbyRadioAdapter
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            _ = request;
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

    private sealed class C14Platform : INearbyPlatformState
    {
        private EventHandler<NearbyPlatformSnapshot>? changed;
        private EventHandler<NearbyPlatformSnapshot>? captured;

        public int UnsubscribeCalls { get; private set; }
        public NearbyPlatformSnapshot Snapshot => Current;

        public bool TrySubscribe(
            EventHandler<NearbyPlatformSnapshot> handler,
            out INearbyPlatformSubscription? subscription)
        {
            changed += handler;
            captured = handler;
            subscription = new NearbyPlatformSubscription(() =>
            {
                UnsubscribeCalls++;
                changed -= handler;
            });
            return true;
        }

        public void RaiseCaptured() => captured?.Invoke(this, Current);

        private static NearbyPlatformSnapshot Current { get; } = new(
            IsForeground: true,
            HasRequiredPermission: true,
            IsBluetoothEnabled: true,
            IsLocationAvailable: true,
            IsCharging: true,
            BatteryPercent: 80,
            ThermalState: NearbyThermalState.Nominal);
    }

    private sealed class C14IntentStore : INearbyModeIntentStore
    {
        public bool FailOff { get; set; }
        public NearbyModeIntent? Saved { get; private set; }

        public Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOff && intent.Mode == NearbyUserMode.Off)
            {
                throw new InvalidOperationException("intent-secret");
            }

            Saved = intent;
            return Task.CompletedTask;
        }
    }

    private sealed class C14Clock : INearbyClock
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
}
