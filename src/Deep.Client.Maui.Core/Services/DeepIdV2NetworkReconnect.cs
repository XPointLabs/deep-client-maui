using System.Net;
using System.Threading.Channels;
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
    private readonly Task worker;
    private CancellationTokenSource? attempt;
    private long revision;
    private bool foreground, disposed;
    private int state;
    private int accountMutations;
    public DeepIdV2NetworkReconnect(Func<CancellationToken,Task> reconnect, INetworkStatusService network,
        Action<Action>? dispatch = null)
    {
        this.reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.dispatch = dispatch ?? (action => action());
        network.StatusChanged += ConnectivityChanged;
        worker = RunAsync();
    }
    public DeepIdV2ReconnectState State => (DeepIdV2ReconnectState)Volatile.Read(ref state);
    public event EventHandler? StateChanged;
    public void Resume() { lock (gate) { if (disposed) return; foreground = true; Invalidate(); } }
    public void Suspend() { lock (gate) { if (disposed) return; foreground = false; Invalidate(); } }
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
        SetState(foreground ? network.IsConnected ? DeepIdV2ReconnectState.Reconnecting : DeepIdV2ReconnectState.Offline : DeepIdV2ReconnectState.Dormant);
        // State notifications may reenter and supersede this invalidation.
        if (stamp != revision) return;
        wake.Writer.TryWrite(0);
        // Publish state/wake before cancellation: a synchronous cancellation
        // continuation can run the worker inside this reentrant lock. Nothing
        // from the old invalidation may overwrite its newer success afterward.
        if (ReferenceEquals(previous, attempt)) previous?.Cancel();
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
                    else { attempt?.Dispose(); attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); attempt.CancelAfter(TimeSpan.FromMinutes(2)); token = attempt.Token; }
                }
                if (!token.CanBeCanceled) { await WaitAsync(Timeout.InfiniteTimeSpan); continue; }
                var remaining = backoff - System.Diagnostics.Stopwatch.GetElapsedTime(backoffStart);
                if (remaining > TimeSpan.Zero) { await WaitAsync(remaining); continue; }
                lock (gate) { if (stamp != revision) continue; SetState(DeepIdV2ReconnectState.Reconnecting); }
                var wait = TimeSpan.FromSeconds(60);
                try {
                    await pipelineGate.WaitAsync(token).ConfigureAwait(false);
                    try {
                        lock (gate) { if (stamp != revision || accountMutations != 0) continue; }
                        await reconnect(token).ConfigureAwait(false);
                    } finally { pipelineGate.Release(); }
                    lock (gate) {
                        if (stamp != revision || token.IsCancellationRequested || !foreground || !network.IsConnected) continue;
                        SetState(DeepIdV2ReconnectState.Verified);
                    }
                    failures = 0; backoff = TimeSpan.Zero;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (stamp != Volatile.Read(ref revision)) { continue; }
                catch (DeepIdV2AccountUnavailableException) { lock (gate) { if (stamp == revision) SetState(DeepIdV2ReconnectState.Dormant); } wait = Timeout.InfiniteTimeSpan; }
                catch (Exception error) {
                    var transient = ClassifyTransient(error);
                    lock (gate) { if (stamp != revision) continue; SetState(transient is null ? DeepIdV2ReconnectState.Rejected : DeepIdV2ReconnectState.Unavailable); }
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
            if (error is DeepIdV2DirectoryProofUnavailableException unavailable && unavailable.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                return unavailable.RetryAfter ?? TimeSpan.Zero;
            if (error is HttpRequestException or TimeoutException or OperationCanceledException) return TimeSpan.Zero;
            if (error.InnerException is null) break;
        }
        return null;
    }
    private async Task WaitAsync(TimeSpan delay)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var changed = wake.Reader.ReadAsync(stop.Token).AsTask();
        var timer = Task.Delay(delay,stop.Token);
        _ = await Task.WhenAny(changed,timer).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try { await changed.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lifetime.Token.ThrowIfCancellationRequested();
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; network.StatusChanged -= ConnectivityChanged; lifetime.Cancel(); attempt?.Cancel(); }
        await worker.ConfigureAwait(false);
        attempt?.Dispose(); lifetime.Dispose();
    }
}

public sealed class DeepIdV2AccountUnavailableException : InvalidOperationException;
