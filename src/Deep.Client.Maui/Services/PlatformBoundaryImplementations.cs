using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using System.Text.Json;
using Microsoft.Maui.Storage;

#if ANDROID
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidBitmapFactory = Android.Graphics.BitmapFactory;
#endif

namespace Deep.Client.Maui.Services;

public sealed class MauiMediaCodecService : IMediaCodecService
{
    private const int MaxPhotoDimension = 1600;
    private const int InitialPhotoQuality = 86;
    private const int MinPhotoQuality = 70;

    public async Task<MediaTranscodeResult> TranscodeAsync(MediaTranscodeRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("Source media file was not found.", request.SourcePath);
        }

#if ANDROID
        if (request.TargetContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run(() => TranscodeImageAndroid(request), cancellationToken).ConfigureAwait(false);
        }
#endif

        var extension = ContentTypeToExtension(request.TargetContentType);
        var outputPath = Path.Combine(FileSystem.CacheDirectory, $"media-{Guid.NewGuid():N}{extension}");

        await using var source = File.OpenRead(request.SourcePath);
        await using var destination = File.Create(outputPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        var size = new FileInfo(outputPath).Length;
        if (request.MaxBytes > 0 && size > request.MaxBytes)
        {
            File.Delete(outputPath);
            throw new InvalidOperationException($"Transcoded media exceeds limit {request.MaxBytes} bytes.");
        }

        return new MediaTranscodeResult(outputPath, request.TargetContentType, size);
    }

#if ANDROID
    private static MediaTranscodeResult TranscodeImageAndroid(MediaTranscodeRequest request)
    {
        var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
        AndroidBitmapFactory.DecodeFile(request.SourcePath, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            throw new InvalidOperationException("Selected image could not be decoded.");
        }

        var options = new AndroidBitmapFactory.Options
        {
            InSampleSize = CalculateSampleSize(bounds.OutWidth, bounds.OutHeight, MaxPhotoDimension),
            InPreferredConfig = AndroidBitmap.Config.Argb8888
        };

        using var decoded = AndroidBitmapFactory.DecodeFile(request.SourcePath, options)
            ?? throw new InvalidOperationException("Selected image could not be decoded.");
        AndroidBitmap? scaled = null;
        var bitmap = decoded;
        var longestSide = Math.Max(decoded.Width, decoded.Height);
        if (longestSide > MaxPhotoDimension)
        {
            var scale = MaxPhotoDimension / (double)longestSide;
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            scaled = AndroidBitmap.CreateScaledBitmap(decoded, width, height, true);
            bitmap = scaled;
        }

        try
        {
            var jpegFormat = AndroidBitmap.CompressFormat.Jpeg
                ?? throw new InvalidOperationException("Android JPEG encoder is not available.");
            for (var quality = InitialPhotoQuality; quality >= MinPhotoQuality; quality -= 8)
            {
                var outputPath = Path.Combine(FileSystem.CacheDirectory, $"media-{Guid.NewGuid():N}.jpg");
                using (var output = File.Create(outputPath))
                {
                    if (!bitmap.Compress(jpegFormat, quality, output))
                    {
                        throw new InvalidOperationException("Selected image could not be compressed.");
                    }
                }

                var size = new FileInfo(outputPath).Length;
                if (request.MaxBytes <= 0 || size <= request.MaxBytes)
                {
                    return new MediaTranscodeResult(outputPath, "image/jpeg", size, bitmap.Width, bitmap.Height);
                }

                File.Delete(outputPath);
            }
        }
        finally
        {
            scaled?.Dispose();
        }

        throw new InvalidOperationException($"Compressed image exceeds limit {request.MaxBytes} bytes.");
    }

    private static int CalculateSampleSize(int width, int height, int maxDimension)
    {
        var sampleSize = 1;
        while ((width / (sampleSize * 2)) >= maxDimension || (height / (sampleSize * 2)) >= maxDimension)
        {
            sampleSize *= 2;
        }

        return sampleSize;
    }
#endif

    private static string ContentTypeToExtension(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "video/mp4" => ".mp4",
            "audio/mpeg" => ".mp3",
            _ => ".bin"
        };
    }
}

public sealed class MauiPermissionsService : IPermissionsService
{
    public async Task<PermissionState> GetAsync(PermissionKind permission, CancellationToken cancellationToken = default)
    {
        return permission switch
        {
            PermissionKind.Camera => Convert(await Permissions.CheckStatusAsync<Permissions.Camera>().ConfigureAwait(false)),
            PermissionKind.Microphone => Convert(await Permissions.CheckStatusAsync<Permissions.Microphone>().ConfigureAwait(false)),
            PermissionKind.Photos => Convert(await Permissions.CheckStatusAsync<Permissions.Photos>().ConfigureAwait(false)),
            PermissionKind.Notifications => Convert(await Permissions.CheckStatusAsync<Permissions.PostNotifications>().ConfigureAwait(false)),
            PermissionKind.Contacts => Convert(await Permissions.CheckStatusAsync<Permissions.ContactsRead>().ConfigureAwait(false)),
            _ => PermissionState.Unknown
        };
    }

    public async Task<PermissionState> RequestAsync(PermissionKind permission, CancellationToken cancellationToken = default)
    {
        return permission switch
        {
            PermissionKind.Camera => Convert(await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(false)),
            PermissionKind.Microphone => Convert(await Permissions.RequestAsync<Permissions.Microphone>().ConfigureAwait(false)),
            PermissionKind.Photos => Convert(await Permissions.RequestAsync<Permissions.Photos>().ConfigureAwait(false)),
            PermissionKind.Notifications => Convert(await Permissions.RequestAsync<Permissions.PostNotifications>().ConfigureAwait(false)),
            PermissionKind.Contacts => Convert(await Permissions.RequestAsync<Permissions.ContactsRead>().ConfigureAwait(false)),
            _ => PermissionState.Unknown
        };
    }

    private static PermissionState Convert(PermissionStatus status)
    {
        return status switch
        {
            PermissionStatus.Granted => PermissionState.Granted,
            PermissionStatus.Denied => PermissionState.Denied,
            PermissionStatus.Restricted => PermissionState.Restricted,
            _ => PermissionState.Unknown
        };
    }
}

public sealed class MauiBackgroundTaskService : IBackgroundTaskService
{
    private const string NextSyncAtKey = "bg.next-sync-at";
    private const string RetryCountKey = "bg.retry-count";

    public async Task ScheduleSyncAsync(TimeSpan minimumDelay, CancellationToken cancellationToken = default)
    {
        var next = DateTimeOffset.UtcNow.Add(minimumDelay);
        Preferences.Default.Set(NextSyncAtKey, next.ToString("O"));

        _ = Task.Run(async () =>
        {
            try
            {
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                BackgroundSyncBridge.PublishScheduledSync();
                Preferences.Default.Set(RetryCountKey, 0);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                var retries = Preferences.Default.Get(RetryCountKey, 0) + 1;
                Preferences.Default.Set(RetryCountKey, retries);
                var retryDelay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, retries)));
                await ScheduleSyncAsync(retryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }, cancellationToken);

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

public static class BackgroundSyncBridge
{
    public static event Action? SyncScheduled;

    public static void PublishScheduledSync() => SyncScheduled?.Invoke();
}

public sealed class MauiShareExtensionBridge : IShareExtensionBridge
{
    private static readonly string ShareQueuePath = Path.Combine(FileSystem.AppDataDirectory, "pending-shares.json");

    public async Task<IReadOnlyList<SharePayload>> DrainPendingSharesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ShareQueuePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(ShareQueuePath, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<List<SharePayload>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        File.Delete(ShareQueuePath);
        return payload;
    }

    public static async Task EnqueueAsync(SharePayload payload, CancellationToken cancellationToken = default)
    {
        var queue = new List<SharePayload>();
        if (File.Exists(ShareQueuePath))
        {
            var existing = await File.ReadAllTextAsync(ShareQueuePath, cancellationToken).ConfigureAwait(false);
            queue = JsonSerializer.Deserialize<List<SharePayload>>(existing, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }

        queue.Add(payload);
        var json = JsonSerializer.Serialize(queue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(ShareQueuePath, json, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MauiRealtimeCallService : ICallService
{
    private readonly RealtimeCallService _realtime;

    public MauiRealtimeCallService(RealtimeCallService realtime)
    {
        _realtime = realtime;
    }

    public bool IsAvailable => true;

    public Task<CallSessionSnapshot> StartAsync(
        SessionId local,
        SessionId remote,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        return _realtime.StartOutgoingAsync(local, remote, conversationId, cancellationToken);
    }

    public Task<IReadOnlyList<CallSessionSnapshot>> PollAsync(SessionId local, CancellationToken cancellationToken = default)
    {
        return _realtime.PollAsync(local, cancellationToken);
    }

    public Task<CallSessionSnapshot?> AcceptAsync(string callId, SessionId local, CancellationToken cancellationToken = default)
    {
        return _realtime.AcceptIncomingAsync(callId, local, cancellationToken);
    }

    public Task<CallSessionSnapshot?> EndAsync(
        string callId,
        SessionId local,
        string reason = "local-hangup",
        CancellationToken cancellationToken = default)
    {
        return _realtime.EndAsync(callId, local, reason, cancellationToken);
    }

    public Task<CallSessionSnapshot?> ApplyNetworkSampleAsync(
        string callId,
        SessionId local,
        CallNetworkSample sample,
        CancellationToken cancellationToken = default)
    {
        return _realtime.ApplyNetworkSampleAsync(callId, local, sample, cancellationToken);
    }
}

public sealed class MauiNotificationScheduler : INotificationScheduler
{
    private static readonly string PendingNotificationsPath = Path.Combine(FileSystem.AppDataDirectory, "pending-notifications.json");

    public async Task ScheduleAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        queue.RemoveAll(item => string.Equals(item.Identifier, request.Identifier, StringComparison.OrdinalIgnoreCase));
        queue.Add(request);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);

        NotificationActionBridge.Publish(new NotificationAction(
            "notification.open",
            request.ConversationId.Value,
            request.Identifier,
            DateTimeOffset.UtcNow));
    }

    public async Task ClearConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        queue.RemoveAll(item => item.ConversationId == conversationId);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<NotificationRequest>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PendingNotificationsPath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(PendingNotificationsPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<NotificationRequest>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    private static async Task SaveAsync(IReadOnlyList<NotificationRequest> queue, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(queue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(PendingNotificationsPath, json, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record NotificationAction(string ActionId, string ConversationId, string NotificationId, DateTimeOffset CreatedAt);

public static class NotificationActionBridge
{
    private static readonly object Gate = new();
    private static readonly Queue<NotificationAction> Queue = new();

    public static void Publish(NotificationAction action)
    {
        lock (Gate)
        {
            Queue.Enqueue(action);
        }
    }

    public static IReadOnlyList<NotificationAction> Drain()
    {
        var result = new List<NotificationAction>();
        lock (Gate)
        {
            while (Queue.Count > 0)
            {
                result.Add(Queue.Dequeue());
            }
        }

        return result;
    }
}
