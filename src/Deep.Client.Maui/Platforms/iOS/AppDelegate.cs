#if IOS
using Foundation;
using Microsoft.Maui;
using UIKit;
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
