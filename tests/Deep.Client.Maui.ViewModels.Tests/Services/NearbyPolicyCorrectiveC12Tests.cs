using System.Reflection;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyPolicyCorrectiveC12Tests
{
    [Theory]
    [InlineData("suppressed")]
    [InlineData("unsafe")]
    [InlineData("thread")]
    public async Task CallbackSpawnedDisposeWithoutFlowReturnsNonblocking(
        string dispatch)
    {
        NearbyPlatformSubscription? subscription = null;
        Task<Exception?>? nestedDispose = null;
        var nestedReturnedInsideCallback = false;
        var unsubscribeCalls = 0;
        subscription = new NearbyPlatformSubscription(() =>
        {
            Interlocked.Increment(ref unsubscribeCalls);
            var completion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            nestedDispose = completion.Task;
            switch (dispatch)
            {
                case "suppressed":
                    using (ExecutionContext.SuppressFlow())
                    {
                        _ = Task.Run(() => completion.TrySetResult(
                            Record.Exception(subscription!.Dispose)));
                    }

                    break;
                case "unsafe":
                    ThreadPool.UnsafeQueueUserWorkItem(
                        _ => completion.TrySetResult(
                            Record.Exception(subscription!.Dispose)),
                        state: null);
                    break;
                case "thread":
                    var thread = new Thread(() =>
                        completion.TrySetResult(
                            Record.Exception(subscription!.Dispose)));
                    using (ExecutionContext.SuppressFlow())
                    {
                        thread.Start();
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(dispatch));
            }

            nestedReturnedInsideCallback = completion.Task.Wait(
                TimeSpan.FromMilliseconds(500));
        });

        subscription.Dispose();
        var nestedFailure = await nestedDispose!.WaitAsync(
            TimeSpan.FromSeconds(2));
        subscription.Dispose();

        Assert.True(nestedReturnedInsideCallback);
        Assert.Null(nestedFailure);
        Assert.Equal(1, unsubscribeCalls);
    }

    [Fact]
    public async Task StopPublishesPendingOffBeforeBoundedCompletion()
    {
        var environment = new C12Environment();
        var coordinator = environment.CreateCoordinator();
        var starting = coordinator.StartAsync(NearbyUserMode.ChargingHub);
        await environment.Intent.ActiveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var stopping = coordinator.StopAsync();
        await environment.Radio.StopEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshotBeforeDrain = coordinator.Snapshot;
        var intentGeneration = typeof(NearbyPolicyCoordinator).GetField(
            "intentGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(intentGeneration);
        Assert.Equal(2, intentGeneration.GetValue(coordinator));

        var draining = coordinator.DrainAsync();
        await Task.Delay(50);
        Assert.False(draining.IsCompleted);
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            snapshotBeforeDrain.EffectiveState);
        Assert.Equal(
            NearbyIntentPersistenceState.Pending,
            snapshotBeforeDrain.IntentPersistenceState);
        Assert.Equal(1, environment.Radio.StopCalls);

        environment.Intent.ReleaseActive();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await draining.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new NearbyModeIntent(NearbyUserMode.Off, null),
            environment.Intent.Saved);
        Assert.Equal(
            NearbyIntentPersistenceState.Consistent,
            coordinator.Snapshot.IntentPersistenceState);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeStopsRadioBeforeItsInternalDurabilityDrain()
    {
        var environment = new C12Environment();
        var coordinator = environment.CreateCoordinator();
        var starting = coordinator.StartAsync(NearbyUserMode.ChargingHub);
        await environment.Intent.ActiveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var disposing = coordinator.DisposeAsync().AsTask();
        await environment.Radio.StopEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.False(disposing.IsCompleted);
        Assert.Equal(1, environment.Radio.StopCalls);
        Assert.Equal(
            NearbyEffectiveState.Stopped,
            coordinator.Snapshot.EffectiveState);
        Assert.Equal(
            NearbyIntentPersistenceState.Pending,
            coordinator.Snapshot.IntentPersistenceState);

        environment.Intent.ReleaseActive();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new NearbyModeIntent(NearbyUserMode.Off, null),
            environment.Intent.Saved);
        Assert.Equal(
            NearbyIntentPersistenceState.Consistent,
            coordinator.Snapshot.IntentPersistenceState);
    }

    [Fact]
    public async Task DrainPublishesFailedDurabilityForTypedDisposeFailure()
    {
        var environment = new C12Environment();
        var coordinator = environment.CreateCoordinator();
        environment.Intent.ReleaseActive();
        await coordinator.StartAsync(NearbyUserMode.ChargingHub);
        environment.Intent.ThrowOff = true;

        await coordinator.StopAsync();
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            NearbyUserMode.Off,
            coordinator.Snapshot.DesiredMode);
        Assert.Equal(
            NearbyIntentPersistenceState.Failed,
            coordinator.Snapshot.IntentPersistenceState);
        var failure = await Record.ExceptionAsync(() =>
            coordinator.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2)));
        var transition =
            Assert.IsType<NearbyRadioTransitionException>(failure);
        Assert.Equal(
            NearbyRadioTransitionError.IntentCommitFailed,
            transition.Error);

        environment.Intent.ThrowOff = false;
        await coordinator.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2));
    }

    private sealed class C12Environment
    {
        public C12Radio Radio { get; } = new();
        public C12Platform Platform { get; } = new();
        public C12Clock Clock { get; } = new();
        public C12Intent Intent { get; } = new();

        public NearbyPolicyCoordinator CreateCoordinator() =>
            new(Radio, Platform, Clock, Intent);
    }

    private sealed class C12Radio : INearbyRadioAdapter
    {
        public int StopCalls { get; private set; }
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NearbyRadioCapability Capability { get; } =
            new(NearbyRadioSupport.Supported, "test");

        public Task StartForegroundAsync(
            NearbyRadioSession request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class C12Platform : INearbyPlatformState
    {
        public NearbyPlatformSnapshot Snapshot { get; } = new(
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
            _ = handler;
            subscription = new NearbyPlatformSubscription(() => { });
            return true;
        }
    }

    private sealed class C12Clock : INearbyClock
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

    private sealed class C12Intent : INearbyModeIntentStore
    {
        private readonly TaskCompletionSource activeRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ActiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOff { get; set; }
        public NearbyModeIntent? Saved { get; private set; }

        public async Task SaveAsync(
            NearbyModeIntent intent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (intent.Mode != NearbyUserMode.Off)
            {
                ActiveEntered.TrySetResult();
                await activeRelease.Task.ConfigureAwait(false);
            }
            else if (ThrowOff)
            {
                throw new InvalidOperationException("intent-secret");
            }

            Saved = intent;
        }

        public void ReleaseActive() => activeRelease.TrySetResult();
    }
}
