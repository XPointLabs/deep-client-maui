#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Deep.Client.Maui.Services;
using Firebase.Messaging;

namespace Deep.Client.Maui;

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class DeepFirebaseMessagingService : FirebaseMessagingService
{
    private const string ChannelId = "deep-messages";

    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        PushTokenBridge.Set("fcm", token);
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        BackgroundSyncBridge.PublishScheduledSync();

        var data = message.Data;
        var notification = message.GetNotification();
        var title = notification?.Title ?? Get(data, "title") ?? "Deep";
        var body = notification?.Body ?? Get(data, "body") ?? "Новое сообщение";
        var conversationId = Get(data, "conversation_id") ?? string.Empty;
        var notificationId = Get(data, "notification_id") ?? message.MessageId ?? Guid.NewGuid().ToString("N");

        ShowNotification(title, body, conversationId, notificationId);
    }

    private void ShowNotification(string title, string body, string conversationId, string notificationId)
    {
        var manager = NotificationManagerCompat.From(this);
        EnsureChannel();

        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra("notification_action", "open");
        intent.PutExtra("conversation_id", conversationId);
        intent.PutExtra("notification_id", notificationId);

        var pendingIntent = PendingIntent.GetActivity(
            this,
            notificationId.GetHashCode(StringComparison.Ordinal),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(body);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(body));
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetAutoCancel(true);
        builder.SetContentIntent(pendingIntent);

        var notification = builder.Build();
        if (notification is not null)
        {
            manager?.Notify(notificationId.GetHashCode(StringComparison.Ordinal), notification);
        }
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "Сообщения Deep",
            NotificationImportance.High)
        {
            Description = "Новые личные и групповые сообщения"
        });
    }

    private static string? Get(IDictionary<string, string> data, string key) =>
        data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
#endif
