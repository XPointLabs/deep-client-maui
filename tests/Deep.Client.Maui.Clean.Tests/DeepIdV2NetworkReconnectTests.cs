using System.Net;
using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.AccountDirectoryV2;

namespace Deep.Client.Maui.Clean.Tests;

// Scheduling and lifecycle only; delegate success is not device/transport evidence.
public sealed class DeepIdV2NetworkReconnectTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(120001)]
    public void ForegroundDeadlineCannotBeDisabledOrExceedProductionBudget(int milliseconds)
    {
        var network = new Network();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepIdV2NetworkReconnect(
            _ => Task.CompletedTask, network, null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal(0, network.Subscribers);
    }

    [Fact]
    public async Task ForegroundAttemptDeadlineCancelsItsActualTokenAndReleasesLane()
    {
        var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            Interlocked.Increment(ref calls); return Task.CompletedTask;
        }, new Network(), null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        reconnect.Resume(); await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new CancellationTokenSource();
        var foreground = reconnect.RunForegroundAsync(async token => {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }, cleanup.Token);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Background verification has no ownership of this deadline.
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground);
            Assert.Equal(1, calls);
            Assert.Equal(DeepIdV2ReconnectState.Verified, reconnect.State);
            Assert.Equal(7, await reconnect.RunForegroundAsync(_ => Task.FromResult(7)));
        } finally {
            cleanup.Cancel();
            try { await foreground; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ForegroundDeadlineStartsOnlyAfterOwningLane()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            entered.TrySetResult(); await finish.Task.WaitAsync(token);
        }, new Network(), null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var foreground = reconnect.RunForegroundAsync(token => {
            Assert.False(token.IsCancellationRequested); return Task.FromResult(9);
        });
        try {
            await Task.Delay(250);
            Assert.False(foreground.IsCompleted);
        } finally { finish.TrySetResult(); }
        Assert.Equal(9, await foreground.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ForegroundDeadlineCannotReleaseUncooperativeOwnerOrAcceptItsLateSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => Task.CompletedTask,
            new Network(), null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        reconnect.Resume(); await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        var foreground = reconnect.RunForegroundAsync(async token => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await finish.Task; return 1;
        });
        var nextRan = false;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = reconnect.RunForegroundAsync(_ => { nextRan = true; return Task.FromResult(2); });
        try {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(nextRan); Assert.False(foreground.IsCompleted);
        } finally { finish.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ForegroundDelegateRetainsCallerContextAfterWaitingForBackgroundOwner()
    {
        var network = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            entered.TrySetResult(); await finish.Task.WaitAsync(token);
        },network);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new CallerContext(); var previous = SynchronizationContext.Current;
        Task foreground;
        try {
            SynchronizationContext.SetSynchronizationContext(context);
            foreground = reconnect.RunForegroundAsync(_ => {
                Assert.Same(context,SynchronizationContext.Current); return Task.CompletedTask;
            });
        } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        finish.TrySetResult(); await foreground.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("offline")]
    [InlineData("suspend")]
    public async Task QueuedForegroundCannotRunAfterItsWindowOrAccountRevisionChanges(string invalidation)
    {
        var network = new Network(); var ran = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async _ => {
            entered.TrySetResult(); await finish.Task;
        },network);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var foreground = reconnect.RunForegroundAsync(_ => { ran=true; return Task.CompletedTask; });
        if (invalidation == "account") reconnect.AccountChanged();
        else if (invalidation == "offline") network.Set(false);
        else reconnect.Suspend();
        finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(ran);
    }

    [Fact]
    public async Task ForegroundOwnsRefreshLaneAndOfflineCancelsActualWorkAndManualWaiter()
    {
        var network = new Network(); var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            Interlocked.Increment(ref calls); return Task.CompletedTask;
        },network);
        reconnect.Resume(); await WaitFor(reconnect,DeepIdV2ReconnectState.Verified);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = reconnect.RunForegroundAsync(async token => {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan,token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = reconnect.VerifyNowAsync();
        await Task.Delay(150);
        Assert.Equal(1,calls); Assert.False(manual.IsCompleted);
        Assert.Equal(DeepIdV2ReconnectState.Verified,reconnect.State);
        network.Set(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DeepIdV2ReconnectState.Offline,reconnect.State);
    }

    [Fact]
    public async Task CancelledForegroundWaiterCannotCancelBackgroundOwnerOrRunItsDelegate()
    {
        var network = new Network(); var calls = 0; var foregroundRan = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await finish.Task.WaitAsync(token);
        },network);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = reconnect.VerifyNowAsync();
        using var stop = new CancellationTokenSource();
        var foreground = reconnect.RunForegroundAsync(_ => { foregroundRan=true; return Task.CompletedTask; },stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(foregroundRan); Assert.False(manual.IsCompleted); Assert.Equal(1,calls);
        finish.TrySetResult(); await manual.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeepIdV2ReconnectState.Verified,reconnect.State);
    }

    [Fact]
    public async Task ForegroundFailureReleasesLaneWithoutMaskingActualAuthorityRejection()
    {
        var network = new Network(); var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            if (Interlocked.Increment(ref calls) > 1) throw new CryptographicException("Test authority rejection.");
            return Task.CompletedTask;
        },network);
        reconnect.Resume(); await WaitFor(reconnect,DeepIdV2ReconnectState.Verified);
        var failure = new IOException("Test retained outcome.");
        var observed = await Assert.ThrowsAsync<IOException>(() => reconnect.RunForegroundAsync(_ => Task.FromException(failure)));
        Assert.Same(failure,observed); Assert.Equal(1,calls);
        await Assert.ThrowsAsync<CryptographicException>(() => reconnect.VerifyNowAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DeepIdV2ReconnectState.Rejected,reconnect.State);
    }

    [Fact]
    public async Task AccountMutationDrainsCancelledForegroundAndRejectsItsLateSuccess()
    {
        var network = new Network(); var mutationRan = false;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => Task.CompletedTask,network);
        reconnect.Resume(); await WaitFor(reconnect,DeepIdV2ReconnectState.Verified);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = reconnect.RunForegroundAsync(async _ => { entered.TrySetResult(); await finish.Task; return 7; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mutation = reconnect.MutateAccountAsync(_ => { mutationRan=true; return Task.CompletedTask; });
        try { await Task.Delay(100); Assert.False(mutationRan); }
        finally { finish.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        await mutation.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(mutationRan);
    }

    [Fact]
    public async Task RepeatedActivationDoesNotCancelActiveVerificationOrClearVerifiedState()
    {
        var status = new Network(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await finish.Task.WaitAsync(token);
        }, status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var verification = reconnect.VerifyNowAsync();
        reconnect.Resume(); reconnect.Resume();
        Assert.False(verification.IsCompleted); Assert.Equal(1, calls);
        finish.TrySetResult(); await verification.WaitAsync(TimeSpan.FromSeconds(5));
        reconnect.Resume();
        Assert.Equal(DeepIdV2ReconnectState.Verified, reconnect.State); Assert.Equal(1, calls);
        reconnect.Suspend(); reconnect.Suspend();
        Assert.Equal(DeepIdV2ReconnectState.Dormant, reconnect.State);
    }

    [Fact]
    public async Task ManualVerificationJoinsActivePipelineAndWaiterCancellationDoesNotCancelOwner()
    {
        var status = new Network(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await finish.Task.WaitAsync(token);
        }, status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = reconnect.VerifyNowAsync();
        using var cancelled = new CancellationTokenSource();
        var cancelledWaiter = reconnect.VerifyNowAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(joined.IsCompleted); Assert.Equal(1, calls);
        finish.TrySetResult(); await joined.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeepIdV2ReconnectState.Verified, reconnect.State);
        await Task.Delay(100); Assert.Equal(1, calls); // Joining did not queue another flight.
    }

    [Fact]
    public async Task ManualVerificationAfterSuccessRunsFreshPipelineInsteadOfReturningCachedState()
    {
        var status = new Network(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            if (Interlocked.Increment(ref calls) == 2) { entered.TrySetResult(); await finish.Task.WaitAsync(token); }
        }, status);
        reconnect.Resume(); await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        var verification = reconnect.VerifyNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(verification.IsCompleted); Assert.Equal(2, calls);
        finish.TrySetResult(); await verification.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeepIdV2ReconnectState.Verified, reconnect.State);
    }

    [Fact]
    public async Task PeriodicRefreshDoesNotInvalidatePreviouslyVerifiedUiButOfflineStillDoes()
    {
        var status = new Network(); var calls = 0;
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async token => {
            if (Interlocked.Increment(ref calls) == 2) {
                refreshed.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }, status, null, TimeSpan.FromMilliseconds(50));
        reconnect.Resume(); await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeepIdV2ReconnectState.Verified, reconnect.State);
        status.Set(false); Assert.Equal(DeepIdV2ReconnectState.Offline, reconnect.State);
    }

    [Fact]
    public async Task SuspendCancelsManualWaiterAndLatePipelineCannotCompleteItSuccessfully()
    {
        var status = new Network();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnect = new DeepIdV2NetworkReconnect(async _ => {
            entered.TrySetResult(); await finish.Task;
        }, status);
        reconnect.Resume(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try {
            var verification = reconnect.VerifyNowAsync(); reconnect.Suspend();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification.WaitAsync(TimeSpan.FromSeconds(5)));
        } finally { finish.TrySetResult(); }
        await Task.Delay(100);
        Assert.Equal(DeepIdV2ReconnectState.Dormant, reconnect.State);
    }

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
        var manual = reconnect.VerifyNowAsync();
        await Task.Delay(200); Assert.Equal(1,calls); // Explicit UI wake cannot bypass RetryAfter.
        await manual.WaitAsync(TimeSpan.FromSeconds(6));
        await WaitFor(reconnect,DeepIdV2ReconnectState.Verified); Assert.Equal(2,calls);
        var rejectedCalls = 0;
        await using var rejected = new DeepIdV2NetworkReconnect(_ => {
            Interlocked.Increment(ref rejectedCalls); throw new CryptographicException("Test floor integrity rejected.");
        },status);
        rejected.Resume(); await WaitFor(rejected,DeepIdV2ReconnectState.Rejected);
        await Task.Delay(200); Assert.Equal(1,rejectedCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task WrappedBootstrapUnavailableRetainsTypedCauseAndRetryAfter(HttpStatusCode code)
    {
        var cause = new DeepIdV2DirectoryProofUnavailableException(code, TimeSpan.FromSeconds(3));
        var wrapped = Did2NetworkIoFailure.AtStage("BootstrapAuthority", cause);
        Assert.Same(cause, wrapped.InnerException);
        var status = new Network(); var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            if (Interlocked.Increment(ref calls) == 1) throw wrapped;
            return Task.CompletedTask;
        }, status);
        reconnect.Resume(); await WaitFor(reconnect, DeepIdV2ReconnectState.Unavailable);
        var manual = reconnect.VerifyNowAsync();
        await Task.Delay(200); Assert.Equal(1, calls);
        await manual.WaitAsync(TimeSpan.FromSeconds(6));
        await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WrappedRetryableOnionDependencyReverifiesWithoutBeingPermanentlyRejected()
    {
        var cause = new ClientMailboxTransportException(ClientMailboxTransportFailure.DependencyUnavailable,
            true, "Test dependency unavailable.", new IOException("Test private transport cause."));
        var wrapped = Did2NetworkIoFailure.AtStage("ContactPublication", cause);
        var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            if (Interlocked.Increment(ref calls) == 1) throw wrapped;
            return Task.CompletedTask;
        }, new Network());
        reconnect.Resume();
        await WaitFor(reconnect, DeepIdV2ReconnectState.Unavailable);
        await WaitFor(reconnect, DeepIdV2ReconnectState.Verified);
        Assert.Equal(2, calls);
        Assert.Same(cause, wrapped.InnerException);
    }

    [Theory]
    [InlineData(ClientMailboxTransportFailure.DependencyUnavailable, false)]
    [InlineData(ClientMailboxTransportFailure.AuthorizationRejected, false)]
    [InlineData(ClientMailboxTransportFailure.ProtocolViolation, true)]
    public async Task TypedTransportDenialCannotGainRetryFromInnerHttpFailure(
        ClientMailboxTransportFailure failure, bool retryable)
    {
        var cause = new ClientMailboxTransportException(failure, retryable,
            "Test typed refusal.", new HttpRequestException("Test nested network failure."));
        var wrapped = Did2NetworkIoFailure.AtStage("ContactPublication", cause);
        var calls = 0;
        await using var reconnect = new DeepIdV2NetworkReconnect(_ => {
            Interlocked.Increment(ref calls); throw wrapped;
        }, new Network());
        reconnect.Resume();
        await WaitFor(reconnect, DeepIdV2ReconnectState.Rejected);
        await Task.Delay(200);
        Assert.Equal(1, calls);
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
    private sealed class CallerContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback,object? state) =>
            ThreadPool.QueueUserWorkItem(_ => {
                var previous = Current; SetSynchronizationContext(this);
                try { callback(state); }
                finally { SetSynchronizationContext(previous); }
            });
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
