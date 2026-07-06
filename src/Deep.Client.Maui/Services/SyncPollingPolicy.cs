using Deep.Client.Shared.Platform;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class SyncPollingPolicy(IPushNotificationService pushNotifications)
{
    public async Task<bool> IsPushDrivenSyncAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            return false;
        }

        try
        {
            return await pushNotifications.GetCachedRegistrationAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        catch
        {
            return false;
        }
    }
}
