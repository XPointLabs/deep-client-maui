using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public sealed class MauiAccountLogoutCoordinator(
    ClientRuntime runtime,
    IDeepAccountRuntimeAccessor deepAccountRuntime,
    IPushRegistrationCoordinator pushRegistration,
    ChatOpenUiCache chatOpenUiCache,
    PushRegistrationLifecycleCoordinator pushRegistrationLifecycle,
    SyncPollingPolicy pollingPolicy,
    BackgroundSyncSchedulingCoordinator backgroundSyncScheduling) : IAccountLogoutCoordinator
{
    private static readonly TimeSpan PushUnregisterTimeout = TimeSpan.FromSeconds(5);

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Once logout starts it owns its lifetime. Caller cancellation must not leave a
        // half-purged account after push unsubscription or the database commit.
        await TryUnregisterPushAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await runtime.Accounts.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Legacy cleanup is transitional and must never keep the authoritative
            // clean-break Deep account on the device.
            CrashDiagnostics.LogException("MauiAccountLogoutCoordinator.LegacySignOut", exception);
        }
        await deepAccountRuntime.ResetLocalStateAsync(CancellationToken.None).ConfigureAwait(false);

        chatOpenUiCache.Clear();
        TryCleanup("ResetPushRegistration", () => pushRegistrationLifecycle.Reset());
        TryCleanup("ResetPollingPolicy", () => pollingPolicy.Reset());
        await TryCleanupAsync(
            "ResetBackgroundSync",
            () => backgroundSyncScheduling.ResetAsync(CancellationToken.None)).ConfigureAwait(false);
        await MauiAccountArtifactPurger.PurgeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TryUnregisterPushAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PushUnregisterTimeout);
        try
        {
            await pushRegistration.UnregisterAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CrashDiagnostics.LogInfo("Push", "Remote push unregistration timed out during local logout.");
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("MauiAccountLogoutCoordinator.UnregisterPush", exception);
        }
    }

    private static void TryCleanup(string operation, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException($"MauiAccountLogoutCoordinator.{operation}", exception);
        }
    }

    private static async Task TryCleanupAsync(string operation, Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException($"MauiAccountLogoutCoordinator.{operation}", exception);
        }
    }
}

internal static class MauiAccountArtifactPurger
{
    public static async Task PurgeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await TryAsync(
            "AttachmentCache",
            () => AttachmentOpenService.PurgeCacheAsync(cancellationToken)).ConfigureAwait(false);
        Try("AttachmentScope", AttachmentOpenService.ClearAccountScope);
        Try("ContactAvatars", ContactAvatarStore.PurgeAll);
        Try("ShareIngress", MauiShareExtensionBridge.PurgePending);
        Try("NotificationActions", NotificationActionBridge.Purge);
        await TryAsync(
            "ScheduledNotifications",
            () => MauiNotificationScheduler.PurgeAllAsync(cancellationToken)).ConfigureAwait(false);
        Try("BackgroundSync", MauiBackgroundTaskService.ClearScheduledSync);
        Try("PushToken", PushTokenBridge.Clear);
        Try("PushReplay", MauiPushNotificationReplayStore.Clear);
    }

    private static void Try(string operation, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException($"MauiAccountArtifactPurger.{operation}", exception);
        }
    }

    private static async Task TryAsync(string operation, Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException($"MauiAccountArtifactPurger.{operation}", exception);
        }
    }
}
