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
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Выберите вложение"
        }).ConfigureAwait(false);

        if (result is null)
        {
            return [];
        }

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

            var contentType = string.IsNullOrWhiteSpace(result.ContentType)
                ? "application/octet-stream"
                : result.ContentType;
            var transcoded = await _mediaCodecService.TranscodeAsync(new MediaTranscodeRequest(tempPath, contentType, 25 * 1024 * 1024), cancellationToken)
                .ConfigureAwait(false);
            transcodedPath = transcoded.OutputPath;

            if (_attachmentFiles.IsEnabled)
            {
                await using var upload = File.OpenRead(transcoded.OutputPath);
                var uploaded = await _attachmentFiles.UploadAsync(
                    new AttachmentFileUpload(
                        fileName,
                        transcoded.ContentType,
                        upload),
                    cancellationToken).ConfigureAwait(false);
                return [uploaded];
            }

            return [AttachmentMetadata.Local(
                fileName,
                transcoded.ContentType,
                transcoded.SizeBytes)];
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
        var safe = string.IsNullOrWhiteSpace(fileName)
            ? "attachment"
            : fileName;

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
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
