using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public sealed class MauiAttachmentPickerService : IAttachmentPickerService
{
    private readonly IMediaCodecService _mediaCodecService;
    private readonly IAttachmentFileTransport _attachmentFiles;

    public MauiAttachmentPickerService(
        IMediaCodecService mediaCodecService,
        IAttachmentFileTransport attachmentFiles)
    {
        _mediaCodecService = mediaCodecService;
        _attachmentFiles = attachmentFiles;
    }

    public async Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default)
    {
        var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Pick attachments"
        }).ConfigureAwait(false);

        if (results is null)
        {
            return [];
        }

        var attachments = new List<AttachmentMetadata>();
        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            var fileName = SafeFileName(result.FileName);
            await using var stream = await result.OpenReadAsync().ConfigureAwait(false);
            var tempPath = Path.Combine(FileSystem.CacheDirectory, $"pick-{Guid.NewGuid():N}-{fileName}");
            await using (var output = File.Create(tempPath))
            {
                await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var contentType = string.IsNullOrWhiteSpace(result.ContentType)
                ? "application/octet-stream"
                : result.ContentType;
            var transcoded = await _mediaCodecService.TranscodeAsync(new MediaTranscodeRequest(tempPath, contentType, 25 * 1024 * 1024), cancellationToken)
                .ConfigureAwait(false);

            if (_attachmentFiles.IsEnabled)
            {
                await using var upload = File.OpenRead(transcoded.OutputPath);
                attachments.Add(await _attachmentFiles.UploadAsync(
                    new AttachmentFileUpload(
                        fileName,
                        transcoded.ContentType,
                        upload),
                    cancellationToken).ConfigureAwait(false));
                continue;
            }

            attachments.Add(AttachmentMetadata.Local(
                fileName,
                transcoded.ContentType,
                transcoded.SizeBytes));
        }

        return attachments;
    }

    private static string SafeFileName(string? fileName)
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
