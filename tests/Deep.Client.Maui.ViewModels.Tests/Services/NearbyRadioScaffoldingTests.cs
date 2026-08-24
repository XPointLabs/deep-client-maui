using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class NearbyRadioScaffoldingTests
{
    [Fact]
    public void ReadinessRequiresExplicitProtocolActivationEvenWhenDeviceIsReady()
    {
        var readiness = Ready(activationAllowed: false);

        Assert.True(readiness.HasAllRequiredPermissions);
        Assert.False(readiness.CanOpenDiscovery);
    }

    [Fact]
    public void OpaqueHintHasExactSizeAndOwnsItsBytes()
    {
        var source = Enumerable.Range(0, NearbyDiscoveryLimits.EphemeralHintSizeBytes)
            .Select(static value => (byte)value)
            .ToArray();
        var observation = new NearbyOpaqueDiscoveryObservation(source);
        source[0] = byte.MaxValue;

        Assert.Equal(0, observation.EphemeralHint[0]);
        Assert.Throws<ArgumentException>(() =>
            new NearbyOpaqueDiscoveryObservation(new byte[31]));
        Assert.Throws<ArgumentException>(() =>
            new NearbyOpaqueDiscoveryObservation(new byte[33]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(31, 0)]
    [InlineData(31, 65)]
    public void RequestRejectsUnboundedDiscovery(int seconds, int observations)
    {
        var request = new NearbyOpaqueDiscoveryRequest(
            NearbyRadioTechnology.BluetoothLowEnergy,
            TimeSpan.FromSeconds(seconds),
            observations);

        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    [Fact]
    public async Task RunnerNeverOpensAdapterWhenActivationIsBlocked()
    {
        var adapter = new FakeAdapter(Ready(activationAllowed: false));
        var runner = new BoundedNearbyDiscoveryRunner(adapter);

        await Assert.ThrowsAsync<NearbyRadioUnavailableException>(() =>
            runner.RunAsync(Request(maximumObservations: 2), Observe));

        Assert.Equal(0, adapter.OpenCount);
    }

    [Fact]
    public async Task RunnerStopsAndDisposesAtObservationBound()
    {
        var adapter = new FakeAdapter(Ready(activationAllowed: true));
        var runner = new BoundedNearbyDiscoveryRunner(adapter);
        var observed = 0;

        await runner.RunAsync(
            Request(maximumObservations: 2),
            (_, _) =>
            {
                Interlocked.Increment(ref observed);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(2, observed);
        Assert.Equal(1, adapter.OpenCount);
        Assert.NotNull(adapter.Session);
        Assert.Equal(1, adapter.Session!.StopCount);
        Assert.Equal(1, adapter.Session.DisposeCount);
    }

    [Fact]
    public async Task RunnerStopsAndDisposesAfterBoundedDuration()
    {
        var adapter = new FakeAdapter(
            Ready(activationAllowed: true),
            emitObservations: false);
        var runner = new BoundedNearbyDiscoveryRunner(adapter);
        var request = new NearbyOpaqueDiscoveryRequest(
            NearbyRadioTechnology.WifiAware,
            TimeSpan.FromMilliseconds(25),
            1);

        await runner.RunAsync(request, Observe);

        Assert.NotNull(adapter.Session);
        Assert.Equal(1, adapter.Session!.StopCount);
        Assert.Equal(1, adapter.Session.DisposeCount);
    }

    [Fact]
    public async Task RunnerReturnsAtDurationWhenAdapterOpenIgnoresCancellation()
    {
        var adapter = new NeverOpeningAdapter();
        var runner = new BoundedNearbyDiscoveryRunner(adapter);
        var request = new NearbyOpaqueDiscoveryRequest(
            NearbyRadioTechnology.WifiDirect,
            TimeSpan.FromMilliseconds(25),
            1);

        await runner.RunAsync(request, Observe)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, adapter.OpenCount);
    }

    [Fact]
    public async Task RunnerReturnsAtDurationWhenObserverIgnoresCancellation()
    {
        var adapter = new FakeAdapter(Ready(activationAllowed: true));
        var runner = new BoundedNearbyDiscoveryRunner(adapter);
        var request = new NearbyOpaqueDiscoveryRequest(
            NearbyRadioTechnology.BluetoothLowEnergy,
            TimeSpan.FromMilliseconds(25),
            1);
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await runner.RunAsync(
                request,
                (_, _) => new ValueTask(never.Task))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(adapter.Session);
        Assert.Equal(1, adapter.Session!.StopCount);
        Assert.Equal(1, adapter.Session.DisposeCount);
    }

    [Fact]
    public async Task RunnerStillDisposesSessionWhenStopFails()
    {
        var adapter = new FakeAdapter(
            Ready(activationAllowed: true),
            emitObservations: false,
            stopFails: true);
        var runner = new BoundedNearbyDiscoveryRunner(adapter);
        var request = new NearbyOpaqueDiscoveryRequest(
            NearbyRadioTechnology.WifiDirect,
            TimeSpan.FromMilliseconds(25),
            1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(request, Observe));

        Assert.NotNull(adapter.Session);
        Assert.Equal(1, adapter.Session!.StopCount);
        Assert.Equal(1, adapter.Session.DisposeCount);
    }

    private static NearbyOpaqueDiscoveryRequest Request(int maximumObservations) =>
        new(
            NearbyRadioTechnology.BluetoothLowEnergy,
            TimeSpan.FromSeconds(1),
            maximumObservations);

    private static ValueTask Observe(
        NearbyOpaqueDiscoveryObservation _,
        CancellationToken __) => ValueTask.CompletedTask;

    private static NearbyRadioReadiness Ready(bool activationAllowed) =>
        new(
            NearbyRadioTechnology.BluetoothLowEnergy,
            NearbyRadioHardwareState.Ready,
            [
                new NearbyPermissionStatus(
                    NearbyPermissionKind.BluetoothScan,
                    NearbyPermissionDisposition.Granted)
            ],
            activationAllowed,
            activationAllowed ? string.Empty : "disabled");

    private sealed class FakeAdapter(
        NearbyRadioReadiness readiness,
        bool emitObservations = true,
        bool stopFails = false) : INearbyOpaqueDiscoveryAdapter
    {
        public int OpenCount { get; private set; }
        public FakeSession? Session { get; private set; }

        public Task<NearbyRadioReadiness> InspectAsync(
            NearbyRadioTechnology technology,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(readiness with { Technology = technology });
        }

        public Task<INearbyOpaqueDiscoverySession> OpenAsync(
            NearbyOpaqueDiscoveryRequest request,
            Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            Session = new FakeSession(
                request,
                observation,
                cancellationToken,
                emitObservations,
                stopFails);
            return Task.FromResult<INearbyOpaqueDiscoverySession>(Session);
        }
    }

    private sealed class NeverOpeningAdapter : INearbyOpaqueDiscoveryAdapter
    {
        public int OpenCount { get; private set; }

        public Task<NearbyRadioReadiness> InspectAsync(
            NearbyRadioTechnology technology,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Ready(activationAllowed: true) with
            {
                Technology = technology
            });
        }

        public Task<INearbyOpaqueDiscoverySession> OpenAsync(
            NearbyOpaqueDiscoveryRequest request,
            Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return new TaskCompletionSource<INearbyOpaqueDiscoverySession>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class FakeSession : INearbyOpaqueDiscoverySession
    {
        private readonly CancellationTokenSource stopped = new();

        public FakeSession(
            NearbyOpaqueDiscoveryRequest request,
            Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
            CancellationToken operationToken,
            bool emitObservations,
            bool stopFails)
        {
            StopFails = stopFails;
            Completion = RunAsync(
                request,
                observation,
                operationToken,
                emitObservations);
        }

        public Task Completion { get; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        private bool StopFails { get; }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            stopped.Cancel();
            if (StopFails)
            {
                throw new InvalidOperationException("synthetic stop failure");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            stopped.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task RunAsync(
            NearbyOpaqueDiscoveryRequest request,
            Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
            CancellationToken operationToken,
            bool emitObservations)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                operationToken,
                stopped.Token);
            await Task.Yield();
            if (emitObservations)
            {
                for (var index = 0; index <= request.MaximumObservations; index++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await observation(
                        new NearbyOpaqueDiscoveryObservation(
                            new byte[NearbyDiscoveryLimits.EphemeralHintSizeBytes]),
                        linked.Token);
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
    }
}
