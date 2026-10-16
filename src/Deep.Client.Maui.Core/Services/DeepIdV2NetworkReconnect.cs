using System.Net;
using System.Threading.Channels;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.AccountDirectoryV2;

namespace Deep.Client.Maui.Core.Services;

public enum DeepIdV2ReconnectState { Dormant, Offline, Reconnecting, Verified, Unavailable, Rejected }

/// <summary>
/// Process-owned scheduling only, never proof/route/message authority. Each
/// attempt must open fresh transports and execute the complete DID2 composition.
/// Persistent account/floors are retained across connectivity and window changes.
/// </summary>
public sealed class DeepIdV2NetworkReconnect : IAsyncDisposable
{
    private readonly Func<CancellationToken,Task> reconnect;
    private readonly INetworkStatusService network;
    private readonly Action<Action> dispatch;
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim pipelineGate = new(1,1);
    private readonly TimeSpan refreshInterval;
    private readonly TimeSpan foregroundOperationTimeout;
    private readonly Task worker;
    private CancellationTokenSource? attempt;
    private CancellationTokenSource? foregroundAttempt;
    private TaskCompletionSource? requestedVerification;
    private bool verificationWakeAllowed;
    private long revision;
    private bool foreground, disposed;
    private int state;
    private int accountMutations;
    public DeepIdV2NetworkReconnect(Func<CancellationToken,Task> reconnect, INetworkStatusService network,
        Action<Action>? dispatch = null)
        : this(reconnect, network, dispatch, TimeSpan.FromSeconds(60)) { }
    internal DeepIdV2NetworkReconnect(Func<CancellationToken,Task> reconnect, INetworkStatusService network,
        Action<Action>? dispatch, TimeSpan refreshInterval, TimeSpan? foregroundOperationTimeout = null)
    {
        this.reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.dispatch = dispatch ?? (action => action());
        if (refreshInterval <= TimeSpan.Zero || refreshInterval > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        this.refreshInterval = refreshInterval;
        this.foregroundOperationTimeout = foregroundOperationTimeout ?? TimeSpan.FromMinutes(2);
        if (this.foregroundOperationTimeout <= TimeSpan.Zero || this.foregroundOperationTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(foregroundOperationTimeout));
        network.StatusChanged += ConnectivityChanged;
        worker = RunAsync();
    }
    public DeepIdV2ReconnectState State => (DeepIdV2ReconnectState)Volatile.Read(ref state);
    public event EventHandler? StateChanged;
    // Join the actual scheduled pipeline, never a retained success flag. A
    // caller cancelling its wait cannot cancel another caller/background owner.
    // Lifecycle/account invalidation still cancels the underlying operation.
    public Task VerifyNowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (!foreground || !network.IsConnected || accountMutations != 0)
                throw new IOException("DID2 network verification requires an active connected account window.");
            requestedVerification ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (verificationWakeAllowed) wake.Writer.TryWrite(0);
            return requestedVerification.Task.WaitAsync(cancellationToken);
        }
    }
    // Scheduling only: business work still obtains its own current authority.
    // It shares the actual refresh/mutation lane, not a second account pipeline.
    public async Task<T> RunForegroundAsync<T>(Func<CancellationToken,Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        long stamp;
        lock (gate) { RequireForeground(); stamp = revision; }
        // Preserve the caller's UI context: the business delegate may update
        // its owned ViewModel after awaits. The background worker stays free
        // to use ConfigureAwait(false) in its separate scheduling loop.
        await pipelineGate.WaitAsync(linked.Token);
        try {
            lock (gate) {
                if (stamp != revision) throw new OperationCanceledException(linked.Token);
                RequireForeground();
                foregroundAttempt = linked;
            }
            // Scheduling only, like the background attempt: do not consume
            // the active budget while queued behind another owner. Cancel the
            // actual delegate token, but keep ownership until it has drained;
            // racing WaitAsync against it would permit overlapping mutations.
            // Cancellation is not proof of noncommit or replacement authority.
            linked.CancelAfter(foregroundOperationTimeout);
            var result = await action(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate) { if (stamp != revision) throw new OperationCanceledException(linked.Token); }
            return result;
        } finally {
            lock (gate) { if (ReferenceEquals(foregroundAttempt,linked)) foregroundAttempt = null; }
            pipelineGate.Release();
        }
    }
    public Task RunForegroundAsync(Func<CancellationToken,Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunForegroundAsync(async token => { await action(token).ConfigureAwait(false); return true; },cancellationToken);
    }
    private void RequireForeground()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (!foreground || !network.IsConnected || accountMutations != 0)
            throw new IOException("DID2 business work requires an active connected account window.");
    }
    public void Resume() { lock (gate) { if (disposed || foreground) return; foreground = true; Invalidate(); } }
    public void Suspend() { lock (gate) { if (disposed || !foreground) return; foreground = false; Invalidate(); } }
    public void AccountChanged() { lock (gate) { if (!disposed) Invalidate(); } }
    public async Task MutateAccountAsync(Func<CancellationToken,Task> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (gate) { ObjectDisposedException.ThrowIf(disposed,this); accountMutations++; Invalidate(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        var entered = false;
        try {
            await pipelineGate.WaitAsync(linked.Token).ConfigureAwait(false); entered=true;
            await mutation(linked.Token).ConfigureAwait(false);
        } finally {
            if (entered) pipelineGate.Release();
            lock (gate) { accountMutations--; if (!disposed) Invalidate(); }
        }
    }
    private void ConnectivityChanged(object? sender,EventArgs args) { lock (gate) { if (!disposed) Invalidate(); } }
    private void Invalidate()
    {
        var stamp = ++revision;
        var previous = attempt;
        var previousForeground = foregroundAttempt;
        CompleteRequestedVerification(new OperationCanceledException());
        SetState(foreground ? network.IsConnected ? DeepIdV2ReconnectState.Reconnecting : DeepIdV2ReconnectState.Offline : DeepIdV2ReconnectState.Dormant);
        // State notifications may reenter and supersede this invalidation.
        if (stamp != revision) return;
        wake.Writer.TryWrite(0);
        // Publish state/wake before cancellation: a synchronous cancellation
        // continuation can run the worker inside this reentrant lock. Nothing
        // from the old invalidation may overwrite its newer success afterward.
        if (ReferenceEquals(previous, attempt)) previous?.Cancel();
        if (ReferenceEquals(previousForeground, foregroundAttempt)) previousForeground?.Cancel();
    }
    private void SetState(DeepIdV2ReconnectState value)
    {
        if (Interlocked.Exchange(ref state,(int)value) != (int)value)
            dispatch(() => StateChanged?.Invoke(this,EventArgs.Empty));
    }
    private async Task RunAsync()
    {
        var failures = 0;
        var backoffStart = 0L;
        var backoff = TimeSpan.Zero;
        try {
            while (!lifetime.IsCancellationRequested) {
                long stamp;
                CancellationToken token;
                lock (gate) {
                    while (wake.Reader.TryRead(out _)) { }
                    stamp = revision;
                    if (!foreground || !network.IsConnected || accountMutations != 0) { token = default; }
                    else { attempt?.Dispose(); attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); token = attempt.Token; }
                }
                if (!token.CanBeCanceled) { await WaitAsync(Timeout.InfiniteTimeSpan); continue; }
                var remaining = backoff - System.Diagnostics.Stopwatch.GetElapsedTime(backoffStart);
                if (remaining > TimeSpan.Zero) { await WaitAsync(remaining, allowVerificationWake:false); continue; }
                lock (gate) {
                    if (stamp != revision) continue;
                    // A routine refresh is not loss of connectivity. This UI
                    // state grants no proof: every foreground operation still
                    // obtains and independently verifies its actual authority.
                    if (State != DeepIdV2ReconnectState.Verified) SetState(DeepIdV2ReconnectState.Reconnecting);
                }
                var wait = refreshInterval;
                try {
                    await pipelineGate.WaitAsync(token).ConfigureAwait(false);
                    try {
                        lock (gate) { if (stamp != revision || accountMutations != 0) continue; }
                        // No authority/HTTP work has started while waiting for
                        // foreground work. Its original2m attempt budget starts
                        // only after owning this lane, never during that queue.
                        attempt!.CancelAfter(TimeSpan.FromMinutes(2));
                        await reconnect(token).ConfigureAwait(false);
                    } finally { pipelineGate.Release(); }
                    lock (gate) {
                        if (stamp != revision || token.IsCancellationRequested || !foreground || !network.IsConnected) continue;
                        SetState(DeepIdV2ReconnectState.Verified);
                        if (stamp == revision) CompleteRequestedVerification(null);
                    }
                    failures = 0; backoff = TimeSpan.Zero;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (stamp != Volatile.Read(ref revision)) { continue; }
                catch (DeepIdV2AccountUnavailableException error) {
                    lock (gate) { if (stamp == revision) { SetState(DeepIdV2ReconnectState.Dormant); CompleteRequestedVerification(error); } }
                    wait = Timeout.InfiniteTimeSpan;
                }
                catch (Exception error) {
                    var transient = ClassifyTransient(error);
                    lock (gate) {
                        if (stamp != revision) continue;
                        SetState(transient is null ? DeepIdV2ReconnectState.Rejected : DeepIdV2ReconnectState.Unavailable);
                        if (stamp == revision) CompleteRequestedVerification(error);
                    }
                    if (transient is null) wait = Timeout.InfiniteTimeSpan;
                    else {
                        failures = Math.Min(failures+1,6);
                        backoff = TimeSpan.FromSeconds(Math.Max(Math.Min(30,1<<failures),Math.Clamp(transient.Value.TotalSeconds,0,300)));
                        backoffStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        wait = backoff;
                    }
                }
                await WaitAsync(wait);
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private static TimeSpan? ClassifyTransient(Exception error)
    {
        // Display wrappers carry their typed cause. Corrupt floors, malformed
        // records, signature failures and generic IO are not retry authority.
        for (var depth = 0; depth < 8; depth++,error = error.InnerException!) {
            // A typed producer's negative decision is authoritative here:
            // an inner HTTP error cannot promote a nonretryable rejection.
            if (error is ClientMailboxTransportException transport)
                return transport is { Failure: ClientMailboxTransportFailure.DependencyUnavailable, Retryable: true }
                    ? TimeSpan.Zero : null;
            if (error is DeepIdV2DirectoryProofUnavailableException unavailable && unavailable.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                return unavailable.RetryAfter ?? TimeSpan.Zero;
            if (error is HttpRequestException or TimeoutException or OperationCanceledException) return TimeSpan.Zero;
            if (error.InnerException is null) break;
        }
        return null;
    }
    // Called under gate. Observe faulted tasks even if every UI waiter was
    // cancelled; later awaiters still receive the exact failure normally.
    private void CompleteRequestedVerification(Exception? error)
    {
        var pending = requestedVerification; requestedVerification = null;
        if (pending is null) return;
        if (error is OperationCanceledException) pending.TrySetCanceled();
        else if (error is not null) { pending.TrySetException(error); _ = pending.Task.Exception; }
        else pending.TrySetResult();
    }
    private async Task WaitAsync(TimeSpan delay, bool allowVerificationWake = true)
    {
        lock (gate) {
            verificationWakeAllowed = allowVerificationWake;
            if (allowVerificationWake && requestedVerification is not null) wake.Writer.TryWrite(0);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var changed = wake.Reader.ReadAsync(stop.Token).AsTask();
        var timer = Task.Delay(delay,stop.Token);
        _ = await Task.WhenAny(changed,timer).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try { await changed.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (gate) verificationWakeAllowed = false;
        lifetime.Token.ThrowIfCancellationRequested();
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) {
            if (disposed) return; disposed = true; network.StatusChanged -= ConnectivityChanged;
            CompleteRequestedVerification(new OperationCanceledException());
            lifetime.Cancel(); attempt?.Cancel();
        }
        await worker.ConfigureAwait(false);
        attempt?.Dispose(); lifetime.Dispose();
    }
}

public sealed class DeepIdV2AccountUnavailableException : InvalidOperationException;
