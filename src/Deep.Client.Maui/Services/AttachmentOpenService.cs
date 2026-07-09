using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

#if ANDROID
using System.Runtime.Versioning;
using Android.Content;
using Android.Provider;
using AndroidApplication = Android.App.Application;
using AndroidEnvironment = Android.OS.Environment;
#endif

namespace Deep.Client.Maui.Services;

public sealed record PreparedAttachmentFile(string FileName, string ContentType, string Path);

public static class AttachmentOpenService
{
    private static readonly TimeSpan AttachmentCacheMaxAge = TimeSpan.FromDays(1);

    public static async Task OpenAsync(
        Page page,
        IReadOnlyList<AttachmentMetadata> attachments,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        var attachment = attachments[0];

        var cached = TryGetCachedFile(attachment);
        if (cached is null && (!attachmentFiles.IsEnabled || attachment.RemoteUri is null))
        {
            await page.DisplayAlertAsync(
                "Вложение недоступно",
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.",
                "OK");
            return;
        }

        try
        {
            var file = cached
                ?? await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                file.FileName,
                new ReadOnlyFile(file.Path, file.ContentType)));
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Не удалось открыть вложение", ex.Message, "OK");
        }
    }

    public static async Task ShareAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        var file = await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = file.FileName,
            File = new ShareFile(file.Path, file.ContentType)
        }).ConfigureAwait(false);
    }

    public static async Task<string> SaveAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        var file = await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
        return await SavePreparedFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PreparedAttachmentFile> DownloadToCacheAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        if (TryGetCachedFile(attachment) is { } cached)
        {
            return cached;
        }

        if (!attachmentFiles.IsEnabled || attachment.RemoteUri is null)
        {
            throw new InvalidOperationException(
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.");
        }

        CleanupOldAttachmentCache();
        var cachePath = CachePathFor(attachment);
        try
        {
            await using var output = File.Create(cachePath);
            var downloaded = await attachmentFiles.DownloadToAsync(attachment, output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new PreparedAttachmentFile(downloaded.FileName, downloaded.ContentType, cachePath);
        }
        catch
        {
            TryDelete(cachePath);
            throw;
        }
    }

    public static PreparedAttachmentFile? TryGetCachedFile(AttachmentMetadata attachment)
    {
        var cachePath = CachePathFor(attachment);
        return File.Exists(cachePath)
            ? new PreparedAttachmentFile(attachment.FileName, attachment.ContentType, cachePath)
            : null;
    }

    public static async Task<PreparedAttachmentFile> CacheLocalCopyAsync(
        AttachmentMetadata attachment,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Attachment source file was not found.", sourcePath);
        }

        CleanupOldAttachmentCache();
        var cachePath = CachePathFor(attachment);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(cachePath), StringComparison.OrdinalIgnoreCase))
        {
            await using var source = File.OpenRead(sourcePath);
            await using var destination = File.Create(cachePath);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PreparedAttachmentFile(attachment.FileName, attachment.ContentType, cachePath);
    }

    private static string CachePathFor(AttachmentMetadata attachment) =>
        Path.Combine(
            FileSystem.CacheDirectory,
            $"attachment-{SafeFileName(attachment.AttachmentId)}-{SafeFileName(attachment.FileName)}");

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

    private static async Task<string> SavePreparedFileAsync(
        PreparedAttachmentFile file,
        CancellationToken cancellationToken)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return await SavePreparedFileToMediaStoreAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var publicFolder = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? AndroidEnvironment.DirectoryPictures
            : AndroidEnvironment.DirectoryDownloads;
        var publicDirectory = AndroidEnvironment.GetExternalStoragePublicDirectory(publicFolder)?.AbsolutePath
            ?? FileSystem.AppDataDirectory;
        var destinationDirectory = Path.Combine(publicDirectory, "Deep");
        Directory.CreateDirectory(destinationDirectory);
        var destination = UniqueFilePath(destinationDirectory, file.FileName);
        await CopyFileAsync(file.Path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
#else
        var downloads = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (string.IsNullOrWhiteSpace(downloads) || !Directory.Exists(downloads))
        {
            downloads = FileSystem.AppDataDirectory;
        }

        var destinationDirectory = Path.Combine(downloads, "Deep");
        Directory.CreateDirectory(destinationDirectory);
        var destination = UniqueFilePath(destinationDirectory, file.FileName);
        await CopyFileAsync(file.Path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
#endif
    }

#if ANDROID
    [SupportedOSPlatform("android29.0")]
    private static async Task<string> SavePreparedFileToMediaStoreAsync(
        PreparedAttachmentFile file,
        CancellationToken cancellationToken)
    {
        var resolver = AndroidApplication.Context.ContentResolver;
        if (resolver is null)
        {
            throw new InvalidOperationException("Android content resolver is not available.");
        }

        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var collection = isImage
            ? MediaStore.Images.Media.ExternalContentUri
            : isVideo
                ? MediaStore.Video.Media.ExternalContentUri
                : MediaStore.Downloads.ExternalContentUri;
        if (collection is null)
        {
            throw new InvalidOperationException("Android MediaStore collection is not available.");
        }

        var relativePath = isImage
            ? $"{AndroidEnvironment.DirectoryPictures}/Deep"
            : isVideo
                ? $"{AndroidEnvironment.DirectoryMovies}/Deep"
                : $"{AndroidEnvironment.DirectoryDownloads}/Deep";

        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, file.FileName);
        values.Put(MediaStore.IMediaColumns.MimeType, contentType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(collection, values)
            ?? throw new InvalidOperationException("Android MediaStore did not create an output URI.");
        try
        {
            await using (var source = File.OpenRead(file.Path))
            await using (var destination = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("Android MediaStore did not open an output stream."))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
            return uri.ToString() ?? file.FileName;
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }
#endif

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = File.OpenRead(source);
        await using var destinationStream = File.Create(destination);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string UniqueFilePath(string directory, string fileName)
    {
        var safeName = SafeFileName(fileName);
        var candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string SafeFileName(string fileName)
    {
        var safe = string.IsNullOrWhiteSpace(fileName)
            ? "attachment"
            : fileName;

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
    }

    private static void CleanupOldAttachmentCache()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - AttachmentCacheMaxAge;
            foreach (var file in Directory.EnumerateFiles(FileSystem.CacheDirectory, "attachment-*"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    info.Delete();
                }
            }
        }
        catch
        {
            // Cache cleanup is opportunistic.
        }
    }
}
