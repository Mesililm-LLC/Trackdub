using Trackdub.Contracts;

#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
#endif

namespace Trackdub.Media.Playback;

#if WINDOWS

public sealed class MediaFoundationAudioPreviewTransport : IAudioPreviewTransport
{
    private MediaPlayer? mediaPlayer;
    private FileStream? audioFile;
    private IRandomAccessStream? audioStream;
    private MediaSource? audioSource;
    private bool isEnded;

    public event EventHandler? Ended;

    public async Task OpenAsync(string absoluteFilePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFilePath);

        await StopAsync(ct).ConfigureAwait(false);

        MediaPlayer player = CreatePlayer();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMediaOpened(MediaPlayer sender, object args)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            tcs.TrySetResult(true);
        }

        void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            tcs.TrySetException(new InvalidOperationException(
                $"Audio preview media failed to open: {args.ErrorMessage} ({args.Error})."));
        }

        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;

        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            tcs.TrySetCanceled(ct);
        });

        try
        {
            // Open through .NET: Media Foundation's file-URI resolver rejects valid long paths.
            audioFile = File.Open(absoluteFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            audioStream = audioFile.AsRandomAccessStream();
            audioSource = MediaSource.CreateFromStream(audioStream, GetContentType(absoluteFilePath));
            player.Source = audioSource;

            await tcs.Task.ConfigureAwait(false);

            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
            }

            player.PlaybackSession.Position = TimeSpan.Zero;
            isEnded = false;
        }
        catch
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;

            if (ReferenceEquals(mediaPlayer, player))
            {
                DisposePlayer();
            }
            else
            {
                player.Dispose();
            }
            throw;
        }
    }

    public Task PlayAsync(CancellationToken ct)
    {
        isEnded = false;
        mediaPlayer?.Play();
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        mediaPlayer?.Pause();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        MediaPlayer? player = mediaPlayer;
        if (player is not null)
        {
            player.PlaybackSession.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            isEnded = false;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        DisposePlayer();
        return Task.CompletedTask;
    }

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        MediaPlayer? player = mediaPlayer;
        if (player is null)
        {
            return Task.FromResult(AudioPreviewSnapshot.Empty);
        }

        MediaPlaybackSession session = player.PlaybackSession;
        TimeSpan position = session.Position;
        TimeSpan duration = session.NaturalDuration;
        TimeSpan endTolerance = TimeSpan.FromMilliseconds(250);
        bool reachedEnd = duration > TimeSpan.Zero && position > TimeSpan.Zero && position >= duration - endTolerance;
        bool ended = isEnded || reachedEnd;
        return Task.FromResult(new AudioPreviewSnapshot(
            IsLoaded: true,
            IsPlaying: session.PlaybackState == MediaPlaybackState.Playing,
            Position: position,
            Duration: duration,
            IsEnded: ended,
            WarningMessage: null));
    }

    public void Dispose()
    {
        DisposePlayer();
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer
        {
            AutoPlay = false,
            IsLoopingEnabled = false
        };
        player.MediaEnded += MediaPlayer_MediaEnded;
        mediaPlayer = player;
        isEnded = false;
        return player;
    }

    private void DisposePlayer()
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            mediaPlayer.Dispose();
            mediaPlayer = null;
        }

        audioSource?.Dispose();
        audioSource = null;
        audioStream?.Dispose();
        audioStream = null;
        audioFile?.Dispose();
        audioFile = null;
        isEnded = false;
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" or ".wave" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg" or ".opus" => "audio/ogg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".wma" => "audio/x-ms-wma",
        ".aif" or ".aiff" => "audio/aiff",
        _ => "application/octet-stream"
    };

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        isEnded = true;
        Ended?.Invoke(this, EventArgs.Empty);
    }
}

#else

public sealed class MediaFoundationAudioPreviewTransport : IAudioPreviewTransport
{
#pragma warning disable CS0067
    public event EventHandler? Ended;
#pragma warning restore CS0067

    public Task OpenAsync(string absoluteFilePath, CancellationToken ct) =>
        throw new PlatformNotSupportedException("Audio preview requires Windows.");

    public Task PlayAsync(CancellationToken ct) =>
        throw new PlatformNotSupportedException("Audio preview requires Windows.");

    public Task PauseAsync(CancellationToken ct) =>
        Task.CompletedTask;

    public Task SeekAsync(TimeSpan position, CancellationToken ct) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) =>
        Task.CompletedTask;

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(AudioPreviewSnapshot.Empty);

    public void Dispose() { }
}

#endif
