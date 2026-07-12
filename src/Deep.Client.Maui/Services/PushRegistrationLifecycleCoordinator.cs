using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public sealed class PushRegistrationLifecycleCoordinator(
    ClientRuntime runtime,
    IPushRegistrationCoordinator pushRegistration,
    SyncPollingPolicy pollingPolicy)
{
    private readonly object registrationSync = new();
    private string? inFlightSessionId;
    private Task<PushRegistration?>? inFlightRegistration;

    public async Task<PushRegistration?> EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            Reset();
            return null;
        }

        var sessionId = account.SessionId.Value;
        Task<PushRegistration?> registration;
        lock (registrationSync)
        {
            if (inFlightRegistration is { IsCompleted: false } current &&
                string.Equals(inFlightSessionId, sessionId, StringComparison.Ordinal))
            {
                registration = current;
            }
            else
            {
                registration = RegisterForAccountAsync(sessionId);
                inFlightSessionId = sessionId;
                inFlightRegistration = registration;
            }
        }

        return await registration.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Reset(string? sessionId = null)
    {
        lock (registrationSync)
        {
            if (sessionId is not null &&
                !string.Equals(inFlightSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            inFlightSessionId = null;
            inFlightRegistration = null;
        }

        pollingPolicy.Reset(sessionId);
    }

    private async Task<PushRegistration?> RegisterForAccountAsync(string sessionId)
    {
        await Task.Yield();
        try
        {
            var account = await runtime.Accounts.GetActiveAccountAsync(CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(account?.SessionId.Value, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            return await pushRegistration.RegisterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            pollingPolicy.Reset(sessionId);
            lock (registrationSync)
            {
                if (string.Equals(inFlightSessionId, sessionId, StringComparison.Ordinal))
                {
                    inFlightSessionId = null;
                    inFlightRegistration = null;
                }
            }
        }
    }
}
