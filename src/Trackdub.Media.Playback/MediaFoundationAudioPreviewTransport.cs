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
    private readonly object playbackGate = new();
    private readonly Func<string, FileStream> openFile;
    private PreviewSession? current;
    private CancellationTokenSource? pendingOpen;
    private bool isEnded;

    public MediaFoundationAudioPreviewTransport()
        : this(path => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }

    internal MediaFoundationAudioPreviewTransport(Func<string, FileStream> openFile) => this.openFile = openFile;

    public event EventHandler? Ended;

    public async Task OpenAsync(string absoluteFilePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFilePath);
        ct.ThrowIfCancellationRequested();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = request.Token;
        lock (playbackGate)
        {
            token.ThrowIfCancellationRequested();
            pendingOpen?.Cancel();
            pendingOpen = request;
            DisposePlayer();
        }

        // File/cloud hydration and Media Foundation creation must not block Stop or the UI.
        Task<PreviewSession> creation = Task.Run(() => PreviewSession.Create(absoluteFilePath, openFile), token);
        PreviewSession? session = null;
        try
        {
            session = await creation.WaitAsync(token).ConfigureAwait(false);
            lock (playbackGate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(pendingOpen, request))
                    throw new OperationCanceledException("Audio preview open was superseded.", token);
                current = session;
                session.Player.MediaEnded += MediaPlayer_MediaEnded;
                session.Player.Source = session.Source;
            }

            await session.Opened.Task.WaitAsync(token).ConfigureAwait(false);
            lock (playbackGate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(current, session))
                    throw new OperationCanceledException("Audio preview open was superseded.", token);
                if (session.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    session.Player.Pause();
                session.Player.PlaybackSession.Position = TimeSpan.Zero;
                isEnded = false;
            }
        }
        catch
        {
            if (session is null)
            {
                // Synchronous file opening cannot be interrupted. Release any late result.
                _ = DisposeWhenCreatedAsync(creation);
            }
            else
            {
                lock (playbackGate)
                {
                    if (ReferenceEquals(current, session)) DisposePlayer();
                    else session.Dispose();
                }
            }
            throw;
        }
        finally
        {
            lock (playbackGate)
            {
                if (ReferenceEquals(pendingOpen, request)) pendingOpen = null;
            }
        }
    }

    private static async Task DisposeWhenCreatedAsync(Task<PreviewSession> creation)
    {
        try { (await creation.ConfigureAwait(false)).Dispose(); }
        catch
        {
            // The opening request has already reported cancellation or creation failure.
        }
    }

    public Task PlayAsync(CancellationToken ct)
    {
        lock (playbackGate)
        {
            isEnded = false;
            current?.Player.Play();
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        lock (playbackGate) current?.Player.Pause();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        lock (playbackGate)
        {
            if (current is { } session)
            {
                session.Player.PlaybackSession.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                isEnded = false;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (playbackGate)
        {
            pendingOpen?.Cancel();
            pendingOpen = null;
            DisposePlayer();
        }
        return Task.CompletedTask;
    }

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        lock (playbackGate)
        {
            if (current is null) return Task.FromResult(AudioPreviewSnapshot.Empty);
            MediaPlaybackSession session = current.Player.PlaybackSession;
            TimeSpan position = session.Position;
            TimeSpan duration = session.NaturalDuration;
            bool reachedEnd = duration > TimeSpan.Zero && position > TimeSpan.Zero
                && position >= duration - TimeSpan.FromMilliseconds(250);
            return Task.FromResult(new AudioPreviewSnapshot(
                IsLoaded: true,
                IsPlaying: session.PlaybackState == MediaPlaybackState.Playing,
                Position: position,
                Duration: duration,
                IsEnded: isEnded || reachedEnd,
                WarningMessage: null));
        }
    }

    public void Dispose()
    {
        lock (playbackGate)
        {
            pendingOpen?.Cancel();
            pendingOpen = null;
            DisposePlayer();
        }
    }

    // All active-session transitions and end-state reads/writes use playbackGate.
    private void DisposePlayer()
    {
        PreviewSession? previous = current;
        current = null;
        isEnded = false;
        if (previous is null) return;
        previous.Player.MediaEnded -= MediaPlayer_MediaEnded;
        previous.Dispose();
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        lock (playbackGate)
        {
            if (!ReferenceEquals(current?.Player, sender)) return;
            isEnded = true;
        }
        Ended?.Invoke(this, EventArgs.Empty);
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

    // Each open owns its handles even after a newer request replaces the active session.
    private sealed class PreviewSession : IDisposable
    {
        private readonly FileStream file;
        private readonly IRandomAccessStream stream;
        private int disposed;

        private PreviewSession(MediaPlayer player, MediaSource source, IRandomAccessStream stream, FileStream file)
        {
            Player = player;
            Source = source;
            this.stream = stream;
            this.file = file;
            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;
        }

        public MediaPlayer Player { get; }
        public MediaSource Source { get; }
        public TaskCompletionSource<bool> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static PreviewSession Create(string path, Func<string, FileStream> openFile)
        {
            // The file-URI resolver rejects valid long paths; .NET stream opening does not.
            FileStream file = openFile(path);
            IRandomAccessStream? stream = null;
            MediaSource? source = null;
            MediaPlayer? player = null;
            try
            {
                stream = file.AsRandomAccessStream();
                source = MediaSource.CreateFromStream(stream, GetContentType(path));
                player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
                return new PreviewSession(player, source, stream, file);
            }
            catch
            {
                player?.Dispose();
                source?.Dispose();
                stream?.Dispose();
                file.Dispose();
                throw;
            }
        }

        private void OnOpened(MediaPlayer sender, object args)
        {
            DetachOpenEvents();
            Opened.TrySetResult(true);
        }

        private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DetachOpenEvents();
            Opened.TrySetException(new InvalidOperationException(
                $"Audio preview media failed to open: {args.ErrorMessage} ({args.Error})."));
        }

        private void DetachOpenEvents()
        {
            Player.MediaOpened -= OnOpened;
            Player.MediaFailed -= OnFailed;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Opened.TrySetCanceled();
            DetachOpenEvents();
            try { Player.Dispose(); }
            finally
            {
                try { Source.Dispose(); }
                finally
                {
                    try { stream.Dispose(); }
                    finally { file.Dispose(); }
                }
            }
        }
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
