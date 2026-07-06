using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;

#if ANDROID
using Android.Media;
#endif

namespace Deep.Client.Maui.Services;

public sealed class MauiVoiceMessageRecorder : IVoiceMessageRecorder
{
    private readonly IAttachmentFileTransport attachmentFiles;

#if ANDROID
    private MediaRecorder? recorder;
    private string? recordingPath;
    private DateTimeOffset startedAt;
#endif

    public MauiVoiceMessageRecorder(IAttachmentFileTransport attachmentFiles)
    {
        this.attachmentFiles = attachmentFiles;
    }

    public bool IsSupported => DeviceInfo.Current.Platform == DevicePlatform.Android;

    public bool IsRecording { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException("Голосовые сообщения пока доступны только на Android.");
        }

        if (IsRecording)
        {
            return;
        }

#if ANDROID
        await EnsureMicrophonePermissionAsync().ConfigureAwait(false);

        var path = Path.Combine(FileSystem.CacheDirectory, $"voice-{Guid.NewGuid():N}.m4a");
        MediaRecorder? nextRecorder = null;
        try
        {
#pragma warning disable CA1422
            nextRecorder = new MediaRecorder();
#pragma warning restore CA1422
            nextRecorder.SetAudioSource(AudioSource.Mic);
            nextRecorder.SetOutputFormat(OutputFormat.Mpeg4);
            nextRecorder.SetAudioEncoder(AudioEncoder.Aac);
            nextRecorder.SetAudioEncodingBitRate(64000);
            nextRecorder.SetAudioSamplingRate(44100);
            nextRecorder.SetOutputFile(path);
            nextRecorder.Prepare();
            nextRecorder.Start();

            recorder = nextRecorder;
            recordingPath = path;
            startedAt = DateTimeOffset.UtcNow;
            IsRecording = true;
        }
        catch
        {
            nextRecorder?.Release();
            TryDelete(path);
            throw;
        }
#else
        await Task.CompletedTask;
#endif
    }

    public async Task<AttachmentMetadata?> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return null;
        }

#if ANDROID
        var path = recordingPath;
        var duration = DateTimeOffset.UtcNow - startedAt;
        var activeRecorder = recorder;
        recorder = null;
        recordingPath = null;
        IsRecording = false;

        try
        {
            activeRecorder?.Stop();
        }
        catch (Exception) when (duration < TimeSpan.FromMilliseconds(700))
        {
            return null;
        }
        finally
        {
            activeRecorder?.Reset();
            activeRecorder?.Release();
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (duration < TimeSpan.FromMilliseconds(700) || fileInfo.Length == 0)
            {
                return null;
            }

            await using var upload = File.OpenRead(path);
            var fileName = $"voice-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.m4a";
            if (attachmentFiles.IsEnabled)
            {
                return await attachmentFiles.UploadAsync(
                    new AttachmentFileUpload(fileName, "audio/mp4", upload, Duration: duration),
                    cancellationToken).ConfigureAwait(false);
            }

            return new AttachmentMetadata(
                Guid.NewGuid().ToString("n"),
                fileName,
                "audio/mp4",
                fileInfo.Length,
                Duration: duration);
        }
        finally
        {
            TryDelete(path);
        }
#else
        await Task.CompletedTask;
        IsRecording = false;
        return null;
#endif
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
#if ANDROID
        var path = recordingPath;
        var activeRecorder = recorder;
        recorder = null;
        recordingPath = null;
        IsRecording = false;

        try
        {
            activeRecorder?.Stop();
        }
        catch
        {
        }
        finally
        {
            activeRecorder?.Reset();
            activeRecorder?.Release();
            if (!string.IsNullOrWhiteSpace(path))
            {
                TryDelete(path);
            }
        }
#else
        IsRecording = false;
#endif

        return Task.CompletedTask;
    }

    private static async Task EnsureMicrophonePermissionAsync()
    {
        var status = await MainThread.InvokeOnMainThreadAsync(Permissions.CheckStatusAsync<Permissions.Microphone>)
            .ConfigureAwait(false);
        if (status != PermissionStatus.Granted)
        {
            status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Microphone>)
                .ConfigureAwait(false);
        }

        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Разрешите доступ к микрофону, чтобы отправлять голосовые сообщения.");
        }
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
            // Cache cleanup can remove locked temp files later.
        }
    }
}
