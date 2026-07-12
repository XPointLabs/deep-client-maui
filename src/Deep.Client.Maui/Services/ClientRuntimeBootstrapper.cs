using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public enum ClientRuntimeBootstrapState
{
    NotStarted,
    Initializing,
    Ready,
    Failed,
    Cancelled
}

public sealed class ClientRuntimeBootstrapper : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task<ClientRuntime>> runtimeFactory;
    private Task<ClientRuntime>? initialization;
    private CancellationTokenSource? cancellation;
    private ClientRuntime? runtime;
    private Exception? error;
    private ClientRuntimeBootstrapState state;
    private bool disposed;

    public ClientRuntimeBootstrapper(Func<ClientRuntime> runtimeFactory)
        : this(_ => Task.FromResult(runtimeFactory()))
    {
        ArgumentNullException.ThrowIfNull(runtimeFactory);
    }

    public ClientRuntimeBootstrapper(Func<CancellationToken, Task<ClientRuntime>> runtimeFactory)
    {
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public event EventHandler? StateChanged;

    public ClientRuntimeBootstrapState State
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    public Exception? Error
    {
        get
        {
            lock (sync)
            {
                return error;
            }
        }
    }

    public bool IsRetryable => State is ClientRuntimeBootstrapState.Failed or ClientRuntimeBootstrapState.Cancelled;

    public Task<ClientRuntime> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task<ClientRuntime> attempt;
        CancellationToken attemptToken;
        TaskCompletionSource<ClientRuntime> completion;

        lock (sync)
        {
            ThrowIfDisposedLocked();

            if (runtime is not null)
            {
                return Task.FromResult(runtime).WaitAsync(cancellationToken);
            }

            if (initialization is not null && !initialization.IsCompleted)
            {
                return initialization.WaitAsync(cancellationToken);
            }

            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            attemptToken = cancellation.Token;
            completion = new TaskCompletionSource<ClientRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
            initialization = completion.Task;
            attempt = completion.Task;
            SetStateLocked(ClientRuntimeBootstrapState.Initializing, null);
        }

        PublishStateChanged();
        _ = StartAttemptAsync(attemptToken, completion);
        return attempt.WaitAsync(cancellationToken);
    }

    public Task<ClientRuntime> RetryAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(cancellationToken);

    public ClientRuntime GetRequiredRuntime()
    {
        lock (sync)
        {
            return runtime
                ?? throw new InvalidOperationException("Client runtime has not completed initialization.");
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? toCancel;
        lock (sync)
        {
            toCancel = cancellation;
        }

        toCancel?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task<ClientRuntime>? attempt;
        ClientRuntime? readyRuntime;
        CancellationTokenSource? toCancel;

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toCancel = cancellation;
            attempt = initialization;
            readyRuntime = runtime;
            runtime = null;
        }

        toCancel?.Cancel();

        if (attempt is not null)
        {
            try
            {
                await attempt.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposal observes an in-flight startup attempt without masking its result.
            }
        }

        DisposeRuntime(readyRuntime);
    }

    private async Task StartAttemptAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource<ClientRuntime> completion)
    {
        ClientRuntime? initialized = null;
        try
        {
            var createdRuntime = await Task.Run(
                () => runtimeFactory(cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            initialized = createdRuntime;
            cancellationToken.ThrowIfCancellationRequested();

            var publishRuntime = false;
            lock (sync)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    runtime = createdRuntime;
                    initialized = null;
                    error = null;
                    SetStateLocked(ClientRuntimeBootstrapState.Ready, null);
                    publishRuntime = true;
                }
                else
                {
                    SetStateLocked(ClientRuntimeBootstrapState.Cancelled, null);
                }
            }

            if (publishRuntime)
            {
                PublishStateChanged();
                completion.TrySetResult(createdRuntime);
                return;
            }

            DisposeRuntime(initialized);
            completion.TrySetCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeRuntime(initialized);
            lock (sync)
            {
                SetStateLocked(ClientRuntimeBootstrapState.Cancelled, null);
            }

            PublishStateChanged();
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            DisposeRuntime(initialized);
            lock (sync)
            {
                SetStateLocked(ClientRuntimeBootstrapState.Failed, ex);
            }

            PublishStateChanged();
            completion.TrySetException(ex);
        }
    }

    private void SetStateLocked(ClientRuntimeBootstrapState nextState, Exception? nextError)
    {
        state = nextState;
        error = nextError;
    }

    private void PublishStateChanged()
    {
        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A UI observer must not fault the startup task.
        }
    }

    private static void DisposeRuntime(ClientRuntime? value)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception)
        {
            // Best-effort cleanup must not hide the startup result.
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(ClientRuntimeBootstrapper));
        }
    }
}
