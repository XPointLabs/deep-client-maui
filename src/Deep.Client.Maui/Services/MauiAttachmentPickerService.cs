using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public sealed class MauiAttachmentPickerService : ITypedAttachmentPickerService
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

        return choice switch
        {
            "Фото из галереи" => await PickAsync(AttachmentPickKind.Photo, cancellationToken).ConfigureAwait(false),
            "Видео из галереи" => await PickAsync(AttachmentPickKind.Video, cancellationToken).ConfigureAwait(false),
            "Файл" => await PickAsync(AttachmentPickKind.File, cancellationToken).ConfigureAwait(false),
            _ => []
        };
    }

    public async Task<IReadOnlyList<AttachmentMetadata>> PickAsync(
        AttachmentPickKind kind,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileResult> results = kind switch
        {
            AttachmentPickKind.Photo => await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = "Выберите фото"
            }).ConfigureAwait(false),
            AttachmentPickKind.Video => await MediaPicker.Default.PickVideosAsync(new MediaPickerOptions
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
            attachments.Add(await PrepareAsync(result, kind, cancellationToken).ConfigureAwait(false));
        }

        return attachments;
    }

    private async Task<AttachmentMetadata> PrepareAsync(
        FileResult result,
        AttachmentPickKind kind,
        CancellationToken cancellationToken)
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
            if (kind == AttachmentPickKind.File)
            {
                return await CreateAttachmentAsync(tempPath, fileName, contentType, cancellationToken).ConfigureAwait(false);
            }

            if (kind == AttachmentPickKind.Photo)
            {
                contentType = "image/jpeg";
                fileName = WithExtension(fileName, ".jpg");
            }

            var transcoded = await mediaCodecService.TranscodeAsync(
                    new MediaTranscodeRequest(tempPath, contentType, MaxAttachmentBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            transcodedPath = transcoded.OutputPath;

            return await CreateAttachmentAsync(transcoded.OutputPath, fileName, transcoded.ContentType, cancellationToken)
                .ConfigureAwait(false);
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

    private async Task<AttachmentMetadata> CreateAttachmentAsync(
        string path,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        var size = new FileInfo(path).Length;
        if (MaxAttachmentBytes > 0 && size > MaxAttachmentBytes)
        {
            throw new InvalidOperationException($"Вложение превышает лимит {MaxAttachmentBytes} байт.");
        }

        if (attachmentFiles.IsEnabled)
        {
            await using var upload = File.OpenRead(path);
            return await attachmentFiles.UploadAsync(
                new AttachmentFileUpload(fileName, contentType, upload),
                cancellationToken).ConfigureAwait(false);
        }

        return AttachmentMetadata.Local(fileName, contentType, size);
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

    private static string WithExtension(string fileName, string extension)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(withoutExtension)
            ? $"photo{extension}"
            : $"{withoutExtension}{extension}";
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
