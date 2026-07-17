using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Microsoft.Maui;

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
#elif WINDOWS
        if (request.TargetContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return await TranscodeImageWindowsAsync(request, cancellationToken).ConfigureAwait(false);
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
    internal static MediaTranscodeResult TranscodeImageAndroid(MediaTranscodeRequest request)
    {
        var transcoded = AndroidImageTranscoder.TranscodeToMetadataFreeJpeg(
            request.SourcePath,
            FileSystem.CacheDirectory,
            MaxPhotoDimension,
            InitialPhotoQuality,
            MinPhotoQuality,
            qualityStep: 8,
            request.MaxBytes);
        return new MediaTranscodeResult(
            transcoded.OutputPath,
            "image/jpeg",
            transcoded.Size,
            transcoded.Width,
            transcoded.Height);
    }
#endif

#if WINDOWS
    private static async Task<MediaTranscodeResult> TranscodeImageWindowsAsync(
        MediaTranscodeRequest request,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(request.SourcePath);
        using var randomAccessInput = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(source);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessInput);
        cancellationToken.ThrowIfCancellationRequested();

        var codecId = decoder.DecoderInformation.CodecId;
        if (codecId != Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId
            && codecId != Windows.Graphics.Imaging.BitmapDecoder.PngDecoderId
            && codecId != Windows.Graphics.Imaging.BitmapDecoder.WebpDecoderId)
        {
            throw new InvalidOperationException("Selected image is not a supported JPEG, PNG, or WebP file.");
        }

        var width = checked((int)decoder.OrientedPixelWidth);
        var height = checked((int)decoder.OrientedPixelHeight);
        if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384 || (long)width * height > 64_000_000)
        {
            throw new InvalidOperationException("Selected image dimensions exceed the attachment limits.");
        }

        var scale = Math.Min(1d, MaxPhotoDimension / (double)Math.Max(width, height));
        var targetWidth = checked((uint)Math.Max(1, (int)Math.Round(width * scale)));
        var targetHeight = checked((uint)Math.Max(1, (int)Math.Round(height * scale)));
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = targetWidth,
            ScaledHeight = targetHeight,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        cancellationToken.ThrowIfCancellationRequested();

        for (var quality = InitialPhotoQuality; quality >= MinPhotoQuality; quality -= 8)
        {
            using var encodedStream = new MemoryStream();
            using var randomAccessOutput = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(encodedStream);
            var encoderOptions = new Windows.Graphics.Imaging.BitmapPropertySet
            {
                ["ImageQuality"] = new Windows.Graphics.Imaging.BitmapTypedValue(
                    quality / 100f,
                    Windows.Foundation.PropertyType.Single)
            };
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
                randomAccessOutput,
                encoderOptions);
            encoder.IsThumbnailGenerated = false;
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = encodedStream.ToArray();
            if (request.MaxBytes > 0 && bytes.LongLength > request.MaxBytes)
            {
                continue;
            }

            var outputPath = Path.Combine(FileSystem.CacheDirectory, $"media-{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);
            return new MediaTranscodeResult(
                outputPath,
                "image/jpeg",
                bytes.LongLength,
                bitmap.PixelWidth,
                bitmap.PixelHeight);
        }

        throw new InvalidOperationException($"Compressed image exceeds limit {request.MaxBytes} bytes.");
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

    public Task ScheduleSyncAsync(TimeSpan minimumDelay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (minimumDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDelay));
        }

        var next = DateTimeOffset.UtcNow.Add(minimumDelay);
        Preferences.Default.Set(NextSyncAtKey, next.ToString("O"));
#if ANDROID
        AndroidBackgroundSyncScheduler.Schedule(minimumDelay);
#endif
        return Task.CompletedTask;
    }

    public static void TryPublishForegroundCatchUp()
    {
        var raw = Preferences.Default.Get(NextSyncAtKey, string.Empty);
        if (!DateTimeOffset.TryParse(raw, out var next) || next > DateTimeOffset.UtcNow)
        {
            return;
        }

        Preferences.Default.Remove(NextSyncAtKey);
        BackgroundSyncBridge.PublishScheduledSync();
    }

    internal static void ClearScheduledSync()
    {
        Preferences.Default.Remove(NextSyncAtKey);
        BackgroundSyncBridge.Clear();
    }
}

public static class BackgroundSyncBridge
{
    private const string PendingSyncKey = "bg.sync-pending";
    public static event Action? SyncScheduled;
    public static event Action? SyncCompleted;

    public static void PublishScheduledSync()
    {
        Preferences.Default.Set(PendingSyncKey, true);
        SyncScheduled?.Invoke();
    }

    public static bool HasPendingSync() => Preferences.Default.Get(PendingSyncKey, false);

    public static void PublishCompletedSync() => SyncCompleted?.Invoke();

    public static void MarkHandled() => Preferences.Default.Remove(PendingSyncKey);

    internal static void Clear() => Preferences.Default.Remove(PendingSyncKey);
}

public static class MauiBackgroundSyncRunner
{
    public sealed record Result(bool Succeeded, InboxSyncResult Inbox)
    {
        public static Result Unavailable { get; } = new(false, new InboxSyncResult(0, 0, 0, 0));
    }

    public static async Task<bool> TrySynchronizeAsync(CancellationToken cancellationToken = default)
        => (await SynchronizeAsync(services: null, cancellationToken).ConfigureAwait(false)).Succeeded;

    public static async Task<bool> TrySynchronizeAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
        => (await SynchronizeAsync(services, cancellationToken).ConfigureAwait(false)).Succeeded;

    public static Task<Result> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        SynchronizeAsync(services: null, markBackgroundWorkHandled: true, cancellationToken);

    public static Task<Result> SynchronizeDeferredCompletionAsync(CancellationToken cancellationToken = default) =>
        SynchronizeAsync(services: null, markBackgroundWorkHandled: false, cancellationToken);

    public static async Task<Result> SynchronizeAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken = default) =>
        await SynchronizeAsync(services, markBackgroundWorkHandled: true, cancellationToken).ConfigureAwait(false);

    private static async Task<Result> SynchronizeAsync(
        IServiceProvider? services,
        bool markBackgroundWorkHandled,
        CancellationToken cancellationToken)
    {
        var runtime = await InitializeRuntimeAsync(services, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            return Result.Unavailable;
        }

        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        var inbox = new InboxSyncResult(0, 0, 0, 0);
        if (account is not null)
        {
            inbox = await runtime.Inbox.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        }

        BackgroundSyncBridge.PublishCompletedSync();

        if (markBackgroundWorkHandled)
        {
            BackgroundSyncBridge.MarkHandled();
        }

        return new Result(true, inbox);
    }

    public static Task<IReadOnlyList<PendingIncomingMessageNotification>> ListPendingIncomingMessageNotificationIdsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        ListPendingIncomingMessageNotificationIdsAsync(services: null, limit, [], cancellationToken);

    public static Task<IReadOnlyList<PendingIncomingMessageNotification>> ListPendingIncomingMessageNotificationIdsAsync(
        int limit,
        IReadOnlyCollection<ConversationId> excludedConversationIds,
        CancellationToken cancellationToken = default) =>
        ListPendingIncomingMessageNotificationIdsAsync(
            services: null,
            limit,
            excludedConversationIds,
            cancellationToken);

    public static async Task<IReadOnlyList<PendingIncomingMessageNotification>> ListPendingIncomingMessageNotificationIdsAsync(
        IServiceProvider? services,
        int limit,
        IReadOnlyCollection<ConversationId> excludedConversationIds,
        CancellationToken cancellationToken = default)
    {
        var runtime = await InitializeRuntimeAsync(services, cancellationToken).ConfigureAwait(false);
        return runtime is null
            ? []
            : await runtime.Messages
                .ListPendingIncomingMessageNotificationIdsAsync(
                    limit,
                    excludedConversationIds,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public static async Task MarkIncomingMessageNotificationsPresentedAsync(
        IReadOnlyCollection<MessageId> messageIds,
        CancellationToken cancellationToken = default) =>
        await MarkIncomingMessageNotificationsPresentedAsync(
            services: null,
            messageIds,
            cancellationToken).ConfigureAwait(false);

    public static async Task MarkIncomingMessageNotificationsPresentedAsync(
        IServiceProvider? services,
        IReadOnlyCollection<MessageId> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        var runtime = await InitializeRuntimeAsync(services, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            throw new InvalidOperationException("The client runtime is unavailable while acknowledging message notifications.");
        }

        await runtime.Messages
            .MarkIncomingMessageNotificationsPresentedAsync(messageIds, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ClientRuntime?> InitializeRuntimeAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        services ??= IPlatformApplication.Current?.Services ?? App.Services;
        var bootstrapper = services?.GetService<ClientRuntimeBootstrapper>();
        return bootstrapper is null
            ? null
            : await bootstrapper.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MauiShareExtensionBridge : IShareExtensionBridge
{
    private static string ShareQueuePath => Path.Combine(MauiProgram.ResolveAppDataDirectory(), "pending-shares.json");
    private static string CompletedKeysPath => Path.Combine(MauiProgram.ResolveAppDataDirectory(), "completed-share-ingress.json");

    public Task<IReadOnlyList<SharePayload>> DrainPendingSharesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = DurableIngressQueue<ShareIngressEntry>.Read(
            ShareQueuePath,
            PlatformIngressLimits.MaxShareCount,
            cancellationToken)
            .Where(entry => !DurableIngressQueue<string>.Contains(
                CompletedKeysPath,
                entry.IdempotencyKey,
                cancellationToken))
            .ToArray();
        return Task.FromResult<IReadOnlyList<SharePayload>>(entries.Select(static item => item.Payload).ToArray());
    }

    public static Task EnqueueAsync(SharePayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnqueueDurably(payload, cancellationToken);
        return Task.CompletedTask;
    }

    public static void EnqueueInBackground(SharePayload payload)
    {
        _ = TryEnqueueFromBackground(payload);
    }

    internal static ShareIngressEnqueueResult TryEnqueueFromBackground(SharePayload payload)
    {
        try
        {
            return EnqueueDurably(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Deep.Client.Maui.CrashDiagnostics.LogException("Share.Enqueue", ex);
            return ShareIngressEnqueueResult.Rejected;
        }
    }

    public static void MarkHandled(SharePayload payload)
    {
        var key = KeyFor(payload);
        DurableIngressQueue<string>.AddUnique(
            CompletedKeysPath,
            key,
            PlatformIngressLimits.MaxCompletedShareKeys,
            PlatformIngressLimits.MaxCompletedKeyBytes);
        DurableIngressQueue<ShareIngressEntry>.Remove(
            ShareQueuePath,
            key,
            PlatformIngressLimits.MaxShareCount,
            static entry => entry.IdempotencyKey);
    }

    internal static void PurgePending()
    {
        var entries = DurableIngressQueue<ShareIngressEntry>.Read(
            ShareQueuePath,
            PlatformIngressLimits.MaxShareCount,
            CancellationToken.None);
        foreach (var entry in entries)
        {
            foreach (var path in entry.Payload.FilePaths)
            {
                DeleteRequired(path);
            }
        }

        DeleteRequired(ShareQueuePath);
        DeleteRequired(CompletedKeysPath);

        var ingressDirectory = Path.Combine(MauiProgram.ResolveAppDataDirectory(), "share-ingress");
        if (Directory.Exists(ingressDirectory))
        {
            Directory.Delete(ingressDirectory, recursive: true);
        }
    }

    private static ShareIngressEnqueueResult EnqueueDurably(SharePayload payload, CancellationToken cancellationToken)
    {
        Validate(payload);
        var key = KeyFor(payload);
        if (DurableIngressQueue<string>.Contains(CompletedKeysPath, key, cancellationToken))
        {
            return ShareIngressEnqueueResult.AlreadyCompleted;
        }

        var result = DurableIngressQueue<ShareIngressEntry>.Enqueue(
            ShareQueuePath,
            new ShareIngressEntry(key, payload),
            PlatformIngressLimits.MaxShareCount,
            PlatformIngressLimits.MaxShareQueueBytes,
            static entry => entry.IdempotencyKey);
        if (result == DurableIngressEnqueueResult.Enqueued)
        {
            PublishPendingShare();
        }

        return result switch
        {
            DurableIngressEnqueueResult.Enqueued => ShareIngressEnqueueResult.Enqueued,
            DurableIngressEnqueueResult.AlreadyPresent => ShareIngressEnqueueResult.AlreadyPending,
            _ => ShareIngressEnqueueResult.Rejected
        };
    }

    private static void PublishPendingShare()
    {
        try
        {
            PendingSharePublished?.Invoke();
        }
        catch (Exception ex)
        {
            Deep.Client.Maui.CrashDiagnostics.LogException("Share.Publish", ex);
        }
    }

    private static void DeleteRequired(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void Validate(SharePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Encoding.UTF8.GetByteCount(payload.Text ?? string.Empty) > PlatformIngressLimits.MaxShareTextBytes)
        {
            throw new InvalidOperationException("Share text exceeds the ingress limit.");
        }

        if (payload.FilePaths.Count > PlatformIngressLimits.MaxShareUriCount)
        {
            throw new InvalidOperationException("Share contains too many files.");
        }

        long totalBytes = 0;
        foreach (var path in payload.FilePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > PlatformIngressLimits.MaxShareUriLength || !File.Exists(path))
            {
                throw new InvalidOperationException("Share contains an invalid persisted file.");
            }

            var length = new FileInfo(path).Length;
            if (length > PlatformIngressLimits.MaxShareFileBytes || checked(totalBytes + length) > PlatformIngressLimits.MaxShareTotalBytes)
            {
                throw new InvalidOperationException("Shared files exceed the ingress limit.");
            }

            totalBytes += length;
        }

        if (string.IsNullOrWhiteSpace(payload.Text) && payload.FilePaths.Count == 0)
        {
            throw new InvalidOperationException("Share payload is empty.");
        }
    }

    private static string KeyFor(SharePayload payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, payload.Text ?? string.Empty);
        foreach (var path in payload.FilePaths)
        {
            AppendHash(hash, path);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    public static event Action? PendingSharePublished;
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
    private static string PendingNotificationsPath => Path.Combine(MauiProgram.ResolveAppDataDirectory(), "pending-notifications.json");

    public async Task ScheduleAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        queue.RemoveAll(item => string.Equals(item.Identifier, request.Identifier, StringComparison.OrdinalIgnoreCase));
        queue.Add(request);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        queue.RemoveAll(item => item.ConversationId == conversationId);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    internal static Task PurgeAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(PendingNotificationsPath))
        {
            File.Delete(PendingNotificationsPath);
        }

        return Task.CompletedTask;
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
    private static string QueuePath => Path.Combine(MauiProgram.ResolveAppDataDirectory(), "pending-notification-actions.json");
    private static string CompletedKeysPath => Path.Combine(MauiProgram.ResolveAppDataDirectory(), "completed-notification-actions.json");

    public static void Publish(NotificationAction action)
    {
        if (!IsValid(action))
        {
            return;
        }

        var key = KeyFor(action);
        if (DurableIngressQueue<string>.Contains(CompletedKeysPath, key, CancellationToken.None))
        {
            return;
        }

        try
        {
            var result = DurableIngressQueue<NotificationAction>.Enqueue(
                QueuePath,
                action,
                PlatformIngressLimits.MaxNotificationCount,
                PlatformIngressLimits.MaxNotificationQueueBytes,
                KeyFor);
            if (result == DurableIngressEnqueueResult.Enqueued)
            {
                Published?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Deep.Client.Maui.CrashDiagnostics.LogException("NotificationIngress.Publish", ex);
        }
    }

    public static IReadOnlyList<NotificationAction> Drain(CancellationToken cancellationToken = default)
    {
        return DurableIngressQueue<NotificationAction>.Read(
            QueuePath,
            PlatformIngressLimits.MaxNotificationCount,
            cancellationToken)
            .Where(action => !DurableIngressQueue<string>.Contains(
                CompletedKeysPath,
                KeyFor(action),
                cancellationToken))
            .ToArray();
    }

    public static void MarkHandled(NotificationAction action)
    {
        var key = KeyFor(action);
        DurableIngressQueue<string>.AddUnique(
            CompletedKeysPath,
            key,
            PlatformIngressLimits.MaxCompletedNotificationKeys,
            PlatformIngressLimits.MaxCompletedKeyBytes);
        DurableIngressQueue<NotificationAction>.Remove(
            QueuePath,
            key,
            PlatformIngressLimits.MaxNotificationCount,
            KeyFor);
    }

    internal static void Purge()
    {
        DeleteRequired(QueuePath);
        DeleteRequired(CompletedKeysPath);
    }

    private static bool IsValid(NotificationAction action) =>
        !string.IsNullOrWhiteSpace(action.ActionId) && action.ActionId.Length <= PlatformIngressLimits.MaxNotificationActionLength
        && !string.IsNullOrWhiteSpace(action.ConversationId) && action.ConversationId.Length <= PlatformIngressLimits.MaxNotificationConversationLength
        && !string.IsNullOrWhiteSpace(action.NotificationId) && action.NotificationId.Length <= PlatformIngressLimits.MaxNotificationIdLength;

    private static string KeyFor(NotificationAction action) =>
        $"{action.ActionId}\u001f{action.ConversationId}\u001f{action.NotificationId}";

    private static void DeleteRequired(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static event Action? Published;
}

internal sealed record ShareIngressEntry(string IdempotencyKey, SharePayload Payload);

internal enum ShareIngressEnqueueResult
{
    Enqueued,
    AlreadyPending,
    AlreadyCompleted,
    Rejected
}

internal enum DurableIngressEnqueueResult
{
    Enqueued,
    AlreadyPresent,
    Rejected
}

internal static class PlatformIngressLimits
{
    public const int MaxShareCount = 32;
    public const int MaxShareTextBytes = 64 * 1024;
    public const int MaxShareUriCount = 8;
    public const int MaxShareUriLength = 4096;
    public const long MaxShareFileBytes = 25L * 1024 * 1024;
    public const long MaxShareTotalBytes = 25L * 1024 * 1024;
    public const long MaxShareQueueBytes = 512L * 1024;
    public const int MaxNotificationCount = 64;
    public const int MaxNotificationActionLength = 64;
    public const int MaxNotificationConversationLength = 256;
    public const int MaxNotificationIdLength = 128;
    public const long MaxNotificationQueueBytes = 256L * 1024;
    public const int MaxCompletedShareKeys = 128;
    public const int MaxCompletedNotificationKeys = 128;
    public const long MaxCompletedKeyBytes = 32L * 1024;
}

internal static class DurableIngressQueue<T>
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DurableIngressEnqueueResult Enqueue(
        string path,
        T item,
        int maxCount,
        long maxBytes,
        Func<T, string> keySelector)
    {
        lock (Gate)
        {
            var queue = ReadUnsafe(path, maxCount).ToList();
            var key = keySelector(item);
            if (queue.Any(existing => string.Equals(keySelector(existing), key, StringComparison.Ordinal)))
            {
                return DurableIngressEnqueueResult.AlreadyPresent;
            }

            queue.Add(item);
            while (queue.Count > maxCount || SerializedSize(queue) > maxBytes)
            {
                queue.RemoveAt(0);
            }

            if (!queue.Any(existing => string.Equals(keySelector(existing), key, StringComparison.Ordinal)))
            {
                return DurableIngressEnqueueResult.Rejected;
            }

            WriteUnsafe(path, queue);
            return DurableIngressEnqueueResult.Enqueued;
        }
    }

    public static IReadOnlyList<T> Read(string path, int maxCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            return ReadUnsafe(path, maxCount);
        }
    }

    public static bool Remove(
        string path,
        string key,
        int maxCount,
        Func<T, string> keySelector)
    {
        lock (Gate)
        {
            var queue = ReadUnsafe(path, maxCount).ToList();
            var removed = queue.RemoveAll(item =>
                string.Equals(keySelector(item), key, StringComparison.Ordinal)) > 0;
            if (!removed)
            {
                return false;
            }

            if (queue.Count == 0)
            {
                TryDelete(path);
            }
            else
            {
                WriteUnsafe(path, queue);
            }

            return true;
        }
    }

    public static bool Contains(string path, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            return ReadUnsafe(path, int.MaxValue)
                .Any(item => string.Equals(ItemKey(item), key, StringComparison.Ordinal));
        }
    }

    public static void AddUnique(string path, T item, int maxCount, long maxBytes)
    {
        lock (Gate)
        {
            var queue = ReadUnsafe(path, maxCount).ToList();
            if (queue.Any(existing => string.Equals(ItemKey(existing), ItemKey(item), StringComparison.Ordinal)))
            {
                return;
            }

            queue.Add(item);
            while (queue.Count > maxCount || SerializedSize(queue) > maxBytes)
            {
                queue.RemoveAt(0);
            }

            WriteUnsafe(path, queue);
        }
    }

    private static string ItemKey(T item) => item is null ? string.Empty : item.ToString() ?? string.Empty;

    private static IReadOnlyList<T> ReadUnsafe(string path, int maxCount)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var queue = JsonSerializer.Deserialize<List<T>>(File.ReadAllBytes(path), JsonOptions) ?? [];
            return queue.Count <= maxCount ? queue : queue.TakeLast(maxCount).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return [];
        }
    }

    private static long SerializedSize(IReadOnlyList<T> queue) =>
        JsonSerializer.SerializeToUtf8Bytes(queue, JsonOptions).LongLength;

    private static void WriteUnsafe(string path, IReadOnlyList<T> queue)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(queue, JsonOptions);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
