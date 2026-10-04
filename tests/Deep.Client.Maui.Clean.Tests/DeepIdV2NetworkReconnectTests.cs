using System.Net;
using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services.AccountDirectoryV2;

namespace Deep.Client.Maui.Clean.Tests;

// Scheduling and lifecycle only; delegate success is not device/transport evidence.
public sealed class DeepIdV2NetworkReconnectTests
{
    [Fact]
    public async Task ConfirmedAccountMutationWaitsForCancelledPipelineToDrain()
    {
        var status = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var mutationRan = false;
        await using var reconnect = new DeepIdV2NetworkReconnect(async _ => {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await finish.Task; }
        },status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mutation = reconnect.MutateAccountAsync(_ => { mutationRan=true; return Task.CompletedTask; });
        await Task.Delay(100); Assert.False(mutationRan);
        reconnect.Suspend(); finish.TrySetResult(); await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(mutationRan); Assert.Equal(DeepIdV2ReconnectState.Dormant,reconnect.State);
        Assert.Equal(1,calls); // No cancelled/late success or hidden-window retry.
    }

    [Fact]
    public async Task OfflineCancelsWholeAttemptAndReturningNetworkRunsFreshPipeline()
    {
        var status = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximum = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            var current = Interlocked.Increment(ref active); maximum = Math.Max(maximum,current);
            try {
                if (Interlocked.Increment(ref calls) == 1) {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan,token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
            } finally { Interlocked.Decrement(ref active); }
        },status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        status.Set(false); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeepIdV2ReconnectState.Offline,reconnect.State);
        status.Set(true); await WaitFor(reconnect,DeepIdV2ReconnectState.Verified);
        Assert.Equal(2,calls); Assert.Equal(1,maximum);
        reconnect.Suspend(); Assert.Equal(DeepIdV2ReconnectState.Dormant,reconnect.State);
        reconnect.Resume(); await WaitFor(reconnect,DeepIdV2ReconnectState.Verified);
        Assert.Equal(3,calls);
    }

    [Fact]
    public async Task ConnectivityReturningInsideCancellationCannotScheduleDuplicatePipeline()
    {
        var status = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Deliberately synchronous completion: cancellation can resume the
        // worker inside Invalidate's reentrant lock before that method returns.
        var cancelled = new TaskCompletionSource();
        var calls = 0;
        CancellationTokenRegistration registration = default;
        await using var reconnect = new DeepIdV2NetworkReconnect(token => {
            if (Interlocked.Increment(ref calls) != 1) return Task.CompletedTask;
            registration = token.Register(() => { status.Set(true); cancelled.TrySetCanceled(token); });
            entered.TrySetResult();
            return cancelled.Task;
        }, status);
        try
        {
            reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            status.Set(false);
            await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
            Assert.Equal(2, calls);
        }
        finally { registration.Dispose(); }
    }

    [Fact]
    public async Task Typed503RetriesButWakeCannotBypassRetryAfterAndCorruptFloorDoesNotRetry()
    {
        var status = new Network(); var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            if (Interlocked.Increment(ref calls) == 1) throw new DeepIdV2DirectoryProofUnavailableException(HttpStatusCode.ServiceUnavailable,TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        },status);
        reconnect.Resume(); await WaitFor(reconnect,DeepIdV2ReconnectState.Unavailable);
        for (var i = 0; i < 10; i++) reconnect.AccountChanged();
        await Task.Delay(200); Assert.Equal(1,calls);
        await WaitFor(reconnect,DeepIdV2ReconnectState.Verified); Assert.Equal(2,calls);
        var rejectedCalls = 0;
        await using var rejected = new DeepIdV2NetworkReconnect(_ => {
            Interlocked.Increment(ref rejectedCalls); throw new CryptographicException("Test floor integrity rejected.");
        },status);
        rejected.Resume(); await WaitFor(rejected,DeepIdV2ReconnectState.Rejected);
        await Task.Delay(200); Assert.Equal(1,rejectedCalls);
    }

    [Fact]
    public async Task StaleSuccessAfterSuspendCannotBecomeVerifiedAndDisposeUnsubscribes()
    {
        var status = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new DeepIdV2NetworkReconnect(async _ => { entered.TrySetResult(); await finish.Task; },status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reconnect.Suspend(); finish.TrySetResult(); await Task.Delay(100);
        Assert.Equal(DeepIdV2ReconnectState.Dormant,reconnect.State);
        await reconnect.DisposeAsync(); Assert.Equal(0,status.Subscribers);
        status.Set(true); Assert.Equal(DeepIdV2ReconnectState.Dormant,reconnect.State);
    }

    private static async Task WaitFor(DeepIdV2NetworkReconnect reconnect,DeepIdV2ReconnectState state)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (reconnect.State != state) await Task.Delay(10,deadline.Token);
    }
    private sealed class Network : INetworkStatusService
    {
        private EventHandler? changed;
        public bool IsConnected { get; private set; } = true;
        public string ConnectionLabel => "test";
        public int Subscribers { get; private set; }
        public event EventHandler? StatusChanged { add { changed += value; Subscribers++; } remove { changed -= value; Subscribers--; } }
        internal void Set(bool connected) { IsConnected = connected; changed?.Invoke(this,EventArgs.Empty); }
    }
}
