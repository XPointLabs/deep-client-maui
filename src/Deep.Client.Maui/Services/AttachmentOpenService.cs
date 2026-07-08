using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

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

        var downloaded = await attachmentFiles.DownloadAsync(attachment, cancellationToken).ConfigureAwait(false);
        CleanupOldAttachmentCache();
        var cachePath = CachePathFor(attachment);

        await File.WriteAllBytesAsync(cachePath, downloaded.Content, cancellationToken).ConfigureAwait(false);
        return new PreparedAttachmentFile(downloaded.FileName, downloaded.ContentType, cachePath);
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
