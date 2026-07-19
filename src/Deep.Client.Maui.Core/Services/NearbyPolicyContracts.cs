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
    IntentCommitFailed,
    StateReadFailed,
    PhysicalStopFailed
}

public enum NearbyIntentPersistenceState
{
    Consistent,
    Pending,
    Failed
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
    DateTimeOffset? EmergencyEndsAtUtc,
    NearbyIntentPersistenceState IntentPersistenceState =
        NearbyIntentPersistenceState.Consistent);

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
            NearbyRadioTransitionError.StateReadFailed =>
                "Nearby device state could not be verified; effective state is not active.",
            NearbyRadioTransitionError.PhysicalStopFailed =>
                "Nearby radio stop could not be verified; physical state is unknown.",
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
        : base("Nearby external callbacks cannot call the coordinator reentrantly.")
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

    // Failure is atomic: false or an exception must retain no handler.
    // A successful lease must support idempotent disposal and retry on failure.
    bool TrySubscribe(
        EventHandler<NearbyPlatformSnapshot> handler,
        out INearbyPlatformSubscription? subscription);
}

public interface INearbyPlatformSubscription : IDisposable
{
}

public sealed class NearbyPlatformSubscription(
    Action unsubscribe) : INearbyPlatformSubscription
{
    private readonly object sync = new();
    private Action? unsubscribe = unsubscribe ??
        throw new ArgumentNullException(nameof(unsubscribe));

    public void Dispose()
    {
        lock (sync)
        {
            if (unsubscribe is null)
            {
                return;
            }

            unsubscribe();
            unsubscribe = null;
        }
    }
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
    private readonly object intentSync = new();
    private readonly SemaphoreSlim radioGate = new(1, 1);
    private readonly AsyncLocal<int> externalCallbackDepth = new();
    private readonly INearbyRadioAdapter radio;
    private readonly INearbyPlatformState platform;
    private readonly INearbyClock clock;
    private readonly INearbyModeIntentStore? intentStore;
    private INearbyPlatformSubscription? platformSubscription;
    private NearbyCoordinatorSnapshot snapshot;
    private NearbyAttempt? currentAttempt;
    private Task? stopTask;
    private Task? disposeTask;
    private Task lastPlatformTransition = Task.CompletedTask;
    private Task lastIntentTransition = Task.CompletedTask;
    private int pendingIntentOperations;
    private int intentGeneration;
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
            OffPolicy());
        try
        {
            if (!platform.TrySubscribe(
                    OnPlatformChanged,
                    out platformSubscription) ||
                platformSubscription is null)
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.StateReadFailed);
            }
        }
        catch (NearbyRadioTransitionException)
        {
            throw;
        }
        catch
        {
            throw new NearbyRadioTransitionException(
                NearbyRadioTransitionError.StateReadFailed);
        }
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
                Volatile.Read(ref pendingIntentOperations) > 0 ||
                snapshot.EffectiveState == NearbyEffectiveState.StopFailed)
            {
                throw new NearbyCoordinatorBusyException();
            }

            NearbyPlatformSnapshot platformSnapshot;
            NearbyRadioCapability capability;
            try
            {
                platformSnapshot = platform.Snapshot;
                capability = radio.Capability;
            }
            catch
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.StateReadFailed);
            }

            var policy = NearbyPolicyEvaluator.Evaluate(
                mode,
                platformSnapshot,
                capability,
                isActive: false,
                duration);
            if (!policy.MayStart)
            {
                throw new NearbyPolicyDeniedException(policy.DenialReason);
            }

            DateTimeOffset startedAt;
            TimeSpan? monotonicDeadline;
            try
            {
                startedAt = clock.UtcNow;
                monotonicDeadline = duration is null
                    ? null
                    : clock.MonotonicNow + duration.Value;
            }
            catch
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.StateReadFailed);
            }

            attempt = new NearbyAttempt(
                ++generation,
                mode,
                duration,
                capability,
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
        lock (sync)
        {
            ThrowIfNotRunningLocked();
            attempt = currentAttempt;
            if (attempt is null)
            {
                return;
            }

        }

        NearbyPolicyResult policy;
        NearbyRadioCapability capability;
        TimeSpan monotonicNow;
        try
        {
            monotonicNow = clock.MonotonicNow;
            capability = radio.Capability;
            policy = NearbyPolicyEvaluator.Evaluate(
                attempt.Mode,
                platform.Snapshot,
                capability,
                isActive: Snapshot.EffectiveState ==
                    NearbyEffectiveState.Active,
                attempt.Duration);
        }
        catch
        {
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.AdapterFailure).ConfigureAwait(false);
            throw new NearbyRadioTransitionException(
                NearbyRadioTransitionError.StateReadFailed);
        }

        NearbyStopReason? stopReason = null;
        lock (sync)
        {
            if (!IsCurrentAttemptLocked(attempt))
            {
                return;
            }

            if (attempt.Session.EmergencyDeadline is { } deadline &&
                monotonicNow >= deadline)
            {
                stopReason = NearbyStopReason.DeadlineExpired;
            }
            else if (!Equals(capability, attempt.Capability))
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
        ThrowIfAdapterReentrant();
        lock (sync)
        {
            ThrowIfNotRunningLocked();
        }

        await DrainCoreAsync().ConfigureAwait(false);
    }

    private async Task DrainCoreAsync()
    {
        while (true)
        {
            Task platformTransition;
            Task intentTransition;
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

            lock (intentSync)
            {
                intentTransition = lastIntentTransition;
            }

            await platformTransition.ConfigureAwait(false);
            if (attempt?.DeadlineTask is { } deadlineTask)
            {
                var deadlineReached = deadlineTask.IsCompleted;
                if (!deadlineReached &&
                    attempt.Session.EmergencyDeadline is not null)
                {
                    try
                    {
                        deadlineReached =
                            attempt.Session.EmergencyDeadline <=
                            clock.MonotonicNow;
                    }
                    catch
                    {
                        await StopAttemptAsync(
                            attempt,
                            NearbyStopReason.AdapterFailure)
                            .ConfigureAwait(false);
                        throw new NearbyRadioTransitionException(
                            NearbyRadioTransitionError.StateReadFailed);
                    }
                }

                if (deadlineReached)
                {
                    await deadlineTask.ConfigureAwait(false);
                }
            }

            if (pendingStop is not null)
            {
                await pendingStop.ConfigureAwait(false);
            }

            await intentTransition.ConfigureAwait(false);

            lock (eventSync)
            {
                if (ReferenceEquals(
                    platformTransition,
                    lastPlatformTransition))
                {
                    lock (intentSync)
                    {
                        if (ReferenceEquals(
                            intentTransition,
                            lastIntentTransition))
                        {
                            return;
                        }
                    }
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

        _ = DisposeCoreAsync(owner);
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopCurrentAttemptAsync(
                NearbyStopReason.User).ConfigureAwait(false);
            await DrainCoreAsync().ConfigureAwait(false);
            NearbyCoordinatorSnapshot finalSnapshot;
            lock (sync)
            {
                finalSnapshot = snapshot;
            }

            if (finalSnapshot.EffectiveState ==
                NearbyEffectiveState.StopFailed)
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.PhysicalStopFailed);
            }

            if (finalSnapshot.IntentPersistenceState ==
                NearbyIntentPersistenceState.Failed)
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.IntentCommitFailed);
            }

            try
            {
                externalCallbackDepth.Value++;
                try
                {
                    platformSubscription?.Dispose();
                    platformSubscription = null;
                }
                finally
                {
                    externalCallbackDepth.Value--;
                }
            }
            catch
            {
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.StateReadFailed);
            }

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
                lifecycle = NearbyCoordinatorLifecycle.Running;
                disposeTask = null;
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
                attempt.Cancellation.Token);
        try
        {
            var callerCancellationRegistration = callerCancellation.Register(
                () => ObserveCallerCancellation(attempt));
            attempt.AttachCallerCancellation(callerCancellationRegistration);
        }
        catch
        {
            attempt.PhysicalStartCompletion.TrySetResult();
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.Cancelled,
                awaitStart: false,
                persistOff: false).ConfigureAwait(false);
            throw new NearbyRadioTransitionException(
                NearbyRadioTransitionError.StateReadFailed);
        }

        if (callerCancellation.IsCancellationRequested)
        {
            attempt.PhysicalStartCompletion.TrySetResult();
            await AwaitCallerCancellationCleanupAsync(attempt)
                .ConfigureAwait(false);
            throw new OperationCanceledException(callerCancellation);
        }

        try
        {
            await InvokeAdapterStartAndSignalAsync(
                attempt,
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
                await AwaitCallerCancellationCleanupAsync(attempt)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(callerCancellation);
            }

            return;
        }
        catch
        {
            if (callerCancellation.IsCancellationRequested)
            {
                await AwaitCallerCancellationCleanupAsync(attempt)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(callerCancellation);
            }

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
                await AwaitCallerCancellationCleanupAsync(attempt)
                    .ConfigureAwait(false);
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
            TimeSpan remaining;
            try
            {
                remaining = deadline - clock.MonotonicNow;
            }
            catch
            {
                await StopAttemptAsync(
                    attempt,
                    NearbyStopReason.AdapterFailure,
                    awaitStart: false,
                    persistOff: false).ConfigureAwait(false);
                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.StateReadFailed);
            }

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

                attempt.DeadlineTask = RunDeadlineAsync(
                    attempt,
                    deadlineDelay);
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

        if (intentStore is not null)
        {
            Task? activeIntentSave = null;
            lock (sync)
            {
                if (IsCurrentAttemptLocked(attempt) &&
                    !linkedCancellation.IsCancellationRequested)
                {
                    activeIntentSave = EnqueueIntentSave(
                        new NearbyModeIntent(
                            attempt.Mode,
                            attempt.Duration),
                        linkedCancellation.Token);
                }
            }

            if (activeIntentSave is null)
            {
                if (callerCancellation.IsCancellationRequested)
                {
                    await AwaitCallerCancellationCleanupAsync(attempt)
                        .ConfigureAwait(false);
                    throw new OperationCanceledException(callerCancellation);
                }

                return;
            }

            try
            {
                await activeIntentSave.ConfigureAwait(false);
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
                    await AwaitCallerCancellationCleanupAsync(attempt)
                        .ConfigureAwait(false);
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
                if (callerCancellation.IsCancellationRequested)
                {
                    await AwaitCallerCancellationCleanupAsync(
                        attempt,
                        physicalStopAlreadyJoined: true)
                        .ConfigureAwait(false);
                    throw new OperationCanceledException(callerCancellation);
                }

                throw new NearbyRadioTransitionException(
                    NearbyRadioTransitionError.IntentCommitFailed);
            }
        }

        if (linkedCancellation.IsCancellationRequested ||
            !IsCurrentAttempt(attempt))
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
                await AwaitCallerCancellationCleanupAsync(
                    attempt,
                    physicalStopAlreadyJoined: true)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(callerCancellation);
            }

            return;
        }

        NearbyStopReason? stopReason = null;
        NearbyPolicyResult? postStartPolicy = null;
        NearbyRadioCapability? postStartCapability = null;
        TimeSpan postStartMonotonic = default;
        try
        {
            postStartMonotonic = clock.MonotonicNow;
            postStartCapability = radio.Capability;
            postStartPolicy = NearbyPolicyEvaluator.Evaluate(
                attempt.Mode,
                platform.Snapshot,
                postStartCapability,
                isActive: true,
                attempt.Duration);
        }
        catch
        {
            await StopAttemptAsync(
                attempt,
                NearbyStopReason.AdapterFailure,
                awaitStart: false).ConfigureAwait(false);
            throw new NearbyRadioTransitionException(
                NearbyRadioTransitionError.StateReadFailed);
        }

        lock (sync)
        {
            if (!IsCurrentAttemptLocked(attempt) ||
                linkedCancellation.IsCancellationRequested)
            {
                stopReason = NearbyStopReason.Cancelled;
            }
            else if (attempt.Session.EmergencyDeadline is { } currentDeadline &&
                     postStartMonotonic >= currentDeadline)
            {
                stopReason = NearbyStopReason.DeadlineExpired;
            }
            else
            {
                if (!Equals(postStartCapability, attempt.Capability))
                {
                    stopReason = NearbyStopReason.CapabilityChanged;
                }
                else if (!postStartPolicy.MayStart)
                {
                    stopReason = StopReasonFor(
                        postStartPolicy.DenialReason);
                }
                else
                {
                    snapshot = new NearbyCoordinatorSnapshot(
                        attempt.Mode,
                        NearbyEffectiveState.Active,
                        StopReason: null,
                        postStartPolicy,
                        Polling(suppress: true),
                        attempt.EmergencyEndsAtUtc);
                }
            }
        }

        if (stopReason is not null)
        {
            await StopAttemptAsync(
                attempt,
                stopReason.Value,
                awaitStart: false).ConfigureAwait(false);
            if (callerCancellation.IsCancellationRequested)
            {
                await AwaitCallerCancellationCleanupAsync(
                    attempt,
                    physicalStopAlreadyJoined: true)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(callerCancellation);
            }
        }
        else
        {
            if (callerCancellation.IsCancellationRequested)
            {
                await AwaitCallerCancellationCleanupAsync(attempt)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(callerCancellation);
            }

            attempt.DisposeCallerCancellation();
        }
    }

    private void ObserveCallerCancellation(NearbyAttempt attempt)
    {
        var cleanup = StopAttemptAsync(
            attempt,
            NearbyStopReason.Cancelled,
            awaitStart: true);
        attempt.SetCallerCancellationCleanup(cleanup);
    }

    private async Task AwaitCallerCancellationCleanupAsync(
        NearbyAttempt attempt,
        bool physicalStopAlreadyJoined = false)
    {
        if (!physicalStopAlreadyJoined ||
            attempt.CallerCancellationObserved.Task.IsCompleted)
        {
            await attempt.CallerCancellationObserved.Task.ConfigureAwait(false);
            await attempt.CallerCancellationCleanup.ConfigureAwait(false);
        }

        Task reconciliation;
        lock (intentSync)
        {
            reconciliation = lastIntentTransition;
        }

        await reconciliation.ConfigureAwait(false);
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
                return BeginOffPersistenceTransition(reason);
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

        Task? cancellationCallbacks = null;
        try
        {
            cancellationCallbacks = attempt.Cancellation.CancelAsync();
        }
        catch
        {
        }

        _ = CompleteStopAsync(
            attempt,
            reason,
            awaitStart,
            persistOff,
            completion);
        if (cancellationCallbacks is not null)
        {
            _ = ObserveCancellationCallbacksAsync(cancellationCallbacks);
        }

        return task;
    }

    private static async Task ObserveCancellationCallbacksAsync(
        Task cancellationCallbacks)
    {
        try
        {
            await cancellationCallbacks.ConfigureAwait(false);
        }
        catch
        {
        }
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

    private Task BeginOffPersistenceTransition(NearbyStopReason reason)
    {
        if (intentStore is null)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource completion;
        lock (sync)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            snapshot = snapshot with
            {
                DesiredMode = NearbyUserMode.Off,
                StopReason = reason,
                Polling = Polling(suppress: false),
                EmergencyEndsAtUtc = null
            };
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
        }

        _ = CompleteOffPersistenceTransitionAsync(completion);
        return completion.Task;
    }

    private async Task CompleteOffPersistenceTransitionAsync(
        TaskCompletionSource completion)
    {
        NearbyRadioTransitionException? failure = null;
        try
        {
            await EnqueueIntentSave(
                new NearbyModeIntent(NearbyUserMode.Off, null),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            failure = new NearbyRadioTransitionException(
                NearbyRadioTransitionError.IntentCommitFailed);
        }

        Task reconciliation;
        lock (intentSync)
        {
            reconciliation = lastIntentTransition;
        }

        await reconciliation.ConfigureAwait(false);
        lock (sync)
        {
            if (ReferenceEquals(stopTask, completion.Task))
            {
                stopTask = null;
            }
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }
    }

    private async Task CompleteStopAsync(
        NearbyAttempt? attempt,
        NearbyStopReason reason,
        bool awaitStart,
        bool persistOff,
        TaskCompletionSource completion)
    {
        var physicalStopped = false;
        try
        {
            if (awaitStart && attempt is not null)
            {
                try
                {
                    await attempt.PhysicalStartCompletion.Task
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            physicalStopped = await StopRadioAsync(reason).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            if (persistOff)
            {
                _ = EnqueueIntentSave(
                    new NearbyModeIntent(NearbyUserMode.Off, null),
                    CancellationToken.None);
            }

            lock (sync)
            {
                var persistenceState = snapshot.IntentPersistenceState;
                snapshot = physicalStopped
                    ? StoppedSnapshot(
                        reason,
                        OffPolicy(),
                        persistenceState)
                    : new NearbyCoordinatorSnapshot(
                        NearbyUserMode.Off,
                        NearbyEffectiveState.StopFailed,
                        NearbyStopReason.StopFailed,
                        OffPolicy(),
                        Polling(suppress: false),
                        EmergencyEndsAtUtc: null,
                        persistenceState);
                if (ReferenceEquals(stopTask, completion.Task))
                {
                    stopTask = null;
                }
            }

            attempt?.DisposeResources();
            completion.TrySetResult();
        }
    }

    private async Task<bool> StopRadioAsync(NearbyStopReason reason)
    {
        try
        {
            await radioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                externalCallbackDepth.Value++;
                await radio.StopAsync(
                    reason,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                externalCallbackDepth.Value--;
                radioGate.Release();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InvokeAdapterStartAndSignalAsync(
        NearbyAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await radioGate.WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                externalCallbackDepth.Value++;
                await radio.StartForegroundAsync(
                    attempt.Session,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                externalCallbackDepth.Value--;
                radioGate.Release();
            }
        }
        finally
        {
            attempt.PhysicalStartCompletion.TrySetResult();
        }
    }

    private Task EnqueueIntentSave(
        NearbyModeIntent intent,
        CancellationToken cancellationToken)
    {
        if (intentStore is null)
        {
            return Task.CompletedTask;
        }

        var operationGeneration = Interlocked.Increment(ref intentGeneration);
        Interlocked.Increment(ref pendingIntentOperations);
        lock (sync)
        {
            snapshot = snapshot with
            {
                IntentPersistenceState = NearbyIntentPersistenceState.Pending
            };
        }

        Task operation;
        lock (intentSync)
        {
            operation = RunIntentSaveAsync(
                lastIntentTransition,
                intent,
                cancellationToken);
            lastIntentTransition = ObserveIntentSaveAsync(
                operation,
                operationGeneration);
        }

        return operation;
    }

    private async Task RunIntentSaveAsync(
        Task previous,
        NearbyModeIntent intent,
        CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);
        externalCallbackDepth.Value++;
        try
        {
            await intentStore!.SaveAsync(intent, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            externalCallbackDepth.Value--;
        }
    }

    private async Task ObserveIntentSaveAsync(
        Task operation,
        int operationGeneration)
    {
        var persistenceState = NearbyIntentPersistenceState.Consistent;
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            persistenceState = NearbyIntentPersistenceState.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref pendingIntentOperations);
            if (operationGeneration == Volatile.Read(ref intentGeneration))
            {
                lock (sync)
                {
                    snapshot = snapshot with
                    {
                        IntentPersistenceState = persistenceState
                    };
                }
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

    private void ThrowIfAdapterReentrant()
    {
        if (externalCallbackDepth.Value > 0)
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
        NearbyPolicyResult policy,
        NearbyIntentPersistenceState persistenceState =
            NearbyIntentPersistenceState.Consistent) =>
        new(
            NearbyUserMode.Off,
            NearbyEffectiveState.Stopped,
            reason,
            policy,
            Polling(suppress: false),
            EmergencyEndsAtUtc: null,
            persistenceState);

    private static NearbyPolicyResult OffPolicy() =>
        new(
            MayStart: false,
            DenialReason: NearbyPolicyDenialReason.ModeOff);

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
        private readonly object cancellationSync = new();
        private CancellationTokenRegistration callerCancellationRegistration;
        private bool resourcesDisposed;

        public int Generation { get; } = generation;
        public NearbyUserMode Mode { get; } = mode;
        public TimeSpan? Duration { get; } = duration;
        public NearbyRadioCapability Capability { get; } = capability;
        public NearbyRadioSession Session { get; } = session;
        public DateTimeOffset? EmergencyEndsAtUtc { get; } = emergencyEndsAtUtc;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task StartTask { get; set; } = Task.CompletedTask;
        public TaskCompletionSource PhysicalStartCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallerCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CallerCancellationCleanup { get; private set; } =
            Task.CompletedTask;
        public Task? DeadlineTask { get; set; }

        public void AttachCallerCancellation(
            CancellationTokenRegistration registration)
        {
            var unregister = false;
            lock (cancellationSync)
            {
                if (resourcesDisposed)
                {
                    unregister = true;
                }
                else
                {
                    callerCancellationRegistration = registration;
                }
            }

            if (unregister)
            {
                registration.Unregister();
            }
        }

        public void SetCallerCancellationCleanup(Task cleanup)
        {
            lock (cancellationSync)
            {
                CallerCancellationCleanup = cleanup;
            }

            CallerCancellationObserved.TrySetResult();
        }

        public void DisposeCallerCancellation()
        {
            CancellationTokenRegistration registration;
            var completeObservation = false;
            lock (cancellationSync)
            {
                registration = callerCancellationRegistration;
                callerCancellationRegistration = default;
                if (!CallerCancellationObserved.Task.IsCompleted)
                {
                    CallerCancellationCleanup = Task.CompletedTask;
                    completeObservation = true;
                }
            }

            registration.Unregister();
            if (completeObservation)
            {
                CallerCancellationObserved.TrySetResult();
            }
        }

        public void DisposeResources()
        {
            CancellationTokenRegistration registration;
            var completeObservation = false;
            lock (cancellationSync)
            {
                if (resourcesDisposed)
                {
                    return;
                }

                resourcesDisposed = true;
                registration = callerCancellationRegistration;
                callerCancellationRegistration = default;
                if (!CallerCancellationObserved.Task.IsCompleted)
                {
                    CallerCancellationCleanup = Task.CompletedTask;
                    completeObservation = true;
                }
            }

            registration.Unregister();
            if (completeObservation)
            {
                CallerCancellationObserved.TrySetResult();
            }

            Cancellation.Dispose();
        }
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
