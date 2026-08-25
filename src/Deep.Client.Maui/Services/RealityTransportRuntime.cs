#if ANDROID
using ConnectivityManager = Android.Net.ConnectivityManager;
using Network = Android.Net.Network;
using NetworkCapabilities = Android.Net.NetworkCapabilities;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Deep.Client.Maui.Core.Services;
using Microsoft.Maui.Storage;
#endif

namespace Deep.Client.Maui;

#if ANDROID
internal sealed class AndroidRealityTransportRuntime : IRealityTransportRuntime
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly object Sync = new();
    private global::LibXray.IDialerController? dialerController;
    private readonly IReadOnlyList<RealityRouterEndpoint> routerEndpoints;
    private readonly IReadOnlyList<RealitySeed> configuredSeeds;
    private readonly RealityStartupCoordinator startupCoordinator;
    private readonly ConnectivityManager? connectivityManager;
    private readonly ConnectivityManager.NetworkCallback? networkCallback;
    private readonly RealityForegroundLifecycle foregroundLifecycle = new();
    private int disposed;

    public AndroidRealityTransportRuntime()
    {
        var bootstrap = RealityTransportConfiguration.LoadEmbedded(typeof(AndroidRealityTransportRuntime).Assembly);
#if DEEP_PHYSICAL_E2E
        bootstrap = RealityTransportConfiguration.ApplyLocalPortProfile(
            bootstrap,
            RealityTransportPortProfile.PhysicalE2E);
#endif
        routerEndpoints = RealityTransportConfiguration.BuildRouterEndpoints(bootstrap);
        configuredSeeds = bootstrap.Seeds.ToArray();
        startupCoordinator = new RealityStartupCoordinator(
            StartCoreAsync,
            ProbeListenerAsync,
            initialRetryDelay: TimeSpan.FromMilliseconds(250),
            maximumRetryDelay: TimeSpan.FromSeconds(2),
            listenerPollInterval: TimeSpan.FromMilliseconds(100),
            invalidatedSuccessCleanupAsync: () => StopAsync(),
            startupFailed: static exception => global::Android.Util.Log.Warn(
                "DeepXray",
                $"Embedded Xray startup failed and remains retryable: {exception.GetType().Name}"));
        _ = startupCoordinator.TrySetRecoveryEnabled(false);
        connectivityManager = Microsoft.Maui.ApplicationModel.Platform.AppContext
            .GetSystemService(global::Android.Content.Context.ConnectivityService) as ConnectivityManager;
        if (connectivityManager is not null && OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            networkCallback = new RealityNetworkCallback(this);
            try
            {
                connectivityManager.RegisterDefaultNetworkCallback(networkCallback);
            }
            catch (global::Java.Lang.Exception)
            {
                networkCallback = null;
            }
        }
    }

    public IReadOnlyList<RealityRouterEndpoint> RouterEndpoints => routerEndpoints;

    public RealityTransportEndpointSource EndpointSource => RealityTransportEndpointSource.Embedded;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        using var lease = foregroundLifecycle.Capture(cancellationToken);
        await RunWithTimeoutAsync(
            startupCoordinator.EnsureStartedAsync,
            ReadinessTimeout,
            "Embedded Xray startup did not complete in time.",
            lease.Token).ConfigureAwait(false);
        foregroundLifecycle.Validate(lease.Generation, cancellationToken);
    }

    public Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken)
    {
        var seed = RealityTransportConfiguration.FindSeedForRequest(configuredSeeds, requestUri);
        if (seed is null)
        {
            return Task.CompletedTask;
        }

        return foregroundLifecycle.IsForeground
            ? WaitForForegroundListenerAsync(seed, cancellationToken)
            : RequireExistingBackgroundListenerAsync(seed, cancellationToken);
    }

    public void SetForeground(bool isForeground)
    {
        if (isForeground && Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (isForeground)
        {
            foregroundLifecycle.SetForeground(true);
            _ = startupCoordinator.TrySetRecoveryEnabled(true);
            return;
        }

        // The coordinator owns startup publication. Invalidate its generation first so a
        // non-cooperative native start cannot publish success during the lifecycle transition.
        _ = startupCoordinator.TrySetRecoveryEnabled(false);
        foregroundLifecycle.SetForeground(false);
    }

    public void NotifyNetworkChanged()
    {
        _ = startupCoordinator.TryInvalidateReadiness();
    }

    public Task OnForegroundAsync(CancellationToken cancellationToken = default)
    {
        SetForeground(true);
        return EnsureStartedAsync(cancellationToken);
    }

    private async Task WaitForForegroundListenerAsync(
        RealitySeed seed,
        CancellationToken cancellationToken)
    {
        using var lease = foregroundLifecycle.Capture(cancellationToken);
        await WaitForListenerAsync(seed, lease.Token).ConfigureAwait(false);
        foregroundLifecycle.Validate(lease.Generation, cancellationToken);
    }

    private Task StartCoreAsync(
        bool restart,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => StartCore(restart, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private void StartCore(
        bool restart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisterDialerController();

        if (restart && global::LibXray.LibXray.XrayState)
        {
            _ = global::LibXray.LibXray.StopXray();
            WaitUntilStopped(cancellationToken);
        }

        if (global::LibXray.LibXray.XrayState)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataDirectory = Path.Combine(MauiProgram.ResolveAppDataDirectory(), "xray");
        Directory.CreateDirectory(dataDirectory);
        var config = RealityTransportConfiguration.BuildXrayConfig(configuredSeeds);
        try
        {
            var request = global::LibXray.LibXray.NewXrayRunFromJSONRequest(dataDirectory, string.Empty, config)
                ?? throw new InvalidOperationException("libXray did not create a startup request.");
            var response = global::LibXray.LibXray.RunXrayFromJSON(request)
                ?? throw new InvalidOperationException("libXray did not return a startup response.");
            EnsureSuccess(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw RealityTransportFailure.CreateSanitizedStartupException(exception.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (global::LibXray.LibXray.XrayState)
            {
                _ = global::LibXray.LibXray.StopXray();
                WaitUntilStopped(CancellationToken.None);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void WaitUntilStopped(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (global::LibXray.LibXray.XrayState && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }

        if (global::LibXray.LibXray.XrayState)
        {
            throw new InvalidOperationException("Embedded Xray did not stop before restart.");
        }
    }

    private void RegisterDialerController()
    {
        dialerController ??= new AndroidXrayDialerController();
        global::LibXray.LibXray.RegisterDialerController(dialerController);
    }

    private static void EnsureSuccess(string encodedResponse)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encodedResponse);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("libXray returned an invalid startup response.", exception);
        }

        using var response = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        if (!response.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var untrustedError = response.RootElement.TryGetProperty("error", out var value)
                ? value.GetString()
                : null;
            throw RealityTransportFailure.CreateSanitizedStartupException(untrustedError);
        }
    }

    private async Task RequireExistingBackgroundListenerAsync(
        RealitySeed seed,
        CancellationToken cancellationToken)
    {
        if (await startupCoordinator.TryUseExistingListenerAsync(seed.LocalPort, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            "Embedded Xray listener is unavailable while Deep is backgrounded; recovery is deferred until foreground.");
    }

    private async Task WaitForListenerAsync(
        RealitySeed seed,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        try
        {
            await startupCoordinator.WaitUntilReadyAsync(seed.LocalPort, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"Embedded Xray did not open the listener for router {seed.RouterId} on local port {seed.LocalPort}.",
                exception);
        }
    }

    private static async Task<bool> ProbeListenerAsync(int localPort, CancellationToken cancellationToken)
    {
        using var socket = new TcpClient();
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await socket.ConnectAsync("127.0.0.1", localPort, attemptTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Factory.StartNew(
            () =>
            {
                lock (Sync)
                {
                    if (global::LibXray.LibXray.XrayState)
                    {
                        _ = global::LibXray.LibXray.StopXray();
                        WaitUntilStopped(CancellationToken.None);
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        SetForeground(false);
        foregroundLifecycle.Dispose();
        if (networkCallback is not null && connectivityManager is not null)
        {
            try
            {
                connectivityManager.UnregisterNetworkCallback(networkCallback);
            }
            catch (global::Java.Lang.Exception)
            {
                // The operating system may already have removed the callback during shutdown.
            }
        }
        var coordinatorDisposal = startupCoordinator.DisposeAsync().AsTask();
        var stop = StopAsync();
        try
        {
            await Task.WhenAll(coordinatorDisposal, stop)
                .WaitAsync(ShutdownTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateCompletion(coordinatorDisposal);
            ObserveLateCompletion(stop);
        }
    }

    private static async Task RunWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            await operation(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(timeoutMessage, exception);
        }
    }

    private static void ObserveLateCompletion(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class RealityNetworkCallback(AndroidRealityTransportRuntime owner)
        : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.NotifyNetworkChanged();

        public override void OnLost(Network network) => owner.NotifyNetworkChanged();

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities) =>
            owner.NotifyNetworkChanged();
    }

}
#endif

internal static class RealityTransportFailure
{
    internal static InvalidOperationException CreateSanitizedStartupException(string? untrustedNativeError)
    {
        _ = untrustedNativeError;
        return new InvalidOperationException(
            "The embedded Reality transport rejected its startup configuration.");
    }
}

internal sealed class RealityForegroundLifecycle : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource transitionCancellation = CreateCancelledSource();
    private long generation;
    private bool foreground;
    private bool disposed;

    public bool IsForeground
    {
        get
        {
            lock (sync)
            {
                return foreground && !disposed;
            }
        }
    }

    public void SetForeground(bool isForeground)
    {
        lock (sync)
        {
            if (disposed || foreground == isForeground)
            {
                return;
            }

            generation++;
            foreground = isForeground;
            if (isForeground)
            {
                transitionCancellation.Dispose();
                transitionCancellation = new CancellationTokenSource();
            }
            else
            {
                CancelNoThrow(transitionCancellation);
            }
        }
    }

    public Lease Capture(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!foreground)
            {
                throw new InvalidOperationException(
                    "Reality transport recovery is deferred while the application is backgrounded.");
            }

            return new Lease(
                generation,
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    transitionCancellation.Token));
        }
    }

    public void Validate(long expectedGeneration, CancellationToken callerCancellation)
    {
        callerCancellation.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (disposed || !foreground || expectedGeneration != generation)
            {
                throw new OperationCanceledException(
                    "Reality transport foreground operation was cancelled by a lifecycle transition.");
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreground = false;
            generation++;
            CancelNoThrow(transitionCancellation);
            transitionCancellation.Dispose();
        }
    }

    private static CancellationTokenSource CreateCancelledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }

    private static void CancelNoThrow(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // Lifecycle cancellation must not escape a platform callback.
        }
    }

    internal sealed class Lease(
        long generation,
        CancellationTokenSource cancellation) : IDisposable
    {
        public long Generation { get; } = generation;

        public CancellationToken Token => cancellation.Token;

        public void Dispose() => cancellation.Dispose();
    }
}

internal sealed class RealityStartupCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<bool, CancellationToken, Task> startAsync;
    private readonly Func<int, CancellationToken, Task<bool>> probeListenerAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Action<Exception>? startupFailed;
    private readonly TimeSpan initialRetryDelay;
    private readonly TimeSpan maximumRetryDelay;
    private readonly TimeSpan listenerPollInterval;
    private readonly TimeSpan disposalWaitTimeout;
    private readonly Func<Task>? invalidatedSuccessCleanupAsync;
    private readonly Action<CancellationTokenSource> cancelAttempt;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private StartupAttempt? currentAttempt;
    private ListenerRecovery? listenerRecovery;
    private Task? disposalTask;
    private int consecutiveFailures;
    private long recoveryGeneration;
    private bool restartPending;
    private bool recoveryEnabled = true;
    private bool disposed;

    public RealityStartupCoordinator(
        Func<bool, CancellationToken, Task> startAsync,
        Func<int, CancellationToken, Task<bool>> probeListenerAsync,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay,
        TimeSpan listenerPollInterval,
        TimeSpan? disposalWaitTimeout = null,
        Func<Task>? invalidatedSuccessCleanupAsync = null,
        Action<CancellationTokenSource>? cancelAttempt = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<Exception>? startupFailed = null)
    {
        ArgumentNullException.ThrowIfNull(startAsync);
        ArgumentNullException.ThrowIfNull(probeListenerAsync);
        if (initialRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        }
        if (maximumRetryDelay < initialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
        }
        if (listenerPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPollInterval));
        }

        this.startAsync = startAsync;
        this.probeListenerAsync = probeListenerAsync;
        this.initialRetryDelay = initialRetryDelay;
        this.maximumRetryDelay = maximumRetryDelay;
        this.listenerPollInterval = listenerPollInterval;
        this.disposalWaitTimeout = disposalWaitTimeout ?? TimeSpan.FromSeconds(5);
        if (this.disposalWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposalWaitTimeout));
        }
        this.invalidatedSuccessCleanupAsync = invalidatedSuccessCleanupAsync;
        this.cancelAttempt = cancelAttempt ?? (static source => source.Cancel());
        this.delayAsync = delayAsync ?? Task.Delay;
        this.startupFailed = startupFailed;
    }

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StartupAttempt? attemptToRun = null;
        Task sharedTask;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            ThrowIfRecoveryDisabledLocked();
            if (currentAttempt is null)
            {
                attemptToRun = new StartupAttempt(
                    restartPending,
                    GetRetryDelayLocked(),
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token),
                    recoveryGeneration);
                restartPending = false;
                currentAttempt = attemptToRun;
            }

            sharedTask = currentAttempt.Completion.Task;
        }

        if (attemptToRun is not null)
        {
            _ = RunAttemptAsync(attemptToRun);
        }

        return cancellationToken.CanBeCanceled
            ? sharedTask.WaitAsync(cancellationToken)
            : sharedTask;
    }

    public async Task WaitUntilReadyAsync(int listenerPort, CancellationToken cancellationToken)
    {
        if (listenerPort is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort));
        }

        var restartRequired = false;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            ThrowIfRecoveryDisabledLocked();
            restartRequired = restartPending;
        }

        if (!restartRequired &&
            await ProbeAndMarkHealthyAsync(listenerPort, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        while (true)
        {
            try
            {
                await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        if (await ProbeAndMarkHealthyAsync(listenerPort, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var recovery = await AcquireListenerRecoveryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                if (await ProbeAndMarkHealthyAsync(listenerPort, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await delayAsync(listenerPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseListenerRecovery(recovery);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StartupAttempt? observedAttempt;
        bool shareActiveRestart;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            ThrowIfRecoveryDisabledLocked();
            observedAttempt = currentAttempt;
            shareActiveRestart = observedAttempt?.IsRestart == true
                && !observedAttempt.Completion.Task.IsCompleted;
            if (!shareActiveRestart)
            {
                restartPending = true;
                if (observedAttempt?.Completion.Task.IsCompleted == true)
                {
                    currentAttempt = null;
                    observedAttempt = null;
                }
            }
        }

        if (shareActiveRestart)
        {
            await observedAttempt!.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (observedAttempt is not null)
        {
            try
            {
                await observedAttempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryUseExistingListenerAsync(
        int listenerPort,
        CancellationToken cancellationToken)
    {
        if (listenerPort is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort));
        }

        lock (sync)
        {
            ThrowIfDisposedLocked();
            if (restartPending || currentAttempt is null || !currentAttempt.Completion.Task.IsCompletedSuccessfully)
            {
                return false;
            }
        }

        return await probeListenerAsync(listenerPort, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks cached listener health stale without creating background work. The next foreground
    /// or routed request performs the single-flight restart through the normal bounded path.
    /// </summary>
    public bool TryInvalidateReadiness()
    {
        lock (sync)
        {
            if (disposed)
            {
                return false;
            }

            restartPending = true;
            listenerRecovery = null;
            if (currentAttempt?.Completion.Task.IsCompleted == true)
            {
                currentAttempt = null;
            }

            return true;
        }
    }

    public bool TrySetRecoveryEnabled(bool enabled)
    {
        CancellationTokenSource? cancellation = null;
        lock (sync)
        {
            if (disposed)
            {
                return false;
            }

            if (recoveryEnabled == enabled)
            {
                return true;
            }

            recoveryEnabled = enabled;
            recoveryGeneration++;
            if (!enabled && currentAttempt is { } attempt && !attempt.Completion.Task.IsCompleted)
            {
                restartPending = true;
                attempt.Invalidated = true;
                cancellation = attempt.Cancellation;
            }

            if (!enabled && listenerRecovery is not null)
            {
                restartPending = true;
            }

        }

        try
        {
            if (cancellation is not null)
            {
                cancelAttempt(cancellation);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or AggregateException)
        {
            // The attempt completed or a cancellation callback failed after the guarded snapshot.
        }
        return true;
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? disposalCompletion = null;
        Task? activeTask = null;
        lock (sync)
        {
            if (disposalTask is not null)
            {
                return new ValueTask(disposalTask);
            }

            disposed = true;
            recoveryGeneration++;
            restartPending = false;
            listenerRecovery = null;
            if (currentAttempt is { } attempt)
            {
                attempt.Invalidated = true;
            }
            activeTask = currentAttempt?.Completion.Task;
            currentAttempt = null;
            disposalCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = disposalCompletion.Task;
        }

        lifetimeCancellation.Cancel();
        _ = FinishDisposalAsync(activeTask, disposalCompletion);
        return new ValueTask(disposalTask);
    }

    internal static bool TargetsListener(Uri? requestUri, int listenerPort)
    {
        return requestUri is { IsAbsoluteUri: true, IsLoopback: true }
            && requestUri.Scheme == Uri.UriSchemeHttp
            && requestUri.Port == listenerPort;
    }

    private async Task RunAttemptAsync(StartupAttempt attempt)
    {
        Exception? error = null;
        var cancelled = false;
        var startReturnedSuccessfully = false;
        try
        {
            if (attempt.RetryDelay > TimeSpan.Zero)
            {
                await delayAsync(attempt.RetryDelay, attempt.Cancellation.Token).ConfigureAwait(false);
            }

            await startAsync(attempt.IsRestart, attempt.Cancellation.Token).ConfigureAwait(false);
            startReturnedSuccessfully = true;
            attempt.Cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (attempt.Cancellation.IsCancellationRequested)
        {
            error = exception;
            cancelled = true;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var cleanupInvalidatedSuccess = false;
        var notifyFailure = false;
        lock (sync)
        {
            if (error is null && startReturnedSuccessfully &&
                (attempt.Invalidated || attempt.Generation != recoveryGeneration || !recoveryEnabled || disposed))
            {
                error = new OperationCanceledException(
                    "Reality transport startup completed after its lifecycle generation was invalidated.");
                cancelled = true;
                cleanupInvalidatedSuccess = true;
            }

            if (!cleanupInvalidatedSuccess)
            {
                if (ReferenceEquals(currentAttempt, attempt))
                {
                    if (error is null)
                    {
                        consecutiveFailures = 0;
                        if (restartPending)
                        {
                            currentAttempt = null;
                        }
                    }
                    else
                    {
                        consecutiveFailures = Math.Min(consecutiveFailures + 1, 31);
                        restartPending = true;
                        currentAttempt = null;
                    }
                }

                if (error is null)
                {
                    attempt.Completion.TrySetResult();
                }
                else if (cancelled)
                {
                    attempt.Completion.TrySetCanceled();
                }
                else
                {
                    attempt.Completion.TrySetException(error);
                    notifyFailure = true;
                }
            }
        }

        if (cleanupInvalidatedSuccess)
        {
            await CleanupInvalidatedSuccessAsync().ConfigureAwait(false);
            lock (sync)
            {
                if (ReferenceEquals(currentAttempt, attempt))
                {
                    consecutiveFailures = Math.Max(consecutiveFailures, 1);
                    restartPending = true;
                    currentAttempt = null;
                }

                attempt.Completion.TrySetCanceled();
            }
        }
        else if (notifyFailure)
        {
            NotifyStartupFailed(error!);
        }

        attempt.Cancellation.Dispose();
    }

    private async Task CleanupInvalidatedSuccessAsync()
    {
        if (invalidatedSuccessCleanupAsync is null)
        {
            return;
        }

        try
        {
            await invalidatedSuccessCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NotifyStartupFailed(exception);
        }
    }

    private async Task<bool> ProbeAndMarkHealthyAsync(
        int listenerPort,
        CancellationToken cancellationToken)
    {
        if (!await probeListenerAsync(listenerPort, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        lock (sync)
        {
            if (listenerRecovery is { } recovery)
            {
                recovery.HealthObserved = true;
                if (recovery.Waiters == 0 && recovery.Completion.Task.IsCompleted)
                {
                    listenerRecovery = null;
                }
            }

            consecutiveFailures = 0;
        }

        return true;
    }

    private async Task<ListenerRecovery> AcquireListenerRecoveryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ListenerRecovery? recoveryToRun = null;
        ListenerRecovery recovery;
        Task sharedTask;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            ThrowIfRecoveryDisabledLocked();
            if (listenerRecovery is null)
            {
                recoveryToRun = new ListenerRecovery(
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                listenerRecovery = recoveryToRun;
            }

            recovery = listenerRecovery;
            recovery.Waiters++;
            sharedTask = recovery.Completion.Task;
        }

        if (recoveryToRun is not null)
        {
            _ = RunListenerRecoveryAsync(recoveryToRun);
        }

        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await sharedTask.ConfigureAwait(false);
            }

            return recovery;
        }
        catch
        {
            ReleaseListenerRecovery(recovery);
            throw;
        }
    }

    private async Task RunListenerRecoveryAsync(ListenerRecovery recovery)
    {
        try
        {
            await RestartAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            recovery.Completion.TrySetResult();
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            recovery.Completion.TrySetCanceled(lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            recovery.Completion.TrySetException(exception);
        }
    }

    private void ReleaseListenerRecovery(ListenerRecovery recovery)
    {
        lock (sync)
        {
            if (!ReferenceEquals(listenerRecovery, recovery))
            {
                return;
            }

            recovery.Waiters--;
            if (recovery.Waiters != 0 || !recovery.Completion.Task.IsCompleted)
            {
                return;
            }

            listenerRecovery = null;
            if (!recovery.HealthObserved)
            {
                consecutiveFailures = Math.Max(consecutiveFailures, 1);
            }
        }
    }

    private TimeSpan GetRetryDelayLocked()
    {
        if (consecutiveFailures == 0)
        {
            return TimeSpan.Zero;
        }

        var delayTicks = initialRetryDelay.Ticks;
        for (var failure = 1; failure < consecutiveFailures && delayTicks < maximumRetryDelay.Ticks; failure++)
        {
            delayTicks = delayTicks > maximumRetryDelay.Ticks / 2
                ? maximumRetryDelay.Ticks
                : delayTicks * 2;
        }

        return TimeSpan.FromTicks(delayTicks);
    }

    private async Task FinishDisposalAsync(Task? activeTask, TaskCompletionSource completion)
    {
        if (activeTask is not null)
        {
            try
            {
                await activeTask.WaitAsync(disposalWaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = DisposeLifetimeAfterLateAttemptAsync(activeTask);
                completion.TrySetResult();
                return;
            }
            catch (Exception)
            {
            }
        }

        lifetimeCancellation.Dispose();
        completion.TrySetResult();
    }

    private async Task DisposeLifetimeAfterLateAttemptAsync(Task activeTask)
    {
        try
        {
            await activeTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            lifetimeCancellation.Dispose();
        }
    }

    private void NotifyStartupFailed(Exception exception)
    {
        try
        {
            startupFailed?.Invoke(exception);
        }
        catch (Exception)
        {
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void ThrowIfRecoveryDisabledLocked()
    {
        if (!recoveryEnabled)
        {
            throw new OperationCanceledException(
                "Reality transport recovery is paused while the application is backgrounded.");
        }
    }

    private sealed class StartupAttempt(
        bool isRestart,
        TimeSpan retryDelay,
        TaskCompletionSource completion,
        CancellationTokenSource cancellation,
        long generation)
    {
        public bool IsRestart { get; } = isRestart;

        public TimeSpan RetryDelay { get; } = retryDelay;

        public TaskCompletionSource Completion { get; } = completion;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public long Generation { get; } = generation;

        public bool Invalidated { get; set; }
    }

    private sealed class ListenerRecovery(TaskCompletionSource completion)
    {
        public TaskCompletionSource Completion { get; } = completion;

        public int Waiters { get; set; }

        public bool HealthObserved { get; set; }
    }
}
