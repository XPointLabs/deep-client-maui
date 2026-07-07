using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.ApplicationModel;

#if ANDROID
using AndroidMediaPlayer = Android.Media.MediaPlayer;
#elif WINDOWS
using WindowsMediaPlayer = Windows.Media.Playback.MediaPlayer;
using WindowsMediaSource = Windows.Media.Core.MediaSource;
using WindowsStorageFile = Windows.Storage.StorageFile;
#endif

namespace Deep.Client.Maui.Services;

public sealed class VoiceMessagePlaybackService : IDisposable
{
#if ANDROID
    private AndroidMediaPlayer? player;
#elif WINDOWS
    private WindowsMediaPlayer? player;
#endif
    private string? playingAttachmentId;

    public async Task ToggleAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(playingAttachmentId, attachment.AttachmentId, StringComparison.Ordinal))
        {
            Stop();
            return;
        }

        Stop();
        var file = await AttachmentOpenService.DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken)
            .ConfigureAwait(false);

#if ANDROID
        var next = new AndroidMediaPlayer();
        next.SetDataSource(file.Path);
        next.Completion += (_, _) => Stop();
        next.Prepare();
        player = next;
        playingAttachmentId = attachment.AttachmentId;
        next.Start();
#elif WINDOWS
        var next = new WindowsMediaPlayer();
        var storageFile = await WindowsStorageFile.GetFileFromPathAsync(file.Path);
        next.Source = WindowsMediaSource.CreateFromStorageFile(storageFile);
        next.MediaEnded += (_, _) => Stop();
        player = next;
        playingAttachmentId = attachment.AttachmentId;
        next.Play();
#else
        await Launcher.Default.OpenAsync(new OpenFileRequest(
            file.FileName,
            new ReadOnlyFile(file.Path, file.ContentType))).ConfigureAwait(false);
#endif
    }

    public void Stop()
    {
#if ANDROID
        var active = player;
        player = null;
        playingAttachmentId = null;
        if (active is null)
        {
            return;
        }

        try
        {
            if (active.IsPlaying)
            {
                active.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            active.Release();
            active.Dispose();
        }
#elif WINDOWS
        var active = player;
        player = null;
        playingAttachmentId = null;
        if (active is null)
        {
            return;
        }

        active.Pause();
        active.Source = null;
        active.Dispose();
#else
        playingAttachmentId = null;
#endif
    }

    public void Dispose() => Stop();
}
