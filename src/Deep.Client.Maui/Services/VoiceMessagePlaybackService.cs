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

public sealed record VoicePlaybackSnapshot(
    string? AttachmentId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration)
{
    public static readonly VoicePlaybackSnapshot Stopped = new(null, false, TimeSpan.Zero, TimeSpan.Zero);

    public double Progress => Duration.TotalMilliseconds <= 0
        ? 0
        : Math.Clamp(Position.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
}

public sealed class VoiceMessagePlaybackService : IDisposable
{
    private readonly object playbackGate = new();
#if ANDROID
    private AndroidMediaPlayer? player;
    private AndroidPlaybackSession? playbackSession;
    private CancellationTokenSource? playbackCancellation;
#elif WINDOWS
    private WindowsMediaPlayer? player;
#endif
    private string? playingAttachmentId;
    private TimeSpan playingDuration;
    private int playbackGeneration;

    public VoicePlaybackSnapshot Snapshot => GetSnapshot();

    public async Task ToggleAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        bool stopCurrent;
        lock (playbackGate)
        {
            stopCurrent = string.Equals(playingAttachmentId, attachment.AttachmentId, StringComparison.Ordinal);
        }

        if (stopCurrent)
        {
            Stop();
            return;
        }

        Stop();
        var generation = Interlocked.Increment(ref playbackGeneration);
        var file = await AttachmentOpenService.DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken)
            .ConfigureAwait(false);

        if (generation != Volatile.Read(ref playbackGeneration))
        {
            return;
        }

#if ANDROID
        await StartAndroidPlaybackAsync(file, attachment, generation, cancellationToken).ConfigureAwait(false);
#elif WINDOWS
        var next = new WindowsMediaPlayer();
        var storageFile = await WindowsStorageFile.GetFileFromPathAsync(file.Path);
        next.Source = WindowsMediaSource.CreateFromStorageFile(storageFile);
        next.MediaEnded += (_, _) => Stop();
        lock (playbackGate)
        {
            if (generation != Volatile.Read(ref playbackGeneration))
            {
                next.Dispose();
                return;
            }

            player = next;
            playingAttachmentId = attachment.AttachmentId;
            playingDuration = attachment.Duration ?? TimeSpan.Zero;
            next.Play();
        }
#else
        playingDuration = attachment.Duration ?? TimeSpan.Zero;
        await Launcher.Default.OpenAsync(new OpenFileRequest(
            file.FileName,
            new ReadOnlyFile(file.Path, file.ContentType))).ConfigureAwait(false);
#endif
    }

    public void Stop()
    {
#if ANDROID
        AndroidPlaybackSession? activeSession;
        AndroidMediaPlayer? active;
        CancellationTokenSource? activeCancellation;
        lock (playbackGate)
        {
            Interlocked.Increment(ref playbackGeneration);
            activeSession = playbackSession;
            active = player;
            activeCancellation = playbackCancellation;
            playbackSession = null;
            playbackCancellation = null;
            player = null;
            playingAttachmentId = null;
            playingDuration = TimeSpan.Zero;
        }

        try
        {
            activeCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (activeSession is not null)
        {
            activeSession.Dispose();
        }
        else if (active is not null)
        {
            try
            {
                active.Release();
            }
            catch (Exception)
            {
            }
            finally
            {
                active.Dispose();
            }
        }
#elif WINDOWS
        WindowsMediaPlayer? active;
        lock (playbackGate)
        {
            Interlocked.Increment(ref playbackGeneration);
            active = player;
            player = null;
            playingAttachmentId = null;
            playingDuration = TimeSpan.Zero;
        }

        if (active is null)
        {
            return;
        }

        active.Pause();
        active.Source = null;
        active.Dispose();
#else
        lock (playbackGate)
        {
            Interlocked.Increment(ref playbackGeneration);
            playingAttachmentId = null;
            playingDuration = TimeSpan.Zero;
        }
#endif
    }

    public void Dispose() => Stop();

#if ANDROID
    private async Task StartAndroidPlaybackAsync(
        PreparedAttachmentFile file,
        AttachmentMetadata attachment,
        int generation,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new AndroidPlaybackSession();
        try
        {
            lock (playbackGate)
            {
                if (generation != Volatile.Read(ref playbackGeneration))
                {
                    return;
                }

                playbackSession = session;
                playbackCancellation = linkedCancellation;
                player = session.Player;
                playingAttachmentId = attachment.AttachmentId;
                playingDuration = TimeSpan.Zero;

                session.Completed += (_, _) => CompleteAndroidPlayback(session, generation);
                session.BeginPrepareAsync(file.Path);
            }

            await session.Prepared.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);

            lock (playbackGate)
            {
                if (generation != Volatile.Read(ref playbackGeneration)
                    || !ReferenceEquals(playbackSession, session))
                {
                    return;
                }

                playingDuration = TimeSpan.FromMilliseconds(Math.Max(0, session.Player.Duration));
                session.Start();
            }
        }
        catch
        {
            var invalidated = false;
            var detached = false;
            lock (playbackGate)
            {
                invalidated = generation != Volatile.Read(ref playbackGeneration)
                    || !ReferenceEquals(playbackSession, session);
                if (ReferenceEquals(playbackSession, session))
                {
                    playbackSession = null;
                    playbackCancellation = null;
                    player = null;
                    playingAttachmentId = null;
                    playingDuration = TimeSpan.Zero;
                    detached = true;
                }
            }

            if (detached)
            {
                session.Dispose();
            }

            if (invalidated)
            {
                return;
            }

            throw;
        }
        finally
        {
            var retained = false;
            lock (playbackGate)
            {
                if (ReferenceEquals(playbackCancellation, linkedCancellation))
                {
                    playbackCancellation = null;
                }

                retained = ReferenceEquals(playbackSession, session);
            }

            if (!retained)
            {
                session.Dispose();
            }
        }
    }

    private void CompleteAndroidPlayback(AndroidPlaybackSession session, int generation)
    {
        var detached = false;
        lock (playbackGate)
        {
            if (generation == Volatile.Read(ref playbackGeneration)
                && ReferenceEquals(playbackSession, session))
            {
                playbackSession = null;
                playbackCancellation = null;
                player = null;
                playingAttachmentId = null;
                playingDuration = TimeSpan.Zero;
                detached = true;
            }
        }

        if (detached)
        {
            session.Dispose();
        }
    }

    private sealed class AndroidPlaybackSession : IDisposable
    {
        private readonly EventHandler preparedHandler;
        private readonly EventHandler<AndroidMediaPlayer.ErrorEventArgs> errorHandler;
        private readonly EventHandler completionHandler;
        private int disposed;
        private int started;

        public AndroidPlaybackSession()
        {
            Player = new AndroidMediaPlayer();
            Prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            preparedHandler = (_, _) => Prepared.TrySetResult(true);
            errorHandler = (_, _) =>
            {
                Volatile.Write(ref started, 0);
                Prepared.TrySetException(
                    new InvalidOperationException("Android media playback preparation failed."));
            };
            completionHandler = (_, _) =>
            {
                Volatile.Write(ref started, 0);
                Completed?.Invoke(this, EventArgs.Empty);
            };
            Player.Prepared += preparedHandler;
            Player.Error += errorHandler;
            Player.Completion += completionHandler;
        }

        public AndroidMediaPlayer Player { get; }

        public TaskCompletionSource<bool> Prepared { get; }

        public bool HasStarted => Volatile.Read(ref started) != 0;

        public event EventHandler? Completed;

        public void BeginPrepareAsync(string path)
        {
            Player.SetDataSource(path);
            Player.PrepareAsync();
        }

        public void Start()
        {
            Volatile.Write(ref started, 1);
            try
            {
                Player.Start();
            }
            catch
            {
                Volatile.Write(ref started, 0);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref started, 0);

            Player.Prepared -= preparedHandler;
            Player.Error -= errorHandler;
            Player.Completion -= completionHandler;
            try
            {
                if (Player.IsPlaying)
                {
                    Player.Stop();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    Player.Release();
                }
                catch (Exception)
                {
                }

                try
                {
                    Player.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }
#endif

    private VoicePlaybackSnapshot GetSnapshot()
    {
        lock (playbackGate)
        {
            var attachmentId = playingAttachmentId;
            if (string.IsNullOrWhiteSpace(attachmentId))
            {
                return VoicePlaybackSnapshot.Stopped;
            }

#if ANDROID
            var active = player;
            var activeSession = playbackSession;
            if (active is null || activeSession is null)
            {
                return VoicePlaybackSnapshot.Stopped;
            }

            try
            {
                var duration = TimeSpan.FromMilliseconds(Math.Max(0, active.Duration));
                if (duration <= TimeSpan.Zero)
                {
                    duration = playingDuration;
                }

                return new VoicePlaybackSnapshot(
                    attachmentId,
                    activeSession.HasStarted,
                    TimeSpan.FromMilliseconds(Math.Max(0, active.CurrentPosition)),
                    duration);
            }
            catch (Exception)
            {
                return VoicePlaybackSnapshot.Stopped;
            }
#elif WINDOWS
            var active = player;
            if (active is null)
            {
                return VoicePlaybackSnapshot.Stopped;
            }

            var duration = active.PlaybackSession.NaturalDuration;
            if (duration <= TimeSpan.Zero)
            {
                duration = playingDuration;
            }

            return new VoicePlaybackSnapshot(
                attachmentId,
                active.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing,
                active.PlaybackSession.Position,
                duration);
#else
            return new VoicePlaybackSnapshot(attachmentId, false, TimeSpan.Zero, playingDuration);
#endif
        }
    }
}
