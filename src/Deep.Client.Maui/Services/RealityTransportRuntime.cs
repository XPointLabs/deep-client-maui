#if ANDROID
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;
#endif

namespace Deep.Client.Maui;

#if ANDROID
internal static class AndroidRealityTransport
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
    private static readonly object Sync = new();
    private static global::LibXray.IDialerController? dialerController;
    private static IReadOnlyList<PinnedRouterEndpoint>? routerEndpoints;
    private static IReadOnlyList<RealitySeed>? configuredSeeds;
    private static RealityStartupCoordinator? startupCoordinator;

    public static IReadOnlyList<PinnedRouterEndpoint> Start()
    {
        lock (Sync)
        {
            if (routerEndpoints is not null)
            {
                return routerEndpoints;
            }

            var bootstrap = RealityTransportConfiguration.LoadEmbedded(typeof(AndroidRealityTransport).Assembly);
            var endpoints = RealityTransportConfiguration.BuildRouterEndpoints(bootstrap);

            var seeds = bootstrap.Seeds.ToArray();
            var coordinator = new RealityStartupCoordinator(
                (restart, cancellationToken) => StartCoreAsync(seeds, restart, cancellationToken),
                ProbeListenerAsync,
                initialRetryDelay: TimeSpan.FromMilliseconds(250),
                maximumRetryDelay: TimeSpan.FromSeconds(2),
                listenerPollInterval: TimeSpan.FromMilliseconds(100),
                startupFailed: static exception => global::Android.Util.Log.Warn(
                    "DeepXray",
                    $"Embedded Xray startup failed and remains retryable: {exception.Message}"));

            routerEndpoints = endpoints;
            configuredSeeds = seeds;
            startupCoordinator = coordinator;
            ObserveInitialStartup(coordinator);
            return routerEndpoints;
        }
    }

    public static Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken)
    {
        RealitySeed? seed;
        RealityStartupCoordinator? coordinator;
        lock (Sync)
        {
            seed = RealityTransportConfiguration.FindSeedForRequest(configuredSeeds, requestUri);
            coordinator = startupCoordinator;
        }

        return seed is null || coordinator is null
            ? Task.CompletedTask
            : WaitForListenerAsync(seed, coordinator, cancellationToken);
    }

    private static void ObserveInitialStartup(RealityStartupCoordinator coordinator)
    {
        _ = ObserveInitialStartupAsync(coordinator);
    }

    private static async Task ObserveInitialStartupAsync(RealityStartupCoordinator coordinator)
    {
        try
        {
            await coordinator.EnsureStartedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn(
                "DeepXray",
                $"Initial embedded Xray startup did not complete: {exception.Message}");
        }
    }

    private static Task StartCoreAsync(
        IReadOnlyList<RealitySeed> seeds,
        bool restart,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => StartCore(seeds, restart, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void StartCore(
        IReadOnlyList<RealitySeed> seeds,
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
        var dataDirectory = Path.Combine(FileSystem.AppDataDirectory, "xray");
        Directory.CreateDirectory(dataDirectory);
        var config = RealityTransportConfiguration.BuildXrayConfig(seeds);
        var request = global::LibXray.LibXray.NewXrayRunFromJSONRequest(dataDirectory, string.Empty, config)
            ?? throw new InvalidOperationException("libXray did not create a startup request.");
        var response = global::LibXray.LibXray.RunXrayFromJSON(request)
            ?? throw new InvalidOperationException("libXray did not return a startup response.");
        EnsureSuccess(response);
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

    private static void RegisterDialerController()
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
            var error = response.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            throw new InvalidOperationException($"Could not start embedded Xray: {error ?? "unknown error"}");
        }
    }

    private static async Task WaitForListenerAsync(
        RealitySeed seed,
        RealityStartupCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        try
        {
            await coordinator.WaitUntilReadyAsync(seed.LocalPort, timeout.Token).ConfigureAwait(false);
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

}
#endif

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
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private StartupAttempt? currentAttempt;
    private ListenerRecovery? listenerRecovery;
    private Task? disposalTask;
    private int consecutiveFailures;
    private bool restartPending;
    private bool disposed;

    public RealityStartupCoordinator(
        Func<bool, CancellationToken, Task> startAsync,
        Func<int, CancellationToken, Task<bool>> probeListenerAsync,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay,
        TimeSpan listenerPollInterval,
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
            if (currentAttempt is null)
            {
                attemptToRun = new StartupAttempt(
                    restartPending,
                    GetRetryDelayLocked(),
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
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

        if (await ProbeAndMarkHealthyAsync(listenerPort, cancellationToken).ConfigureAwait(false))
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
            restartPending = false;
            listenerRecovery = null;
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
        try
        {
            if (attempt.RetryDelay > TimeSpan.Zero)
            {
                await delayAsync(attempt.RetryDelay, lifetimeCancellation.Token).ConfigureAwait(false);
            }

            await startAsync(attempt.IsRestart, lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (lifetimeCancellation.IsCancellationRequested)
        {
            error = exception;
            cancelled = true;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        lock (sync)
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
        }

        if (error is null)
        {
            attempt.Completion.TrySetResult();
        }
        else if (cancelled)
        {
            attempt.Completion.TrySetCanceled(lifetimeCancellation.Token);
        }
        else
        {
            attempt.Completion.TrySetException(error);
            NotifyStartupFailed(error);
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
                await activeTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        lifetimeCancellation.Dispose();
        completion.TrySetResult();
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

    private sealed record StartupAttempt(
        bool IsRestart,
        TimeSpan RetryDelay,
        TaskCompletionSource Completion);

    private sealed class ListenerRecovery(TaskCompletionSource completion)
    {
        public TaskCompletionSource Completion { get; } = completion;

        public int Waiters { get; set; }

        public bool HealthObserved { get; set; }
    }
}
