using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public interface IRegularBackgroundSyncScheduler
{
    void EnsureScheduled();

    void Cancel();
}

public sealed class MauiRegularBackgroundSyncScheduler : IRegularBackgroundSyncScheduler
{
    public void EnsureScheduled()
    {
#if ANDROID
        AndroidBackgroundSyncScheduler.ScheduleRegularRetry();
#endif
    }

    public void Cancel()
    {
#if ANDROID
        AndroidBackgroundSyncScheduler.CancelRegularRetry();
#endif
    }
}

public sealed class BackgroundSyncSchedulingCoordinator(
    ClientRuntime runtime,
    IRegularBackgroundSyncScheduler scheduler)
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private string? scheduledSessionId;

    public async Task EnsureScheduledForActiveAccountAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                CancelCore();
                return;
            }

            var sessionId = account.SessionId.Value;
            if (string.Equals(scheduledSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            scheduler.EnsureScheduled();
            scheduledSessionId = sessionId;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelCore();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void CancelCore()
    {
        if (scheduledSessionId is null)
        {
            return;
        }

        scheduler.Cancel();
        scheduledSessionId = null;
    }
}
