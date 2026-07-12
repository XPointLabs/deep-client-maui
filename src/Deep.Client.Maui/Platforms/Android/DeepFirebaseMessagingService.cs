#if ANDROID
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Deep.Client.Maui.Services;
using Firebase.Messaging;

namespace Deep.Client.Maui;

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class DeepFirebaseMessagingService : FirebaseMessagingService
{
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        PushTokenBridge.Set("fcm", token);
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);

        var data = new Dictionary<string, string>(message.Data, StringComparer.Ordinal);
        try
        {
            if (!PushNotificationBackgroundHandler.TryNormalizeEnvelopeData(
                    data,
                    out var encodedEnvelope,
                    out var protocolVersion))
            {
                return;
            }

            // JobScheduler persists the encrypted envelope before this callback returns. The job
            // authenticates it after process death; no SecureStorage or network I/O occurs here.
            AndroidBackgroundSyncScheduler.SchedulePush(encodedEnvelope, protocolVersion);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Android.PushSchedule", exception);
        }
    }
}

internal static class AndroidPushNotificationPresenter
{
    private const string ChannelId = "deep-messages";
    private const string GenericTitle = "Deep";
    private const string GenericBody = "New message";

    public static void Show(Context context, string notificationId)
    {
        var manager = NotificationManagerCompat.From(context);
        EnsureChannel(context);
        var stableNotificationId = StableNotificationId(notificationId);

        var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(MainActivity.TrustedNotificationAction);
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra("notification_action", "open");
        intent.PutExtra("notification_id", notificationId);

        var pendingIntent = PendingIntent.GetActivity(
            context,
            stableNotificationId,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(GenericTitle);
        builder.SetContentText(GenericBody);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(GenericBody));
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetVisibility(NotificationCompat.VisibilitySecret);
        builder.SetAutoCancel(true);
        if (pendingIntent is not null)
        {
            builder.SetContentIntent(pendingIntent);
        }

        var publicBuilder = new NotificationCompat.Builder(context, ChannelId);
        publicBuilder.SetSmallIcon(Resource.Mipmap.appicon);
        publicBuilder.SetContentTitle(GenericTitle);
        publicBuilder.SetContentText(GenericBody);
        publicBuilder.SetVisibility(NotificationCompat.VisibilitySecret);
        var publicVersion = publicBuilder.Build();
        if (publicVersion is not null)
        {
            builder.SetPublicVersion(publicVersion);
        }

        var notification = builder.Build();
        if (notification is not null)
        {
            manager?.Notify(stableNotificationId, notification);
        }
    }

    private static int StableNotificationId(string notificationId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(notificationId));
        return BinaryPrimitives.ReadInt32LittleEndian(digest) & int.MaxValue;
    }

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "Deep messages",
            NotificationImportance.High)
        {
            Description = "Encrypted Deep push notifications"
        });
    }
}
#endif
