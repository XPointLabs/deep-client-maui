#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System.Text;
using Uri = Android.Net.Uri;

namespace Deep.Client.Maui;

[Activity(Name = "network.xpoint.deep.ShareIngressActivity", Exported = true, NoHistory = true)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "text/plain")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "video/*")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "audio/*")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/pdf")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "video/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "audio/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/pdf")]
public sealed class ShareIngressActivity : Activity
{
    private const long MaxTextBytes = 64L * 1024;
    private const long MaxFileBytes = 25L * 1024 * 1024;
    private const int MaxFiles = 8;
    private const int MaxUriLength = 4096;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            if (Intent is not null && TryCreateWorkerIntent(Intent, out var workerIntent))
            {
                StartService(workerIntent);
            }
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("Android.ShareIngress", ex);
        }
        finally
        {
            FinishOnMainThread();
        }
    }

    private bool TryCreateWorkerIntent(Intent source, out Intent workerIntent)
    {
        workerIntent = new Intent(this, typeof(ShareIngressService));
        if (!TryReadShareMetadata(source, out var workItem))
        {
            return false;
        }

        workerIntent.SetAction(ShareIngressService.ProcessAction);
        workerIntent.SetType(workItem.MimeType);
        workerIntent.PutExtra(ShareIngressService.OriginalActionExtra, workItem.Action);
        workerIntent.PutExtra(ShareIngressService.TextExtra, workItem.Text);
        workerIntent.PutExtra(ShareIngressService.UriCountExtra, workItem.Uris.Count);
        workerIntent.AddFlags(source.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission));

        for (var index = 0; index < workItem.Uris.Count; index++)
        {
            workerIntent.PutExtra($"{ShareIngressService.UriExtraPrefix}{index}", workItem.Uris[index].ToString());
        }

        if (workItem.Uris.Count > 0)
        {
            // ClipData carries the transient grant to the service when the sender did not use EXTRA_STREAM.
            var clipData = source.ClipData ?? ClipData.NewRawUri("shared-content", workItem.Uris[0])!;
            if (source.ClipData is null)
            {
                for (var index = 1; index < workItem.Uris.Count; index++)
                {
                    clipData.AddItem(new ClipData.Item(workItem.Uris[index]!));
                }
            }

            workerIntent.ClipData = clipData;
        }

        return true;
    }

    internal static bool TryReadShareMetadata(Intent intent, out ShareIngressWorkItem workItem)
    {
        workItem = new ShareIngressWorkItem(string.Empty, string.Empty, null, Array.Empty<Uri>(), (ActivityFlags)0);
        var action = intent.GetStringExtra(ShareIngressService.OriginalActionExtra) ?? intent.Action;
        var mimeType = intent.Type;
        if (action is null
            || mimeType is null
            || (!string.Equals(action, Intent.ActionSend, StringComparison.Ordinal)
                && !string.Equals(action, Intent.ActionSendMultiple, StringComparison.Ordinal))
            || !IsSupportedMime(mimeType))
        {
            return false;
        }

        var text = intent.GetStringExtra(Intent.ExtraText) ?? intent.GetStringExtra(ShareIngressService.TextExtra);
        if (Encoding.UTF8.GetByteCount(text ?? string.Empty) > MaxTextBytes)
        {
            return false;
        }

        var uris = GetUris(intent);
        var isMultiple = string.Equals(action, Intent.ActionSendMultiple, StringComparison.Ordinal);
        if (isMultiple && (uris.Count == 0 || uris.Count > MaxFiles || intent.ClipData?.ItemCount > MaxFiles))
        {
            return false;
        }

        if (!isMultiple && uris.Count > 1)
        {
            return false;
        }

        if (uris.Count == 0)
        {
            workItem = new ShareIngressWorkItem(action, mimeType, text, uris, intent.Flags);
            return !string.IsNullOrWhiteSpace(text)
                && string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase);
        }

        if ((intent.Flags & ActivityFlags.GrantReadUriPermission) == 0)
        {
            return false;
        }

        foreach (var uri in uris)
        {
            if (uri is null
                || !string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase)
                || (uri.ToString() ?? string.Empty).Length > MaxUriLength)
            {
                return false;
            }
        }

        workItem = new ShareIngressWorkItem(action, mimeType, text, uris, intent.Flags);
        return true;
    }

    internal static SharePayload PersistShare(ContentResolver resolver, ShareIngressWorkItem workItem)
    {
        if (workItem.Uris.Count == 0)
        {
            return new SharePayload(workItem.Text, Array.Empty<string>());
        }

        var paths = new List<string>(workItem.Uris.Count);
        var fingerprint = Fingerprint(workItem);
        long totalBytes = 0;
        try
        {
            PreserveUriPermissions(resolver, workItem);
            for (var index = 0; index < workItem.Uris.Count; index++)
            {
                var uri = workItem.Uris[index];
                var actualMime = resolver.GetType(uri);
                if (!IsConcreteMimeMatch(workItem.MimeType, actualMime))
                {
                    throw new InvalidOperationException("Shared URI MIME type does not match the activation.");
                }

                var fileName = SafeFileName(QueryDisplayName(resolver, uri) ?? $"shared-{index + 1}");
                var transcodeImage = ShouldTranscodeImage(actualMime);
                if (transcodeImage)
                {
                    fileName = WithJpegExtension(fileName);
                }

                var path = Path.Combine(
                    MauiProgram.ResolveAppDataDirectory(),
                    "share-ingress",
                    $"{fingerprint}-{index}-{fileName}");
                var length = PersistUri(resolver, uri, path, transcodeImage);
                if (length <= 0 || length > MaxFileBytes || checked(totalBytes + length) > MaxFileBytes)
                {
                    throw new InvalidOperationException("Shared files exceed the ingress limit.");
                }

                totalBytes += length;
                paths.Add(path);
            }

            return new SharePayload(workItem.Text, paths);
        }
        catch
        {
            DeletePersistedFiles(new SharePayload(workItem.Text, paths));
            throw;
        }
        finally
        {
            ReleasePersistedUriPermissions(resolver, workItem);
        }
    }

    private static IReadOnlyList<Uri> GetUris(Intent intent)
    {
        if (intent.ClipData is { ItemCount: > 0 } clipData)
        {
            var result = new List<Uri>(Math.Min(clipData.ItemCount, MaxFiles));
            for (var index = 0; index < clipData.ItemCount && index < MaxFiles; index++)
            {
                if (clipData.GetItemAt(index)?.Uri is { } uri)
                {
                    result.Add(uri);
                }
            }

            return result;
        }

        if (string.Equals(intent.Action, Intent.ActionSendMultiple, StringComparison.Ordinal))
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var streams = intent.GetParcelableArrayListExtra(
                    Intent.ExtraStream,
                    Java.Lang.Class.FromType(typeof(Uri)));
                return streams is null
                    ? []
                    : streams.OfType<Uri>().Take(MaxFiles + 1).ToArray();
            }

#pragma warning disable CA1422
            return intent.GetParcelableArrayListExtra(Intent.ExtraStream)?
                .OfType<Uri>()
                .Take(MaxFiles + 1)
                .ToArray() ?? [];
#pragma warning restore CA1422
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Uri))) is Uri uri
                ? [uri]
                : [];
        }

#pragma warning disable CA1422
        return intent.GetParcelableExtra(Intent.ExtraStream) is Uri legacyUri ? [legacyUri] : [];
#pragma warning restore CA1422
    }

    private static void PreserveUriPermissions(ContentResolver resolver, ShareIngressWorkItem workItem)
    {
        if ((workItem.Flags & ActivityFlags.GrantPersistableUriPermission) == 0)
        {
            return;
        }

        foreach (var uri in workItem.Uris)
        {
            try
            {
                resolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException)
            {
                // Some providers grant a transient read permission only. The service still owns that grant.
            }
        }
    }

    private static void ReleasePersistedUriPermissions(ContentResolver resolver, ShareIngressWorkItem workItem)
    {
        if ((workItem.Flags & ActivityFlags.GrantPersistableUriPermission) == 0)
        {
            return;
        }

        foreach (var uri in workItem.Uris)
        {
            try
            {
                resolver.ReleasePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException)
            {
            }
        }
    }

    private static long PersistUri(ContentResolver resolver, Uri uri, string path, bool transcodeImage)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        string? transcodedPath = null;
        try
        {
            long total = 0;
            using (var source = resolver.OpenInputStream(uri) ?? throw new InvalidOperationException("Shared URI cannot be opened."))
            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total = checked(total + read);
                    if (total > MaxFileBytes)
                    {
                        throw new InvalidOperationException("Shared file exceeds the ingress limit.");
                    }

                    destination.Write(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
            }

            if (transcodeImage)
            {
                var transcoded = MauiMediaCodecService.TranscodeImageAndroid(
                    new MediaTranscodeRequest(temporaryPath, "image/jpeg", MaxFileBytes));
                transcodedPath = transcoded.OutputPath;
                File.Move(transcodedPath, path, overwrite: true);
                transcodedPath = null;
                return transcoded.SizeBytes;
            }

            File.Move(temporaryPath, path, overwrite: true);
            return total;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (!string.IsNullOrWhiteSpace(transcodedPath) && File.Exists(transcodedPath))
            {
                File.Delete(transcodedPath);
            }
        }
    }

    private static string? QueryDisplayName(ContentResolver resolver, Uri uri)
    {
        using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
        if (cursor is null || !cursor.MoveToFirst())
        {
            return null;
        }

        var column = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
        return column >= 0 ? cursor.GetString(column) : null;
    }

    private static bool IsSupportedMime(string? mime) =>
        !string.IsNullOrWhiteSpace(mime)
        && !string.Equals(mime, "*/*", StringComparison.Ordinal)
        && (mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    private static bool IsConcreteMimeMatch(string? requested, string? actual) =>
        IsSupportedMime(actual)
        && (string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase)
            || (requested?.EndsWith("/*", StringComparison.Ordinal) == true
                && actual?.StartsWith(requested[..^1], StringComparison.OrdinalIgnoreCase) == true));

    private static bool ShouldTranscodeImage(string? mimeType) =>
        mimeType?.ToLowerInvariant() is
            "image/jpeg"
            or "image/png"
            or "image/webp"
            or "image/heif"
            or "image/heic"
            or "image/avif";

    private static string Fingerprint(ShareIngressWorkItem workItem)
    {
        var value = $"{workItem.Action}\u001f{workItem.MimeType}\u001f{workItem.Text}\u001f{string.Join("\u001f", workItem.Uris)}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }

    private static string SafeFileName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "shared-file" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length > 160 ? name[..160] : name;
    }

    private static string WithJpegExtension(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return $"{(string.IsNullOrWhiteSpace(name) ? "shared-photo" : name)}.jpg";
    }

    internal static void DeletePersistedFiles(SharePayload payload)
    {
        foreach (var path in payload.FilePaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("Android.ShareIngress.Delete", ex);
            }
        }
    }

    private void FinishOnMainThread()
    {
        if (MainThread.IsMainThread)
        {
            Finish();
            return;
        }

        MainThread.BeginInvokeOnMainThread(Finish);
    }
}

internal sealed record ShareIngressWorkItem(
    string Action,
    string MimeType,
    string? Text,
    IReadOnlyList<Uri> Uris,
    ActivityFlags Flags);

[Service(Name = "network.xpoint.deep.ShareIngressService", Exported = false)]
public sealed class ShareIngressService : Service
{
    internal const string ProcessAction = "network.xpoint.deep.action.PROCESS_SHARE";
    internal const string OriginalActionExtra = "share_original_action";
    internal const string TextExtra = "share_text";
    internal const string UriCountExtra = "share_uri_count";
    internal const string UriExtraPrefix = "share_uri_";

    private readonly SemaphoreSlim workGate = new(1, 1);

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent is null)
        {
            StopSelfResult(startId);
            return StartCommandResult.NotSticky;
        }

        var work = ProcessIntentAsync(intent, startId);
        _ = work.ContinueWith(
            completed => CrashDiagnostics.LogException("Android.ShareIngressService", completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return StartCommandResult.RedeliverIntent;
    }

    private async Task ProcessIntentAsync(Intent intent, int startId)
    {
        SharePayload? payload = null;
        var gateHeld = false;
        try
        {
            await workGate.WaitAsync().ConfigureAwait(false);
            gateHeld = true;
            if (!ShareIngressActivity.TryReadShareMetadata(intent, out var workItem))
            {
                return;
            }

            payload = ShareIngressActivity.PersistShare(ContentResolver!, workItem);
            var enqueueResult = MauiShareExtensionBridge.TryEnqueueFromBackground(payload);
            if (enqueueResult is ShareIngressEnqueueResult.AlreadyCompleted or ShareIngressEnqueueResult.Rejected)
            {
                ShareIngressActivity.DeletePersistedFiles(payload);
                return;
            }

            // A pending duplicate shares its deterministic persisted paths with the queued entry.
            // Do not delete those paths while the original share is waiting for foreground handling.
            payload = null;
            MainThread.BeginInvokeOnMainThread(LaunchMainActivity);
        }
        catch (Exception ex)
        {
            if (payload is not null)
            {
                ShareIngressActivity.DeletePersistedFiles(payload);
            }

            CrashDiagnostics.LogException("Android.ShareIngressService", ex);
        }
        finally
        {
            if (gateHeld)
            {
                workGate.Release();
            }

            StopSelfResult(startId);
        }
    }

    private void LaunchMainActivity()
    {
        try
        {
            StartActivity(new Intent(this, typeof(MainActivity))
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop));
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("Android.ShareIngressService.Launch", ex);
        }
    }
}
#endif
