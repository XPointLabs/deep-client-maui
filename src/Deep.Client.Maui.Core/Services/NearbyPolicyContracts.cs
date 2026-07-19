namespace Deep.Client.Maui.Core.Services;

public enum NearbyUserMode
{
    Off,
    ForegroundEmergency,
    ChargingHub
}

public enum NearbyEffectiveState
{
    Stopped,
    Starting,
    Active,
    Stopping
}

public enum NearbyRadioSupport
{
    Supported,
    Unsupported,
    DisabledPendingReview
}

public enum NearbyThermalState
{
    Unknown,
    Nominal,
    Moderate,
    Severe,
    Critical
}

public enum NearbyPolicyDenialReason
{
    None,
    ModeOff,
    Unsupported,
    NotForeground,
    PermissionRequired,
    RadioDisabled,
    LocationUnavailable,
    ChargingRequired,
    BatteryAdmissionDenied,
    BatteryCritical,
    ThermalUnknown,
    ThermalUnsafe,
    InvalidDuration
}

public enum NearbyStopReason
{
    User,
    Backgrounded,
    DeadlineExpired,
    PermissionLost,
    RadioDisabled,
    LocationUnavailable,
    PowerLost,
    BatteryLow,
    ThermalUnsafe,
    Unsupported,
    CapabilityChanged,
    StartFailed,
    Cancelled
}

public sealed record NearbyRadioCapability(
    NearbyRadioSupport Support,
    string Limitation)
{
    public bool CanStart => Support == NearbyRadioSupport.Supported;
}

public sealed record NearbyPlatformSnapshot(
    bool IsForeground,
    bool HasRequiredPermission,
    bool IsBluetoothEnabled,
    bool IsLocationAvailable,
    bool IsCharging,
    int BatteryPercent,
    NearbyThermalState ThermalState);

public sealed record NearbyRadioSession(
    NearbyUserMode Mode,
    DateTimeOffset StartedAtUtc,
    TimeSpan? EmergencyDeadline);

public sealed record NearbyPolicyResult(
    bool MayStart,
    NearbyPolicyDenialReason DenialReason);

public sealed record NearbyPollingPolicyResult(
    bool SuppressManagedNetworkPolling,
    string Reason);

public sealed record NearbyModeIntent(
    NearbyUserMode Mode,
    TimeSpan? EmergencyDuration);

public sealed record NearbyCoordinatorSnapshot(
    NearbyUserMode DesiredMode,
    NearbyEffectiveState EffectiveState,
    NearbyStopReason? StopReason,
    NearbyPolicyResult Policy,
    NearbyPollingPolicyResult Polling,
    DateTimeOffset? EmergencyEndsAtUtc);

public sealed class NearbyPolicyDeniedException : InvalidOperationException
{
    public NearbyPolicyDeniedException(NearbyPolicyDenialReason reason)
        : base("Nearby mode is unavailable under the current device policy.")
    {
        Reason = reason;
    }

    public NearbyPolicyDenialReason Reason { get; }
}

public sealed class NearbyRadioTransitionException : InvalidOperationException
{
    public NearbyRadioTransitionException()
        : base("Nearby radio transition failed and the radio remains stopped.")
    {
    }
}

public sealed class NearbyRadioUnavailableException : InvalidOperationException
{
    public NearbyRadioUnavailableException()
        : base("Nearby radio is unavailable in this application build.")
    {
    }
}

public interface INearbyRadioAdapter
{
    NearbyRadioCapability Capability { get; }

    Task StartForegroundAsync(
        NearbyRadioSession request,
        CancellationToken cancellationToken);

    Task StopAsync(
        NearbyStopReason reason,
        CancellationToken cancellationToken);
}

public interface INearbyPlatformState
{
    NearbyPlatformSnapshot Snapshot { get; }

    event EventHandler<NearbyPlatformSnapshot>? Changed;
}

public interface INearbyClock
{
    DateTimeOffset UtcNow { get; }

    TimeSpan MonotonicNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface INearbyModeIntentStore
{
    Task SaveAsync(
        NearbyModeIntent intent,
        CancellationToken cancellationToken);
}

public static class NearbyPolicyConstants
{
    public const int StopBatteryPercent = 15;
    public const int StartBatteryPercent = 20;

    public static TimeSpan DefaultEmergencyDuration =>
        TimeSpan.FromMinutes(15);

    public static TimeSpan MaximumEmergencyDuration =>
        TimeSpan.FromMinutes(60);

    public static IReadOnlyList<TimeSpan> ApprovedEmergencyDurations { get; } =
        [
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(60)
        ];
}

public static class NearbyPolicyEvaluator
{
    public static NearbyPolicyResult Evaluate(
        NearbyUserMode mode,
        NearbyPlatformSnapshot platform,
        NearbyRadioCapability capability,
        bool isActive,
        TimeSpan? emergencyDuration = null) =>
        throw new NotImplementedException();
}

public sealed class NearbyPolicyCoordinator : IAsyncDisposable
{
    public NearbyPolicyCoordinator(
        INearbyRadioAdapter radio,
        INearbyPlatformState platform,
        INearbyClock clock,
        INearbyModeIntentStore? intentStore = null)
    {
        throw new NotImplementedException();
    }

    public NearbyCoordinatorSnapshot Snapshot { get; } = null!;

    public Task StartAsync(
        NearbyUserMode mode,
        TimeSpan? emergencyDuration = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task StopAsync(
        NearbyStopReason reason = NearbyStopReason.User,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task RefreshAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task DrainAsync() => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

public sealed class DisabledAndroidNearbyRadioAdapter : INearbyRadioAdapter
{
    public NearbyRadioCapability Capability => throw new NotImplementedException();

    public Task StartForegroundAsync(
        NearbyRadioSession request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task StopAsync(
        NearbyStopReason reason,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class UnsupportedWindowsNearbyRadioAdapter : INearbyRadioAdapter
{
    public NearbyRadioCapability Capability => throw new NotImplementedException();

    public Task StartForegroundAsync(
        NearbyRadioSession request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task StopAsync(
        NearbyStopReason reason,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
