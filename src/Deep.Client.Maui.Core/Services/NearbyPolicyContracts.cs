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
    Stopping,
    StopFailed
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
    DeadlineFailure,
    StopFailed,
    Cancelled
}

public enum NearbyRadioTransitionError
{
    AdapterStartFailed,
    IntentCommitFailed
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
    public NearbyRadioTransitionException(NearbyRadioTransitionError error)
        : base(error switch
        {
            NearbyRadioTransitionError.AdapterStartFailed =>
                "Nearby radio start failed; effective state is not active.",
            NearbyRadioTransitionError.IntentCommitFailed =>
                "Nearby mode intent could not be committed; effective state is not active.",
            _ => "Nearby transition failed; effective state is not active."
        })
    {
        Error = error;
    }

    public NearbyRadioTransitionError Error { get; }

    public override string ToString() =>
        $"{GetType().FullName}: {Message} ({Error})";
}

public sealed class NearbyCoordinatorBusyException : InvalidOperationException
{
    public NearbyCoordinatorBusyException()
        : base("A nearby radio transition is already in progress.")
    {
    }
}

public sealed class NearbyCoordinatorReentrancyException : InvalidOperationException
{
    public NearbyCoordinatorReentrancyException()
        : base("Nearby radio adapters cannot call the coordinator reentrantly.")
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
        if (!Enum.IsDefined(mode))
        {
            return Denied(NearbyPolicyDenialReason.Unsupported);
        }

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

        if (!Enum.IsDefined(platform.ThermalState) ||
            platform.ThermalState == NearbyThermalState.Unknown)
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
    private readonly AsyncLocal<int> adapterCallbackDepth = new();
    private readonly INearbyRadioAdapter radio;
    private readonly INearbyPlatformState platform;
    private readonly INearbyClock clock;
    private readonly INearbyModeIntentStore? intentStore;
    private NearbyCoordinatorSnapshot snapshot;
    private NearbyAttempt? currentAttempt;
    private Task? stopTask;
    private Task? disposeTask;
    private Task lastPlatformTransition = Task.CompletedTask;
    private int generation;
    private NearbyCoordinatorLifecycle lifecycle =
        NearbyCoordinatorLifecycle.Running;

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
        ThrowIfAdapterReentrant();
        cancellationToken.ThrowIfCancellationRequested();
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
        NearbyAttempt attempt;
        lock (sync)
        {
            ThrowIfNotRunningLocked();
            if (currentAttempt is not null ||
                stopTask is not null ||
                snapshot.EffectiveState == NearbyEffectiveState.StopFailed)
            {
                throw new NearbyCoordinatorBusyException();
            }

            var policy = NearbyPolicyEvaluator.Evaluate(
                mode,
                platform.Snapshot,
                radio.Capability,
                isActive: false,
                duration);
            if (!policy.MayStart)
            {
                throw new NearbyPolicyDeniedException(policy.DenialReason);
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
        ThrowIfAdapterReentrant();
        _ = cancellationToken;
        lock (sync)
        {
            ThrowIfNotRunningLocked();
        }

        await StopCurrentAttemptAsync(reason).ConfigureAwait(false);
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfAdapterReentrant();
        cancellationToken.ThrowIfCancellationRequested();
        NearbyAttempt? attempt;
        NearbyPolicyResult policy;
        NearbyStopReason? stopReason = null;
        lock (sync)
        {
            ThrowIfNotRunningLocked();
            attempt = currentAttempt;
            if (attempt is null)
            {
                return;
            }

            if (attempt.Session.EmergencyDeadline is { } deadline &&
                clock.MonotonicNow >= deadline)
            {
                policy = snapshot.Policy;
                stopReason = NearbyStopReason.DeadlineExpired;
            }
            else
            {
                var capability = radio.Capability;
                policy = NearbyPolicyEvaluator.Evaluate(
                    attempt.Mode,
                    platform.Snapshot,
                    capability,
                    isActive: snapshot.EffectiveState ==
                        NearbyEffectiveState.Active,
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
            Task? pendingStop;
            lock (eventSync)
            {
                platformTransition = lastPlatformTransition;
            }

            lock (sync)
            {
                attempt = currentAttempt;
                pendingStop = stopTask;
            }

            await platformTransition.ConfigureAwait(false);
            if (attempt?.DeadlineTask is { } deadlineTask &&
                (deadlineTask.IsCompleted ||
                 attempt.Session.EmergencyDeadline <= clock.MonotonicNow))
            {
                await deadlineTask.ConfigureAwait(false);
            }

            if (pendingStop is not null)
            {
                await pendingStop.ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        ThrowIfAdapterReentrant();
        Task task;
        TaskCompletionSource? owner = null;
        lock (sync)
        {
            if (lifecycle == NearbyCoordinatorLifecycle.Disposed)
            {
                return ValueTask.CompletedTask;
            }

            if (lifecycle == NearbyCoordinatorLifecycle.Disposing)
            {
                return new ValueTask(disposeTask!);
            }

            lifecycle = NearbyCoordinatorLifecycle.Disposing;
            owner = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = owner.Task;
            task = disposeTask;
        }

        platform.Changed -= OnPlatformChanged;
        _ = DisposeCoreAsync(owner);
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await DrainAsync().ConfigureAwait(false);
            await StopCurrentAttemptAsync(
                NearbyStopReason.User).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            radioGate.Dispose();
            lock (sync)
            {
                lifecycle = NearbyCoordinatorLifecycle.Disposed;
            }

            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                lifecycle = NearbyCoordinatorLifecycle.Disposed;
            }

            completion.TrySetException(exception);
        }
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
            await InvokeAdapterStartAsync(
                attempt.Session,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (NearbyCoordinatorReentrancyException)
        {
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.StartFailed,
                awaitStart: false,
                persistOff: false).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAttempt(attempt))
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.Cancelled,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
            }

            if (callerCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerCancellation);
            }

            return;
        }
        catch
        {
            if (IsCurrentAttempt(attempt))
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.StartFailed,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
            }

            throw new NearbyRadioTransitionException(
                NearbyRadioTransitionError.AdapterStartFailed);
        }

        if (linkedCancellation.IsCancellationRequested)
        {
            if (IsCurrentAttempt(attempt))
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.Cancelled,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
            }

            if (callerCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerCancellation);
            }

            return;
        }

        if (!IsCurrentAttempt(attempt))
        {
            return;
        }

        Task? deadlineDelay = null;
        if (attempt.Session.EmergencyDeadline is { } deadline)
        {
            var remaining = deadline - clock.MonotonicNow;
            if (remaining <= TimeSpan.Zero)
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.DeadlineExpired,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
                return;
            }

            try
            {
                deadlineDelay = clock.DelayAsync(
                    remaining,
                    attempt.Cancellation.Token);
                if (deadlineDelay.IsCompleted)
                {
                    await deadlineDelay.ConfigureAwait(false);
                    await StopAttemptAsync(
                        attempt,
                        NearbyStopReason.DeadlineExpired,
                        awaitStart: false,
                        persistOff: false).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
                when (attempt.Cancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.DeadlineFailure,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
                return;
            }
        }

        var intentCommitted = false;
        if (intentStore is not null)
        {
            try
            {
                await intentStore.SaveAsync(
                    new NearbyModeIntent(attempt.Mode, attempt.Duration),
                    linkedCancellation.Token).ConfigureAwait(false);
                intentCommitted = true;
            }
            catch (OperationCanceledException)
                when (linkedCancellation.IsCancellationRequested)
            {
                if (IsCurrentAttempt(attempt))
                {
                    await StopAttemptAsync(
                        attempt,
                        NearbyStopReason.Cancelled,
                        awaitStart: false).ConfigureAwait(false);
                }

                if (callerCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(callerCancellation);
                }

                return;
            }
            catch
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.StartFailed,
                    awaitStart: false).ConfigureAwait(false);
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.IntentCommitFailed);
            }
        }

        if (linkedCancellation.IsCancellationRequested ||
            !IsCurrentAttempt(attempt))
        {
            if (intentCommitted)
            {
                await PersistOffAsync().ConfigureAwait(false);
            }

            if (IsCurrentAttempt(attempt))
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.Cancelled,
                    awaitStart: false).ConfigureAwait(false);
            }

            if (callerCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerCancellation);
            }

            return;
        }

        NearbyStopReason? stopReason = null;
        lock (sync)
        {
            if (!IsCurrentAttemptLocked(attempt) ||
                linkedCancellation.IsCancellationRequested)
            {
                stopReason = NearbyStopReason.Cancelled;
            }
            else if (attempt.Session.EmergencyDeadline is { } currentDeadline &&
                     clock.MonotonicNow >= currentDeadline)
            {
                stopReason = NearbyStopReason.DeadlineExpired;
            }
            else
            {
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
                    if (deadlineDelay is not null)
                    {
                        attempt.DeadlineTask = RunDeadlineAsync(
                            attempt,
                            deadlineDelay);
                    }
                }
            }
        }

        if (stopReason is not null)
        {
            await StopAttemptAsync(
                attempt,
                stopReason.Value,
                awaitStart: false).ConfigureAwait(false);
        }
    }

    private async Task RunDeadlineAsync(
        NearbyAttempt attempt,
        Task deadlineDelay)
    {
        try
        {
            await deadlineDelay.ConfigureAwait(false);
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.DeadlineExpired,
                awaitStart: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (attempt.Cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.DeadlineFailure,
                awaitStart: false).ConfigureAwait(false);
        }
    }

    private Task StopCurrentAttemptAsync(NearbyStopReason reason)
    {
        NearbyAttempt? attempt;
        lock (sync)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            attempt = currentAttempt;
            if (attempt is null &&
                snapshot.EffectiveState != NearbyEffectiveState.StopFailed)
            {
                return PersistOffAsync();
            }
        }

        return attempt is null
            ? BeginUnknownPhysicalStop(reason)
            : StopAttemptAsync(attempt, reason);
    }

    private Task StopAttemptAsync(
        NearbyAttempt attempt,
        NearbyStopReason reason,
        bool awaitStart = true,
        bool persistOff = true)
    {
        TaskCompletionSource? completion;
        Task task;
        lock (sync)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            if (!ReferenceEquals(currentAttempt, attempt))
            {
                return Task.CompletedTask;
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
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            task = stopTask;
        }

        _ = CompleteStopAsync(
            attempt,
            reason,
            awaitStart,
            persistOff,
            completion);
        return task;
    }

    private Task BeginUnknownPhysicalStop(NearbyStopReason reason)
    {
        TaskCompletionSource completion;
        Task task;
        lock (sync)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            snapshot = snapshot with
            {
                EffectiveState = NearbyEffectiveState.Stopping,
                StopReason = reason
            };
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            task = stopTask;
        }

        _ = CompleteStopAsync(
            attempt: null,
            reason,
            awaitStart: false,
            persistOff: true,
            completion);
        return task;
    }

    private async Task CompleteStopAsync(
        NearbyAttempt? attempt,
        NearbyStopReason reason,
        bool awaitStart,
        bool persistOff,
        TaskCompletionSource completion)
    {
        if (awaitStart && attempt is not null)
        {
            try
            {
                await attempt.StartTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var physicalStopped = await StopRadioAsync(reason).ConfigureAwait(false);
        if (persistOff)
        {
            await PersistOffAsync().ConfigureAwait(false);
        }
        lock (sync)
        {
            snapshot = physicalStopped
                ? StoppedSnapshot(
                    reason,
                    NearbyPolicyEvaluator.Evaluate(
                        NearbyUserMode.Off,
                        platform.Snapshot,
                        radio.Capability,
                        isActive: false))
                : new NearbyCoordinatorSnapshot(
                    NearbyUserMode.Off,
                    NearbyEffectiveState.StopFailed,
                    NearbyStopReason.StopFailed,
                    NearbyPolicyEvaluator.Evaluate(
                        NearbyUserMode.Off,
                        platform.Snapshot,
                        radio.Capability,
                        isActive: false),
                    Polling(suppress: false),
                    EmergencyEndsAtUtc: null);
            stopTask = null;
        }

        attempt?.Cancellation.Dispose();
        completion.TrySetResult();
    }

    private async Task<bool> StopRadioAsync(NearbyStopReason reason)
    {
        try
        {
            await radioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                adapterCallbackDepth.Value++;
                await radio.StopAsync(
                    reason,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                adapterCallbackDepth.Value--;
                radioGate.Release();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InvokeAdapterStartAsync(
        NearbyRadioSession session,
        CancellationToken cancellationToken)
    {
        await radioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            adapterCallbackDepth.Value++;
            await radio.StartForegroundAsync(
                session,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            adapterCallbackDepth.Value--;
            radioGate.Release();
        }
    }

    private async Task PersistOffAsync()
    {
        if (intentStore is null)
        {
            return;
        }

        try
        {
            await intentStore.SaveAsync(
                new NearbyModeIntent(NearbyUserMode.Off, null),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
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

    private void ThrowIfAdapterReentrant()
    {
        if (adapterCallbackDepth.Value > 0)
        {
            throw new NearbyCoordinatorReentrancyException();
        }
    }

    private void ThrowIfNotRunningLocked()
    {
        ObjectDisposedException.ThrowIf(
            lifecycle != NearbyCoordinatorLifecycle.Running,
            this);
    }

    private bool IsCurrentAttempt(NearbyAttempt attempt)
    {
        lock (sync)
        {
            return IsCurrentAttemptLocked(attempt);
        }
    }

    private bool IsCurrentAttemptLocked(NearbyAttempt attempt) =>
        ReferenceEquals(currentAttempt, attempt) &&
        attempt.Generation == generation;

    private enum NearbyCoordinatorLifecycle
    {
        Running,
        Disposing,
        Disposed
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
        public int Generation { get; } = generation;
        public NearbyUserMode Mode { get; } = mode;
        public TimeSpan? Duration { get; } = duration;
        public NearbyRadioCapability Capability { get; } = capability;
        public NearbyRadioSession Session { get; } = session;
        public DateTimeOffset? EmergencyEndsAtUtc { get; } = emergencyEndsAtUtc;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task StartTask { get; set; } = Task.CompletedTask;
        public Task? DeadlineTask { get; set; }
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
