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
    AdapterFailure,
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
    public NearbyRadioTransitionException(Exception innerException)
        : base(
            "Nearby radio transition failed and the effective state is stopped.",
            innerException)
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
        TimeSpan? emergencyDuration = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(capability);
        if (mode == NearbyUserMode.Off)
        {
            return Denied(NearbyPolicyDenialReason.ModeOff);
        }

        if (!capability.CanStart)
        {
            return Denied(NearbyPolicyDenialReason.Unsupported);
        }

        if (!platform.IsForeground)
        {
            return Denied(NearbyPolicyDenialReason.NotForeground);
        }

        if (!platform.HasRequiredPermission)
        {
            return Denied(NearbyPolicyDenialReason.PermissionRequired);
        }

        if (!platform.IsBluetoothEnabled)
        {
            return Denied(NearbyPolicyDenialReason.RadioDisabled);
        }

        if (!platform.IsLocationAvailable)
        {
            return Denied(NearbyPolicyDenialReason.LocationUnavailable);
        }

        if (platform.ThermalState == NearbyThermalState.Unknown)
        {
            return Denied(NearbyPolicyDenialReason.ThermalUnknown);
        }

        if (platform.ThermalState is
            NearbyThermalState.Severe or NearbyThermalState.Critical)
        {
            return Denied(NearbyPolicyDenialReason.ThermalUnsafe);
        }

        if (platform.BatteryPercent is < 0 or > 100 ||
            (isActive &&
             platform.BatteryPercent <= NearbyPolicyConstants.StopBatteryPercent))
        {
            return Denied(NearbyPolicyDenialReason.BatteryCritical);
        }

        if (!isActive &&
            platform.BatteryPercent <= NearbyPolicyConstants.StartBatteryPercent)
        {
            return Denied(NearbyPolicyDenialReason.BatteryAdmissionDenied);
        }

        if (mode == NearbyUserMode.ChargingHub && !platform.IsCharging)
        {
            return Denied(NearbyPolicyDenialReason.ChargingRequired);
        }

        if (mode == NearbyUserMode.ForegroundEmergency &&
            (emergencyDuration is null ||
             !NearbyPolicyConstants.ApprovedEmergencyDurations.Contains(
                 emergencyDuration.Value) ||
             emergencyDuration > NearbyPolicyConstants.MaximumEmergencyDuration))
        {
            return Denied(NearbyPolicyDenialReason.InvalidDuration);
        }

        return new NearbyPolicyResult(
            MayStart: true,
            NearbyPolicyDenialReason.None);
    }

    private static NearbyPolicyResult Denied(
        NearbyPolicyDenialReason reason) =>
        new(MayStart: false, reason);
}

public sealed class NearbyPolicyCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly object eventSync = new();
    private readonly SemaphoreSlim radioGate = new(1, 1);
    private readonly INearbyRadioAdapter radio;
    private readonly INearbyPlatformState platform;
    private readonly INearbyClock clock;
    private readonly INearbyModeIntentStore? intentStore;
    private NearbyCoordinatorSnapshot snapshot;
    private NearbyAttempt? currentAttempt;
    private Task lastPlatformTransition = Task.CompletedTask;
    private int generation;
    private bool disposed;

    public NearbyPolicyCoordinator(
        INearbyRadioAdapter radio,
        INearbyPlatformState platform,
        INearbyClock clock,
        INearbyModeIntentStore? intentStore = null)
    {
        this.radio = radio ?? throw new ArgumentNullException(nameof(radio));
        this.platform = platform ??
            throw new ArgumentNullException(nameof(platform));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.intentStore = intentStore;
        snapshot = StoppedSnapshot(
            NearbyStopReason.User,
            NearbyPolicyEvaluator.Evaluate(
                NearbyUserMode.Off,
                platform.Snapshot,
                radio.Capability,
                isActive: false));
        platform.Changed += OnPlatformChanged;
    }

    public NearbyCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return snapshot;
            }
        }
    }

    public async Task StartAsync(
        NearbyUserMode mode,
        TimeSpan? emergencyDuration = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (mode == NearbyUserMode.Off)
        {
            await StopAsync(
                NearbyStopReason.User,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan? duration = mode == NearbyUserMode.ForegroundEmergency
            ? emergencyDuration ?? NearbyPolicyConstants.DefaultEmergencyDuration
            : null;
        var policy = NearbyPolicyEvaluator.Evaluate(
            mode,
            platform.Snapshot,
            radio.Capability,
            isActive: false,
            duration);
        if (!policy.MayStart)
        {
            SetDenied(policy);
            throw new NearbyPolicyDeniedException(policy.DenialReason);
        }

        if (intentStore is not null)
        {
            await intentStore.SaveAsync(
                new NearbyModeIntent(mode, duration),
                cancellationToken).ConfigureAwait(false);
        }

        NearbyAttempt attempt;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            policy = NearbyPolicyEvaluator.Evaluate(
                mode,
                platform.Snapshot,
                radio.Capability,
                isActive: false,
                duration);
            if (!policy.MayStart)
            {
                snapshot = StoppedSnapshot(null, policy);
                throw new NearbyPolicyDeniedException(policy.DenialReason);
            }

            if (currentAttempt is not null)
            {
                throw new NearbyPolicyDeniedException(
                    NearbyPolicyDenialReason.Unsupported);
            }

            var startedAt = clock.UtcNow;
            TimeSpan? monotonicDeadline = duration is null
                ? null
                : clock.MonotonicNow + duration.Value;
            attempt = new NearbyAttempt(
                ++generation,
                mode,
                duration,
                radio.Capability,
                new NearbyRadioSession(
                    mode,
                    startedAt,
                    monotonicDeadline),
                duration is null ? null : startedAt + duration.Value);
            currentAttempt = attempt;
            snapshot = new NearbyCoordinatorSnapshot(
                mode,
                NearbyEffectiveState.Starting,
                StopReason: null,
                policy,
                Polling(suppress: false),
                attempt.EmergencyEndsAtUtc);
            attempt.StartTask = RunStartAsync(
                attempt,
                cancellationToken);
        }

        await attempt.StartTask.ConfigureAwait(false);
    }

    public async Task StopAsync(
        NearbyStopReason reason = NearbyStopReason.User,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ThrowIfDisposed();
        await StopCurrentAttemptAsync(reason).ConfigureAwait(false);
        if (reason == NearbyStopReason.User && intentStore is not null)
        {
            await intentStore.SaveAsync(
                new NearbyModeIntent(NearbyUserMode.Off, null),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        NearbyAttempt? attempt;
        NearbyPolicyResult policy;
        NearbyStopReason? stopReason = null;
        lock (sync)
        {
            attempt = currentAttempt;
            if (attempt is null)
            {
                return;
            }

            var capability = radio.Capability;
            policy = NearbyPolicyEvaluator.Evaluate(
                attempt.Mode,
                platform.Snapshot,
                capability,
                isActive: snapshot.EffectiveState == NearbyEffectiveState.Active,
                attempt.Duration);
            if (!Equals(capability, attempt.Capability))
            {
                stopReason = NearbyStopReason.CapabilityChanged;
            }
            else if (!policy.MayStart)
            {
                stopReason = StopReasonFor(policy.DenialReason);
            }
            else
            {
                snapshot = snapshot with { Policy = policy };
            }
        }

        if (stopReason is not null)
        {
            await StopAttemptAsync(
                attempt,
                stopReason.Value).ConfigureAwait(false);
        }
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task platformTransition;
            NearbyAttempt? attempt;
            lock (eventSync)
            {
                platformTransition = lastPlatformTransition;
            }

            lock (sync)
            {
                attempt = currentAttempt;
            }

            await platformTransition.ConfigureAwait(false);
            if (attempt?.DeadlineTask is { } deadlineTask &&
                attempt.Session.EmergencyDeadline <= clock.MonotonicNow)
            {
                await deadlineTask.ConfigureAwait(false);
            }

            lock (eventSync)
            {
                if (ReferenceEquals(
                    platformTransition,
                    lastPlatformTransition))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
        }

        platform.Changed -= OnPlatformChanged;
        await StopCurrentAttemptAsync(
            NearbyStopReason.User).ConfigureAwait(false);
        lock (sync)
        {
            disposed = true;
        }

        radioGate.Dispose();
    }

    private async Task RunStartAsync(
        NearbyAttempt attempt,
        CancellationToken callerCancellation)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                attempt.Cancellation.Token,
                callerCancellation);
        try
        {
            await radioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await radio.StartForegroundAsync(
                    attempt.Session,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                radioGate.Release();
            }

            NearbyStopReason? stopReason = null;
            lock (sync)
            {
                if (!ReferenceEquals(currentAttempt, attempt) ||
                    attempt.Generation != generation)
                {
                    return;
                }

                var policy = NearbyPolicyEvaluator.Evaluate(
                    attempt.Mode,
                    platform.Snapshot,
                    radio.Capability,
                    isActive: true,
                    attempt.Duration);
                if (!Equals(radio.Capability, attempt.Capability))
                {
                    stopReason = NearbyStopReason.CapabilityChanged;
                }
                else if (!policy.MayStart)
                {
                    stopReason = StopReasonFor(policy.DenialReason);
                }
                else
                {
                    snapshot = new NearbyCoordinatorSnapshot(
                        attempt.Mode,
                        NearbyEffectiveState.Active,
                        StopReason: null,
                        policy,
                        Polling(suppress: true),
                        attempt.EmergencyEndsAtUtc);
                    if (attempt.Session.EmergencyDeadline is not null)
                    {
                        attempt.DeadlineTask = RunDeadlineAsync(attempt);
                    }
                }
            }

            if (stopReason is not null)
            {
                await StopAttemptAfterCompletedStartAsync(
                    attempt,
                    stopReason.Value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            await FailStartAsync(
                attempt,
                NearbyStopReason.Cancelled).ConfigureAwait(false);
            if (callerCancellation.IsCancellationRequested)
            {
                throw;
            }

            throw new NearbyRadioTransitionException(exception);
        }
        catch (Exception exception)
            when (exception is not NearbyRadioTransitionException)
        {
            await FailStartAsync(
                attempt,
                NearbyStopReason.StartFailed).ConfigureAwait(false);
            throw new NearbyRadioTransitionException(exception);
        }
    }

    private async Task RunDeadlineAsync(NearbyAttempt attempt)
    {
        try
        {
            var deadline = attempt.Session.EmergencyDeadline!.Value;
            var remaining = deadline - clock.MonotonicNow;
            if (remaining > TimeSpan.Zero)
            {
                await clock.DelayAsync(
                    remaining,
                    attempt.Cancellation.Token).ConfigureAwait(false);
            }

            await StopAttemptAsync(
                attempt,
                NearbyStopReason.DeadlineExpired).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (attempt.Cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task StopCurrentAttemptAsync(NearbyStopReason reason)
    {
        NearbyAttempt? attempt;
        lock (sync)
        {
            attempt = currentAttempt;
            if (attempt is null)
            {
                if (snapshot.EffectiveState != NearbyEffectiveState.Stopped ||
                    snapshot.DesiredMode != NearbyUserMode.Off)
                {
                    snapshot = StoppedSnapshot(
                        reason,
                        NearbyPolicyEvaluator.Evaluate(
                            NearbyUserMode.Off,
                            platform.Snapshot,
                            radio.Capability,
                            isActive: false));
                }

                return;
            }
        }

        await StopAttemptAsync(attempt, reason).ConfigureAwait(false);
    }

    private async Task StopAttemptAsync(
        NearbyAttempt attempt,
        NearbyStopReason reason)
    {
        bool ownsStop;
        lock (sync)
        {
            if (!ReferenceEquals(currentAttempt, attempt))
            {
                return;
            }

            currentAttempt = null;
            generation++;
            attempt.Cancellation.Cancel();
            snapshot = snapshot with
            {
                DesiredMode = NearbyUserMode.Off,
                EffectiveState = NearbyEffectiveState.Stopping,
                StopReason = reason,
                Polling = Polling(suppress: false),
                EmergencyEndsAtUtc = null
            };
            ownsStop = attempt.TryClaimStop();
        }

        if (ownsStop)
        {
            try
            {
                await attempt.StartTask.ConfigureAwait(false);
            }
            catch
            {
            }

            await StopRadioAsync(reason).ConfigureAwait(false);
        }

        SetStopped(reason);
    }

    private async Task StopAttemptAfterCompletedStartAsync(
        NearbyAttempt attempt,
        NearbyStopReason reason)
    {
        var ownsStop = false;
        lock (sync)
        {
            if (ReferenceEquals(currentAttempt, attempt))
            {
                currentAttempt = null;
                generation++;
                attempt.Cancellation.Cancel();
                ownsStop = attempt.TryClaimStop();
                snapshot = StoppedSnapshot(
                    reason,
                    NearbyPolicyEvaluator.Evaluate(
                        NearbyUserMode.Off,
                        platform.Snapshot,
                        radio.Capability,
                        isActive: false));
            }
        }

        if (ownsStop)
        {
            await StopRadioAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task FailStartAsync(
        NearbyAttempt attempt,
        NearbyStopReason reason)
    {
        var ownsStop = false;
        lock (sync)
        {
            if (ReferenceEquals(currentAttempt, attempt))
            {
                currentAttempt = null;
                generation++;
                attempt.Cancellation.Cancel();
                ownsStop = attempt.TryClaimStop();
                snapshot = StoppedSnapshot(
                    reason,
                    NearbyPolicyEvaluator.Evaluate(
                        NearbyUserMode.Off,
                        platform.Snapshot,
                        radio.Capability,
                        isActive: false));
            }
        }

        if (ownsStop)
        {
            await StopRadioAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task StopRadioAsync(NearbyStopReason reason)
    {
        try
        {
            await radioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await radio.StopAsync(
                    reason,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                radioGate.Release();
            }
        }
        catch
        {
            SetStopped(NearbyStopReason.AdapterFailure);
        }
    }

    private void SetDenied(NearbyPolicyResult policy)
    {
        lock (sync)
        {
            snapshot = StoppedSnapshot(null, policy);
        }
    }

    private void SetStopped(NearbyStopReason reason)
    {
        lock (sync)
        {
            if (snapshot.EffectiveState == NearbyEffectiveState.Stopping)
            {
                snapshot = StoppedSnapshot(
                    snapshot.StopReason ?? reason,
                    NearbyPolicyEvaluator.Evaluate(
                        NearbyUserMode.Off,
                        platform.Snapshot,
                        radio.Capability,
                        isActive: false));
            }
        }
    }

    private void OnPlatformChanged(
        object? sender,
        NearbyPlatformSnapshot value)
    {
        _ = sender;
        _ = value;
        lock (eventSync)
        {
            lastPlatformTransition = ContinuePlatformTransitionAsync(
                lastPlatformTransition);
        }
    }

    private async Task ContinuePlatformTransitionAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        lock (sync)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static NearbyCoordinatorSnapshot StoppedSnapshot(
        NearbyStopReason? reason,
        NearbyPolicyResult policy) =>
        new(
            NearbyUserMode.Off,
            NearbyEffectiveState.Stopped,
            reason,
            policy,
            Polling(suppress: false),
            EmergencyEndsAtUtc: null);

    private static NearbyPollingPolicyResult Polling(bool suppress) =>
        new(
            suppress,
            suppress
                ? "Effective nearby session is active."
                : "Managed-network polling remains available.");

    private static NearbyStopReason StopReasonFor(
        NearbyPolicyDenialReason reason) =>
        reason switch
        {
            NearbyPolicyDenialReason.NotForeground =>
                NearbyStopReason.Backgrounded,
            NearbyPolicyDenialReason.PermissionRequired =>
                NearbyStopReason.PermissionLost,
            NearbyPolicyDenialReason.RadioDisabled =>
                NearbyStopReason.RadioDisabled,
            NearbyPolicyDenialReason.LocationUnavailable =>
                NearbyStopReason.LocationUnavailable,
            NearbyPolicyDenialReason.ChargingRequired =>
                NearbyStopReason.PowerLost,
            NearbyPolicyDenialReason.BatteryAdmissionDenied or
            NearbyPolicyDenialReason.BatteryCritical =>
                NearbyStopReason.BatteryLow,
            NearbyPolicyDenialReason.ThermalUnknown or
            NearbyPolicyDenialReason.ThermalUnsafe =>
                NearbyStopReason.ThermalUnsafe,
            NearbyPolicyDenialReason.Unsupported =>
                NearbyStopReason.Unsupported,
            _ => NearbyStopReason.CapabilityChanged
        };

    private sealed class NearbyAttempt(
        int generation,
        NearbyUserMode mode,
        TimeSpan? duration,
        NearbyRadioCapability capability,
        NearbyRadioSession session,
        DateTimeOffset? emergencyEndsAtUtc)
    {
        private int stopClaimed;

        public int Generation { get; } = generation;
        public NearbyUserMode Mode { get; } = mode;
        public TimeSpan? Duration { get; } = duration;
        public NearbyRadioCapability Capability { get; } = capability;
        public NearbyRadioSession Session { get; } = session;
        public DateTimeOffset? EmergencyEndsAtUtc { get; } = emergencyEndsAtUtc;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task StartTask { get; set; } = Task.CompletedTask;
        public Task? DeadlineTask { get; set; }

        public bool TryClaimStop() =>
            Interlocked.CompareExchange(ref stopClaimed, 1, 0) == 0;
    }
}

public sealed class DisabledAndroidNearbyRadioAdapter : INearbyRadioAdapter
{
    public NearbyRadioCapability Capability { get; } = new(
        NearbyRadioSupport.DisabledPendingReview,
        "Radio exchange is disabled pending P03D production cryptography and physical-device review.");

    public Task StartForegroundAsync(
        NearbyRadioSession request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NearbyRadioUnavailableException());
    }

    public Task StopAsync(
        NearbyStopReason reason,
        CancellationToken cancellationToken)
    {
        _ = reason;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class UnsupportedWindowsNearbyRadioAdapter : INearbyRadioAdapter
{
    public NearbyRadioCapability Capability { get; } = new(
        NearbyRadioSupport.Unsupported,
        "Nearby radio sessions are unsupported on Windows.");

    public Task StartForegroundAsync(
        NearbyRadioSession request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NearbyRadioUnavailableException());
    }

    public Task StopAsync(
        NearbyStopReason reason,
        CancellationToken cancellationToken)
    {
        _ = reason;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
