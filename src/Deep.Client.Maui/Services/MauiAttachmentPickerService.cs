using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public sealed class MauiAttachmentPickerService : IAttachmentPickerService
{
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;

    private readonly IMediaCodecService mediaCodecService;
    private readonly IAttachmentFileTransport attachmentFiles;

    public MauiAttachmentPickerService(
        IMediaCodecService mediaCodecService,
        IAttachmentFileTransport attachmentFiles)
    {
        this.mediaCodecService = mediaCodecService;
        this.attachmentFiles = attachmentFiles;
    }

    public async Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default)
    {
        var choice = await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayActionSheetAsync(
            "Добавить вложение",
            "Отмена",
            null,
            "Фото из галереи",
            "Видео из галереи",
            "Файл"));

        if (string.IsNullOrWhiteSpace(choice) || choice == "Отмена")
        {
            return [];
        }

        IReadOnlyList<FileResult> results = choice switch
        {
            "Фото из галереи" => await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = "Выберите фото"
            }).ConfigureAwait(false),
            "Видео из галереи" => await MediaPicker.Default.PickVideosAsync(new MediaPickerOptions
            {
                Title = "Выберите видео"
            }).ConfigureAwait(false),
            _ => (await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Выберите файлы"
            }).ConfigureAwait(false))
                .Where(static result => result is not null)
                .Cast<FileResult>()
                .ToArray()
        };

        if (results.Count == 0)
        {
            return [];
        }

        var attachments = new List<AttachmentMetadata>(results.Count);
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attachments.Add(await PrepareAsync(result, cancellationToken).ConfigureAwait(false));
        }

        return attachments;
    }

    private async Task<AttachmentMetadata> PrepareAsync(FileResult result, CancellationToken cancellationToken)
    {
        var fileName = SafeFileName(result.FileName);
        var tempPath = Path.Combine(FileSystem.CacheDirectory, $"pick-{Guid.NewGuid():N}-{fileName}");
        string? transcodedPath = null;
        try
        {
            await using (var stream = await result.OpenReadAsync().ConfigureAwait(false))
            await using (var output = File.Create(tempPath))
            {
                await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var contentType = ResolveContentType(result.ContentType, fileName);
            var transcoded = await mediaCodecService.TranscodeAsync(
                    new MediaTranscodeRequest(tempPath, contentType, MaxAttachmentBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            transcodedPath = transcoded.OutputPath;

            if (attachmentFiles.IsEnabled)
            {
                await using var upload = File.OpenRead(transcoded.OutputPath);
                return await attachmentFiles.UploadAsync(
                    new AttachmentFileUpload(fileName, transcoded.ContentType, upload),
                    cancellationToken).ConfigureAwait(false);
            }

            return AttachmentMetadata.Local(fileName, transcoded.ContentType, transcoded.SizeBytes);
        }
        finally
        {
            TryDelete(tempPath);
            if (!string.IsNullOrWhiteSpace(transcodedPath))
            {
                TryDelete(transcodedPath);
            }
        }
    }

    private static string SafeFileName(string? fileName)
    {
        var safe = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
    }

    private static string ResolveContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType) && contentType.Contains('/', StringComparison.Ordinal))
        {
            return contentType;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
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
            // The OS cache cleaner will remove a file that is temporarily locked.
        }
    }
}
