using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;

#if ANDROID
using Android.Media;
#endif

namespace Deep.Client.Maui.Services;

public sealed class MauiVoiceMessageRecorder : IVoiceMessageRecorder
{
    private static readonly TimeSpan MinimumVoiceDuration = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan StopRecorderTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelRecorderTimeout = TimeSpan.FromSeconds(2);

    private readonly IAttachmentFileTransport attachmentFiles;

#if ANDROID
    private const int SampleRate = 16000;
    private const short ChannelCount = 1;
    private const short BitsPerSample = 16;
    private const int WavHeaderSize = 44;

    private AudioRecord? recorder;
    private CancellationTokenSource? recordingCancellation;
    private Task? recordingTask;
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

        var path = Path.Combine(FileSystem.CacheDirectory, $"voice-{Guid.NewGuid():N}.wav");
        AudioRecord? nextRecorder = null;
        CancellationTokenSource? nextCancellation = null;
        try
        {
            var bufferSize = AudioRecord.GetMinBufferSize(
                SampleRate,
                ChannelIn.Mono,
                Android.Media.Encoding.Pcm16bit);
            if (bufferSize <= 0)
            {
                throw new InvalidOperationException("Не удалось открыть микрофон для записи голосового сообщения.");
            }

            bufferSize = Math.Max(bufferSize, SampleRate / 2);
            nextRecorder = new AudioRecord(
                AudioSource.Mic,
                SampleRate,
                ChannelIn.Mono,
                Android.Media.Encoding.Pcm16bit,
                bufferSize);
            if (nextRecorder.State != State.Initialized)
            {
                throw new InvalidOperationException("Не удалось подготовить микрофон для записи голосового сообщения.");
            }

            nextCancellation = new CancellationTokenSource();
            nextRecorder.StartRecording();
            var nextTask = Task.Run(
                () => WriteWavAsync(nextRecorder, path, bufferSize, nextCancellation.Token),
                CancellationToken.None);

            recorder = nextRecorder;
            recordingCancellation = nextCancellation;
            recordingTask = nextTask;
            recordingPath = path;
            startedAt = DateTimeOffset.UtcNow;
            IsRecording = true;
        }
        catch
        {
            nextCancellation?.Cancel();
            nextCancellation?.Dispose();
            ReleaseRecorder(nextRecorder);
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
        var activeCancellation = recordingCancellation;
        var activeTask = recordingTask;
        recorder = null;
        recordingCancellation = null;
        recordingTask = null;
        recordingPath = null;
        IsRecording = false;

        await StopRecordingAsync(activeRecorder, activeCancellation, activeTask, duration, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (duration < MinimumVoiceDuration)
            {
                return null;
            }

            if (fileInfo.Length <= WavHeaderSize)
            {
                throw new InvalidOperationException("Не удалось записать звук с микрофона.");
            }

            await using var upload = File.OpenRead(path);
            var fileName = $"voice-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.wav";
            if (attachmentFiles.IsEnabled)
            {
                return await attachmentFiles.UploadAsync(
                    new AttachmentFileUpload(fileName, "audio/wav", upload, Duration: duration),
                    cancellationToken).ConfigureAwait(false);
            }

            return new AttachmentMetadata(
                Guid.NewGuid().ToString("n"),
                fileName,
                "audio/wav",
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

#if ANDROID
    private static async Task StopRecordingAsync(
        AudioRecord? activeRecorder,
        CancellationTokenSource? activeCancellation,
        Task? activeTask,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (activeRecorder is null || activeCancellation is null || activeTask is null)
        {
            return;
        }

        try
        {
            try
            {
                activeRecorder.Stop();
            }
            catch (Exception) when (duration < MinimumVoiceDuration)
            {
                activeCancellation.Cancel();
                return;
            }
            catch (Exception ex)
            {
                activeCancellation.Cancel();
                throw new InvalidOperationException("Не удалось сохранить голосовое сообщение.", ex);
            }

            activeCancellation.Cancel();
            var completed = await Task.WhenAny(activeTask, Task.Delay(StopRecorderTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (completed != activeTask)
            {
                throw new InvalidOperationException("Не удалось сохранить голосовое сообщение: запись не завершилась вовремя.");
            }

            await activeTask.ConfigureAwait(false);
        }
        finally
        {
            activeCancellation.Dispose();
            ReleaseRecorder(activeRecorder);
        }
    }

    private static async Task WriteWavAsync(
        AudioRecord activeRecorder,
        string path,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];
        await using var output = File.Create(path);
        WriteWavHeader(output, 0);
        long dataBytes = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = activeRecorder.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                dataBytes += read;
                continue;
            }

            if (read < 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                throw new InvalidOperationException("Не удалось прочитать звук с микрофона.");
            }

            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        output.Seek(0, SeekOrigin.Begin);
        WriteWavHeader(output, dataBytes);
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void WriteWavHeader(System.IO.Stream output, long dataLength)
    {
        var byteRate = SampleRate * ChannelCount * BitsPerSample / 8;
        var blockAlign = ChannelCount * BitsPerSample / 8;
        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);

        WriteAscii(writer, "RIFF");
        writer.Write((int)(dataLength + WavHeaderSize - 8));
        WriteAscii(writer, "WAVE");
        WriteAscii(writer, "fmt ");
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(ChannelCount);
        writer.Write(SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(BitsPerSample);
        WriteAscii(writer, "data");
        writer.Write((int)dataLength);
    }

    private static void WriteAscii(BinaryWriter writer, string value)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(value));
    }

    private static void ReleaseRecorder(AudioRecord? activeRecorder)
    {
        try
        {
            activeRecorder?.Release();
        }
        catch
        {
        }
    }
#endif

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
#if ANDROID
        var path = recordingPath;
        var activeRecorder = recorder;
        var activeCancellation = recordingCancellation;
        var activeTask = recordingTask;
        recorder = null;
        recordingCancellation = null;
        recordingTask = null;
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
            activeCancellation?.Cancel();
            if (activeTask is not null)
            {
                try
                {
                    var completed = await Task.WhenAny(
                            activeTask,
                            Task.Delay(CancelRecorderTimeout, cancellationToken))
                        .ConfigureAwait(false);
                    if (completed == activeTask)
                    {
                        await activeTask.ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }

            activeCancellation?.Dispose();
            ReleaseRecorder(activeRecorder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                TryDelete(path);
            }
        }
#else
        IsRecording = false;
        await Task.CompletedTask;
#endif
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
