using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Deep.Client.Shared.Platform;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class SyncPollingPolicy(IPushNotificationService pushNotifications, ClientRuntime runtime)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly object cacheSync = new();
    private string? cachedSessionId;
    private DateTimeOffset cachedAt;
    private bool? cachedAvailability;

    public async Task<bool> IsPushDrivenSyncAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            Reset();
            return false;
        }

        var activeAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        if (activeAccount is null)
        {
            Reset();
            return false;
        }

        var sessionId = activeAccount.SessionId.Value;
        if (TryGetCached(sessionId, out var cached))
        {
            return cached;
        }

        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            activeAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
            if (activeAccount is null)
            {
                Reset();
                return false;
            }

            sessionId = activeAccount.SessionId.Value;
            if (TryGetCached(sessionId, out var refreshed))
            {
                return refreshed;
            }

            var registration = await pushNotifications.GetCachedRegistrationAsync(cancellationToken).ConfigureAwait(false);
            var available = await IsCurrentRemoteSubscriptionAsync(
                registration,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            var currentAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentAccount?.SessionId.Value, sessionId, StringComparison.Ordinal))
            {
                Reset(sessionId);
                return false;
            }

            SetCached(sessionId, available);
            return available;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            SetCached(sessionId, false);
            return false;
        }
        finally
        {
            cacheGate.Release();
        }
    }

    public void Invalidate() => Reset();

    public void Reset(string? sessionId = null)
    {
        lock (cacheSync)
        {
            if (sessionId is not null &&
                !string.Equals(cachedSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            cachedSessionId = null;
            cachedAvailability = null;
            cachedAt = default;
        }
    }

    private async Task<bool> IsCurrentRemoteSubscriptionAsync(
        PushRegistration? registration,
        string activeSessionId,
        CancellationToken cancellationToken)
    {
        if (registration is null ||
            !PushRegistrationCoordinator.TryResolvePushService(registration.Provider, out var service) ||
            pushNotifications is not IPushNotificationEncryptionKeyStore keyStore)
        {
            return false;
        }

        var state = await keyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return false;
        }

        var expectedBinding = PushNotificationCrypto.ComputeSubscriptionBinding(
            activeSessionId,
            service,
            registration.Token);
        var isCurrent = state.RemoteSubscribed &&
                        string.Equals(state.SessionBinding, activeSessionId, StringComparison.Ordinal) &&
                        string.Equals(state.SubscriptionBinding, expectedBinding, StringComparison.Ordinal) &&
                        string.Equals(state.Registration.Token, registration.Token, StringComparison.Ordinal) &&
                        string.Equals(state.Registration.Provider, registration.Provider, StringComparison.OrdinalIgnoreCase);
        if (!isCurrent)
        {
            await keyStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
        }

        return isCurrent;
    }

    private bool TryGetCached(string sessionId, out bool available)
    {
        lock (cacheSync)
        {
            if (cachedAvailability is { } cached &&
                string.Equals(cachedSessionId, sessionId, StringComparison.Ordinal) &&
                runtime.Clock.UtcNow - cachedAt < CacheTtl)
            {
                available = cached;
                return true;
            }
        }

        available = false;
        return false;
    }

    private void SetCached(string sessionId, bool available)
    {
        lock (cacheSync)
        {
            if (!available)
            {
                if (string.Equals(cachedSessionId, sessionId, StringComparison.Ordinal))
                {
                    cachedSessionId = null;
                    cachedAvailability = null;
                    cachedAt = default;
                }

                return;
            }

            cachedSessionId = sessionId;
            cachedAvailability = true;
            cachedAt = runtime.Clock.UtcNow;
        }
    }
}
