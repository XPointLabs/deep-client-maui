using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace Deep.Client.Maui.Core.Services;

public enum NearbyRadioTechnology
{
    BluetoothLowEnergy,
    WifiDirect,
    WifiAware
}

public enum NearbyRadioHardwareState
{
    Unavailable,
    Disabled,
    Ready
}

public enum NearbyPermissionKind
{
    BluetoothScan,
    BluetoothConnect,
    BluetoothAdvertise,
    NearbyWifiDevices,
    LegacyFineLocation
}

public enum NearbyPermissionDisposition
{
    Unknown,
    NotRequired,
    Granted,
    Denied,
    Restricted
}

public sealed record NearbyPermissionStatus(
    NearbyPermissionKind Permission,
    NearbyPermissionDisposition Disposition);

public sealed record NearbyRadioReadiness(
    NearbyRadioTechnology Technology,
    NearbyRadioHardwareState Hardware,
    IReadOnlyList<NearbyPermissionStatus> Permissions,
    bool ProtocolActivationAllowed,
    string Limitation)
{
    public bool HasAllRequiredPermissions =>
        Permissions.Count > 0 &&
        Permissions.All(static permission =>
            permission.Disposition is
                NearbyPermissionDisposition.Granted or
                NearbyPermissionDisposition.NotRequired);

    public bool CanOpenDiscovery =>
        ProtocolActivationAllowed &&
        Hardware == NearbyRadioHardwareState.Ready &&
        HasAllRequiredPermissions;
}

public interface INearbyPermissionBroker
{
    Task<IReadOnlyList<NearbyPermissionStatus>> CheckAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NearbyPermissionStatus>> RequestAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default);
}

public interface INearbyRadioCapabilityProbe
{
    Task<NearbyRadioReadiness> InspectAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default);
}

public sealed class NearbyOpaqueDiscoveryObservation
{
    public NearbyOpaqueDiscoveryObservation(ReadOnlySpan<byte> ephemeralHint)
    {
        if (ephemeralHint.Length != NearbyDiscoveryLimits.EphemeralHintSizeBytes)
        {
            throw new ArgumentException(
                "A nearby discovery hint must have the exact bounded size.",
                nameof(ephemeralHint));
        }

        EphemeralHint = ImmutableArray.Create(ephemeralHint.ToArray());
    }

    // This fixed-size value has no product identity or message semantics. The
    // future reviewed protocol must define rotation and authentication before
    // any native adapter is allowed to emit it.
    public ImmutableArray<byte> EphemeralHint { get; }
}

public sealed record NearbyOpaqueDiscoveryRequest(
    NearbyRadioTechnology Technology,
    TimeSpan Duration,
    int MaximumObservations)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Technology) ||
            Duration <= TimeSpan.Zero ||
            Duration > NearbyDiscoveryLimits.MaximumDuration ||
            MaximumObservations is < 1 or > NearbyDiscoveryLimits.MaximumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NearbyOpaqueDiscoveryRequest),
                "Nearby discovery bounds are invalid.");
        }
    }
}

public static class NearbyDiscoveryLimits
{
    public const int EphemeralHintSizeBytes = 32;
    public const int MaximumObservations = 64;

    public static TimeSpan MaximumDuration => TimeSpan.FromSeconds(30);

    public static TimeSpan CleanupTimeout => TimeSpan.FromSeconds(5);
}

public interface INearbyOpaqueDiscoverySession : IAsyncDisposable
{
    Task Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface INearbyOpaqueDiscoveryAdapter
{
    Task<NearbyRadioReadiness> InspectAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default);

    // Cancellation before a session is returned is failure-atomic: the
    // adapter must retain no scan, advertisement, callback, receiver, or link.
    // After return the owned session must stop callbacks before disposal ends.
    Task<INearbyOpaqueDiscoverySession> OpenAsync(
        NearbyOpaqueDiscoveryRequest request,
        Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
        CancellationToken cancellationToken = default);
}

public sealed class BoundedNearbyDiscoveryRunner(
    INearbyOpaqueDiscoveryAdapter adapter)
{
    private readonly INearbyOpaqueDiscoveryAdapter adapter = adapter ??
        throw new ArgumentNullException(nameof(adapter));

    public async Task RunAsync(
        NearbyOpaqueDiscoveryRequest request,
        Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var readiness = await adapter
            .InspectAsync(request.Technology, cancellationToken)
            .WaitAsync(request.Duration, cancellationToken)
            .ConfigureAwait(false);
        if (!readiness.CanOpenDiscovery)
        {
            throw new NearbyRadioUnavailableException();
        }

        using var duration = new CancellationTokenSource(request.Duration);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            duration.Token);
        var observationCount = 0;
        INearbyOpaqueDiscoverySession? session = null;
        Exception? operationFailure = null;
        try
        {
            session = await adapter.OpenAsync(
                    request,
                    async (candidate, callbackCancellationToken) =>
                    {
                        ArgumentNullException.ThrowIfNull(candidate);
                        var count = Interlocked.Increment(ref observationCount);
                        if (count > request.MaximumObservations)
                        {
                            operation.Cancel();
                            return;
                        }

                        using var callback = CancellationTokenSource.CreateLinkedTokenSource(
                            operation.Token,
                            callbackCancellationToken);
                        await observation(candidate, callback.Token).AsTask()
                            .WaitAsync(callback.Token)
                            .ConfigureAwait(false);
                        if (count == request.MaximumObservations)
                        {
                            operation.Cancel();
                        }
                    },
                    operation.Token)
                .WaitAsync(operation.Token)
                .ConfigureAwait(false);

            try
            {
                await session.Completion.WaitAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                duration.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                // The bounded discovery duration elapsed normally.
            }
            catch (OperationCanceledException) when (
                observationCount >= request.MaximumObservations &&
                !cancellationToken.IsCancellationRequested)
            {
                // The bounded observation capacity was reached normally.
            }
        }
        catch (OperationCanceledException) when (
            duration.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            // The adapter is required to leave no native state when its open
            // operation is cancelled before a session is returned.
        }
        catch (OperationCanceledException) when (
            observationCount >= request.MaximumObservations &&
            !cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            operationFailure = failure;
            throw;
        }
        finally
        {
            if (session is not null)
            {
                Exception? cleanupFailure = null;
                try
                {
                    using var cleanup = new CancellationTokenSource(
                        NearbyDiscoveryLimits.CleanupTimeout);
                    await session.StopAsync(cleanup.Token)
                        .WaitAsync(NearbyDiscoveryLimits.CleanupTimeout)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    cleanupFailure = failure;
                }

                try
                {
                    await session.DisposeAsync().AsTask()
                        .WaitAsync(NearbyDiscoveryLimits.CleanupTimeout)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    cleanupFailure ??= failure;
                }

                if (cleanupFailure is not null && operationFailure is null)
                {
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }
            }
        }
    }
}
