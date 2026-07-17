#if WINDOWS
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Windows.Storage.Streams;
#endif

namespace Deep.Client.Maui;

#if WINDOWS
internal static class WindowsShareTargetService
{
    public static bool TryGetShareActivation(
        Microsoft.Windows.AppLifecycle.AppActivationArguments? activation,
        out ShareTargetActivatedEventArgs? shareArgs)
    {
        shareArgs = activation?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.ShareTarget
            ? activation.Data as ShareTargetActivatedEventArgs
            : null;
        return shareArgs is not null;
    }

    public static async Task ProcessAsync(
        ShareTargetActivatedEventArgs shareArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shareArgs);
        var operation = shareArgs.ShareOperation;
        operation.ReportStarted();
        SharePayload? payload = null;
        try
        {
            payload = await PersistAsync(operation, cancellationToken).ConfigureAwait(false);
            var result = MauiShareExtensionBridge.TryEnqueueFromBackground(payload);
            if (result == ShareIngressEnqueueResult.Rejected)
            {
                DeletePersistedFiles(payload);
                throw new InvalidOperationException("The shared content could not be queued.");
            }

            if (result == ShareIngressEnqueueResult.AlreadyCompleted)
            {
                DeletePersistedFiles(payload);
            }

            operation.ReportCompleted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (payload is not null)
            {
                DeletePersistedFiles(payload);
            }

            operation.ReportError("Импорт в Deep отменён.");
        }
        catch (Exception exception)
        {
            if (payload is not null)
            {
                DeletePersistedFiles(payload);
            }

            CrashDiagnostics.LogException("Windows.ShareTarget", exception);
            operation.ReportError("Deep не удалось импортировать выбранные данные.");
        }
    }

    private static async Task<SharePayload> PersistAsync(
        ShareOperation operation,
        CancellationToken cancellationToken)
    {
        var data = operation.Data;
        var text = await ReadTextAsync(data, cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(MauiProgram.ResolveAppDataDirectory(), "share-ingress");
        Directory.CreateDirectory(directory);
        var fingerprint = Guid.NewGuid().ToString("N");
        var paths = new List<string>();
        long totalBytes = 0;

        try
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (items.Count > PlatformIngressLimits.MaxShareUriCount)
                {
                    throw new InvalidOperationException("Share contains too many files.");
                }

                foreach (var file in items.OfType<IStorageFile>())
                {
                    var safeName = SafeFileName(file.Name);
                    var destination = Path.Combine(directory, $"{fingerprint}-{paths.Count}-{safeName}");
                    var copied = await CopyStorageFileAsync(
                        file,
                        destination,
                        PlatformIngressLimits.MaxShareFileBytes,
                        PlatformIngressLimits.MaxShareTotalBytes - totalBytes,
                        cancellationToken).ConfigureAwait(false);
                    totalBytes = checked(totalBytes + copied);
                    paths.Add(destination);
                }
            }

            if (paths.Count == 0 && data.Contains(StandardDataFormats.Bitmap))
            {
                var bitmap = await data.GetBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);
                await using var source = (await bitmap.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false))
                    .AsStreamForRead();
                var destination = Path.Combine(directory, $"{fingerprint}-shared-image.png");
                totalBytes = await WindowsShareFileCopy.CopyBoundedAsync(
                    source,
                    destination,
                    PlatformIngressLimits.MaxShareFileBytes,
                    cancellationToken).ConfigureAwait(false);
                paths.Add(destination);
            }

            if (string.IsNullOrWhiteSpace(text) && paths.Count == 0)
            {
                throw new InvalidOperationException("The share payload is empty or unsupported.");
            }

            return new SharePayload(text, paths);
        }
        catch
        {
            DeletePersistedFiles(new SharePayload(text, paths));
            throw;
        }
    }

    private static async Task<string?> ReadTextAsync(
        Windows.ApplicationModel.DataTransfer.DataPackageView data,
        CancellationToken cancellationToken)
    {
        string? text = null;
        if (data.Contains(StandardDataFormats.Text))
        {
            text = await data.GetTextAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        else if (data.Contains(StandardDataFormats.WebLink))
        {
            text = (await data.GetWebLinkAsync().AsTask(cancellationToken).ConfigureAwait(false)).ToString();
        }
        else if (data.Contains(StandardDataFormats.ApplicationLink))
        {
            text = (await data.GetApplicationLinkAsync().AsTask(cancellationToken).ConfigureAwait(false)).ToString();
        }

        if (System.Text.Encoding.UTF8.GetByteCount(text ?? string.Empty) > PlatformIngressLimits.MaxShareTextBytes)
        {
            throw new InvalidOperationException("Share text exceeds the ingress limit.");
        }

        return text;
    }

    private static async Task<long> CopyStorageFileAsync(
        IStorageFile file,
        string destination,
        long fileLimit,
        long remainingTotal,
        CancellationToken cancellationToken)
    {
        var properties = await file.GetBasicPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        var limit = Math.Min(fileLimit, remainingTotal);
        if (properties.Size == 0 || properties.Size > (ulong)Math.Max(0, limit))
        {
            throw new InvalidOperationException("Shared files exceed the ingress limit.");
        }

        await using var source = (await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false))
            .AsStreamForRead();
        return await WindowsShareFileCopy
            .CopyBoundedAsync(source, destination, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SafeFileName(string? value)
    {
        var name = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "shared-file";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static void DeletePersistedFiles(SharePayload payload)
    {
        foreach (var path in payload.FilePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                CrashDiagnostics.LogException("Windows.ShareTarget.Delete", exception);
            }
        }
    }
}
#endif

internal static class WindowsShareFileCopy
{
    private const int BufferSize = 64 * 1024;

    public static async Task<long> CopyBoundedAsync(
        Stream source,
        string destination,
        long limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (limit <= 0)
        {
            throw new InvalidOperationException("Shared files exceed the ingress limit.");
        }

        var stagingPath = $"{destination}.{Guid.NewGuid():N}.partial";
        var promoted = false;
        try
        {
            long written = 0;
            await using (var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written = checked(written + read);
                    if (written > limit)
                    {
                        throw new InvalidOperationException("Shared files exceed the ingress limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (written == 0)
            {
                throw new InvalidOperationException("Shared file is empty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination);
            promoted = true;
            return written;
        }
        finally
        {
            if (!promoted)
            {
                TryDelete(stagingPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
