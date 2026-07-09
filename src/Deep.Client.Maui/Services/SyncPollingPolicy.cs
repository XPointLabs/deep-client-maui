using Deep.Client.Shared.Platform;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class SyncPollingPolicy(IPushNotificationService pushNotifications)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private DateTimeOffset cachedAt;
    private bool? cachedAvailability;

    public async Task<bool> IsPushDrivenSyncAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            Invalidate();
            return false;
        }

        if (cachedAvailability is { } cached && DateTimeOffset.UtcNow - cachedAt < CacheTtl)
        {
            return cached;
        }

        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedAvailability is { } refreshed && DateTimeOffset.UtcNow - cachedAt < CacheTtl)
            {
                return refreshed;
            }

            var available = await pushNotifications.GetCachedRegistrationAsync(cancellationToken).ConfigureAwait(false) is not null;
            cachedAvailability = available;
            cachedAt = DateTimeOffset.UtcNow;
            return available;
        }
        catch
        {
            cachedAvailability = false;
            cachedAt = DateTimeOffset.UtcNow;
            return false;
        }
        finally
        {
            cacheGate.Release();
        }
    }

    public void Invalidate()
    {
        cachedAvailability = null;
        cachedAt = default;
    }
}
