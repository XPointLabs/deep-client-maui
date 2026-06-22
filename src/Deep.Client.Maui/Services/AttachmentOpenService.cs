using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public static class AttachmentOpenService
{
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

        var attachment = attachments.Count == 1
            ? attachments[0]
            : await SelectAttachmentAsync(page, attachments);

        if (attachment is null)
        {
            return;
        }

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
            var downloaded = await attachmentFiles.DownloadAsync(attachment, cancellationToken);
            var fileName = SafeFileName(downloaded.FileName);
            var cachePath = Path.Combine(
                FileSystem.CacheDirectory,
                $"attachment-{attachment.AttachmentId}-{fileName}");

            await File.WriteAllBytesAsync(cachePath, downloaded.Content, cancellationToken).ConfigureAwait(false);
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                downloaded.FileName,
                new ReadOnlyFile(cachePath, downloaded.ContentType)));
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Не удалось открыть вложение", ex.Message, "OK");
        }
    }

    private static async Task<AttachmentMetadata?> SelectAttachmentAsync(
        Page page,
        IReadOnlyList<AttachmentMetadata> attachments)
    {
        var labels = attachments
            .Select((attachment, index) => $"{index + 1}. {attachment.FileName}")
            .ToArray();

        var selected = await page.DisplayActionSheetAsync("Вложения", "Отмена", null, labels);
        if (string.IsNullOrWhiteSpace(selected) || selected == "Отмена")
        {
            return null;
        }

        var indexEnd = selected.IndexOf('.', StringComparison.Ordinal);
        return indexEnd > 0
            && int.TryParse(selected[..indexEnd], out var index)
            && index > 0
            && index <= attachments.Count
                ? attachments[index - 1]
                : null;
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
}
