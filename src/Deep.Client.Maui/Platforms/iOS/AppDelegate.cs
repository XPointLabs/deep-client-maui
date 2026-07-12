#if IOS
using Foundation;
using Microsoft.Maui;
using UIKit;
using UserNotifications;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;

namespace Deep.Client.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
    {
        var token = deviceToken.ToArray();
        var hex = Convert.ToHexString(token).ToLowerInvariant();
        PushTokenBridge.Set("apns", hex);
    }

    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
    {
        CrashDiagnostics.LogInfo("Push", $"APNs registration failed: {error.LocalizedDescription}");
    }

    [Export("application:didReceiveRemoteNotification:fetchCompletionHandler:")]
    public void ReceivedRemoteNotification(
        UIApplication application,
        NSDictionary userInfo,
        Action<UIBackgroundFetchResult> completionHandler)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var encryptedPayload = userInfo[NSObject.FromObject("enc_payload")]?.ToString();
        var version = userInfo[NSObject.FromObject("spns")]?.ToString();
        if (encryptedPayload is not null)
        {
            data["enc_payload"] = encryptedPayload;
        }

        if (version is not null)
        {
            data["spns"] = version;
        }

        _ = HandleRemoteNotificationAsync(data, completionHandler);
    }

    private static async Task HandleRemoteNotificationAsync(
        IReadOnlyDictionary<string, string> data,
        Action<UIBackgroundFetchResult> completionHandler)
    {
        try
        {
            var authentication = await PushNotificationBackgroundHandler
                .AuthenticateAsync(data, BackgroundSyncBridge.PublishScheduledSync)
                .ConfigureAwait(false);
            if (authentication.Status == PushAuthenticationStatus.Rejected ||
                authentication.Status == PushAuthenticationStatus.Replay && !BackgroundSyncBridge.HasPendingSync())
            {
                completionHandler(UIBackgroundFetchResult.NoData);
                return;
            }

            await MauiBackgroundSyncRunner.TrySynchronizeAsync().ConfigureAwait(false);
            var content = new UNMutableNotificationContent
            {
                Title = "Deep",
                Body = "New message",
                Sound = UNNotificationSound.Default
            };
            var request = UNNotificationRequest.FromIdentifier(
                Guid.NewGuid().ToString("N"),
                content,
                UNTimeIntervalNotificationTrigger.CreateTrigger(0.1, false));
            await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request).ConfigureAwait(false);
            completionHandler(UIBackgroundFetchResult.NewData);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("iOS.PushNotification", exception);
            completionHandler(UIBackgroundFetchResult.Failed);
        }
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (url is null)
        {
            return base.OpenUrl(application, url, options);
        }

        MauiShareExtensionBridge.EnqueueInBackground(new SharePayload(url.AbsoluteString, Array.Empty<string>()));
        return true;
    }

    public override bool ContinueUserActivity(UIApplication application, NSUserActivity userActivity, UIApplicationRestorationHandler completionHandler)
    {
        if (!string.IsNullOrWhiteSpace(userActivity.ActivityType) && userActivity.UserInfo is not null)
        {
            var conversation = userActivity.UserInfo[NSObject.FromObject("conversation_id")]?.ToString();
            var notificationId = userActivity.UserInfo[NSObject.FromObject("notification_id")]?.ToString() ?? Guid.NewGuid().ToString("N");

            if (!string.IsNullOrWhiteSpace(conversation))
            {
                NotificationActionBridge.Publish(new NotificationAction(userActivity.ActivityType, conversation, notificationId, DateTimeOffset.UtcNow));
                return true;
            }
        }

        return base.ContinueUserActivity(application, userActivity, completionHandler);
    }
}
#endif
