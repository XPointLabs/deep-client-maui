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

        if (!attachmentFiles.IsEnabled || attachment.RemoteUri is null)
        {
            await page.DisplayAlertAsync(
                "Вложение недоступно",
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.",
                "OK");
            return;
        }

        try
        {
            var file = await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
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
        if (!attachmentFiles.IsEnabled || attachment.RemoteUri is null)
        {
            throw new InvalidOperationException(
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.");
        }

        var downloaded = await attachmentFiles.DownloadAsync(attachment, cancellationToken).ConfigureAwait(false);
        var fileName = SafeFileName(downloaded.FileName);
        CleanupOldAttachmentCache();
        var cachePath = Path.Combine(
            FileSystem.CacheDirectory,
            $"attachment-{SafeFileName(attachment.AttachmentId)}-{fileName}");

        await File.WriteAllBytesAsync(cachePath, downloaded.Content, cancellationToken).ConfigureAwait(false);
        return new PreparedAttachmentFile(downloaded.FileName, downloaded.ContentType, cachePath);
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
