using System.IO.Pipes;
using System.Text;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class MaxineLipSyncHostSessionTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const int FrameBytes = Width * Height * 3;

    [Fact]
    public async Task Connect_returns_null_and_disposes_a_host_that_does_not_shake_hands()
    {
        using var host = new ScriptedHost(async (_, output, _) =>
            await output.WriteAsync("Unknown flag: \"--model_path\"\n"u8.ToArray()));

        MaxineLipSyncHostSession? session =
            await MaxineLipSyncHostSession.ConnectAsync(host, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(session);
        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task Run_streams_frames_through_a_host_that_lags_behind_its_input()
    {
        // Holding back output until several frames are in mirrors LipSync's NumInitialFrames, and the
        // frames are larger than a pipe buffer, so writing everything before reading would hang.
        using var host = ScriptedHost.Echo(lagFrames: 5);
        using MaxineLipSyncHostSession session = await ConnectAsync(host);
        byte[] source = Frames(12);
        using var sink = new MemoryStream();

        MaxineLipSyncHostReply reply = await session.RunAsync(Job(12), new MemoryStream(source), sink, TestTimeout());

        Assert.True(reply.Succeeded, reply.Message);
        Assert.Equal(Invert(source), sink.ToArray());
        Assert.StartsWith("RUN 64 48 29.97 3 12 ", host.Requests.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_repeats_the_last_source_frame_when_the_source_ends_early()
    {
        using var host = ScriptedHost.Echo(lagFrames: 0);
        using MaxineLipSyncHostSession session = await ConnectAsync(host);
        byte[] source = Frames(3);
        using var sink = new MemoryStream();

        MaxineLipSyncHostReply reply = await session.RunAsync(Job(5), new MemoryStream(source), sink, TestTimeout());

        Assert.True(reply.Succeeded);
        byte[] lastFrame = source[(2 * FrameBytes)..];
        byte[] expected = [.. source, .. lastFrame, .. lastFrame];
        Assert.Equal(Invert(expected), sink.ToArray());
    }

    [Fact]
    public async Task Run_reuses_one_session_for_consecutive_turns()
    {
        using var host = ScriptedHost.Echo(lagFrames: 2);
        using MaxineLipSyncHostSession session = await ConnectAsync(host);

        for (int turn = 0; turn < 3; turn++)
        {
            using var sink = new MemoryStream();
            MaxineLipSyncHostReply reply = await session.RunAsync(Job(4), new MemoryStream(Frames(4)), sink, TestTimeout());
            Assert.True(reply.Succeeded);
            Assert.Equal(4 * FrameBytes, sink.Length);
        }

        Assert.Equal(3, host.Requests.Count);
        Assert.True(session.IsAlive);
    }

    [Fact]
    public async Task Run_reports_a_host_err_reply_without_breaking_the_session()
    {
        using var host = ScriptedHost.Echo(lagFrames: 0, reply: "ERR NvAR_Load failed: model not found");
        using MaxineLipSyncHostSession session = await ConnectAsync(host);
        using var sink = new MemoryStream();

        MaxineLipSyncHostReply reply = await session.RunAsync(Job(2), new MemoryStream(Frames(2)), sink, TestTimeout());

        Assert.False(reply.Succeeded);
        Assert.Equal("NvAR_Load failed: model not found", reply.Message);
        Assert.True(session.IsAlive);
    }

    [Fact]
    public async Task Run_throws_a_protocol_error_when_the_host_exits_mid_turn()
    {
        using var host = new ScriptedHost(async (input, output, _) =>
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes(MaxineLipSyncHostSession.Handshake + "\n"));
            await ScriptedHost.ReadLineAsync(input);
            byte[] frame = new byte[FrameBytes];
            await input.ReadExactlyAsync(frame);
            await output.WriteAsync(frame);
            // Exits after one of four frames, like a host that crashed on the GPU.
        });
        using MaxineLipSyncHostSession session = await ConnectAsync(host);

        var error = await Assert.ThrowsAsync<MaxineLipSyncHostProtocolException>(() =>
            session.RunAsync(Job(4), new MemoryStream(Frames(4)), new MemoryStream(), TestTimeout()));

        Assert.Contains("LipSync host", error.Message);
    }

    [Fact]
    public async Task Run_kills_the_host_and_rethrows_when_the_caller_cancels()
    {
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new ScriptedHost(async (input, output, token) =>
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes(MaxineLipSyncHostSession.Handshake + "\n"));
            await ScriptedHost.ReadLineAsync(input);
            requestSeen.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using MaxineLipSyncHostSession session = await ConnectAsync(host);
        using var cancellation = new CancellationTokenSource();

        Task run = session.RunAsync(Job(4), new MemoryStream(Frames(4)), new MemoryStream(), cancellation.Token);
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(host.Killed);
    }

    [Theory]
    [InlineData(4097, 48)]
    [InlineData(64, 4097)]
    public async Task Run_rejects_frames_the_host_would_refuse_without_reading(int width, int height)
    {
        using var host = ScriptedHost.Echo(lagFrames: 0);
        using MaxineLipSyncHostSession session = await ConnectAsync(host);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.RunAsync(
            Job(1) with { Width = width, Height = height }, new MemoryStream(), new MemoryStream(), TestTimeout()));
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData("TRACKDUB-MAXINE-LIPSYNC 1 READY\n", true)]
    [InlineData("TRACKDUB-MAXINE-LIPSYNC 1 READY\r\n", true)]
    [InlineData("Unknown flag: \"--protocol\"\n", false)]
    [InlineData("", false)]
    public void Engine_recognizes_the_persistent_host_by_its_handshake(string output, bool expected) =>
        Assert.Equal(expected, NvidiaMaxineLipSynthesisEngine.IsHandshake(output));

    [Theory]
    [InlineData("width=704\nheight=1280\n", 704, 1280)]
    [InlineData("width=1920\nheight=1080\nrotation=-90\n", 1080, 1920)]
    [InlineData("width=1920\nheight=1080\nrotation=180\n", 1920, 1080)]
    public void Frame_size_follows_rotation_metadata_like_ffmpeg_autorotate(string output, int width, int height) =>
        Assert.Equal((width, height), NvidiaMaxineLipSynthesisEngine.ParseDisplayFrameSize(output));

    private static async Task<MaxineLipSyncHostSession> ConnectAsync(ScriptedHost host) =>
        await MaxineLipSyncHostSession.ConnectAsync(host, TimeSpan.FromSeconds(5), CancellationToken.None)
        ?? throw new InvalidOperationException("No handshake.");

    private static MaxineLipSyncHostJob Job(int frames) =>
        new(Width, Height, 29.97, NvidiaMaxineLipSyncLanguage.Spanish, frames, Path.Join(Path.GetTempPath(), "turn 16k.wav"));

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static byte[] Frames(int count)
    {
        byte[] data = new byte[count * FrameBytes];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i / FrameBytes * 31 + i % 7);
        }

        return data;
    }

    private static byte[] Invert(byte[] data) => data.Select(value => (byte)~value).ToArray();

    /// <summary>A host process stand-in that runs a script over two anonymous pipes.</summary>
    private sealed class ScriptedHost : IPipedProcess
    {
        private readonly AnonymousPipeServerStream toHost = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream hostInput;
        private readonly AnonymousPipeServerStream fromHost = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream hostOutput;
        private readonly CancellationTokenSource stop = new();
        private readonly Task script;

        public ScriptedHost(Func<Stream, Stream, CancellationToken, Task> run)
        {
            hostInput = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
            hostOutput = new AnonymousPipeClientStream(PipeDirection.In, fromHost.ClientSafePipeHandle);
            script = Task.Run(async () =>
            {
                try
                {
                    await run(hostInput, fromHost, stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
                finally
                {
                    fromHost.Dispose();
                }
            });
        }

        public List<string> Requests { get; } = [];

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public Stream Input => toHost;

        public Stream Output => hostOutput;

        public bool HasExited => script.IsCompleted;

        public string ErrorTail => string.Empty;

        /// <summary>Answers each RUN by inverting every frame, holding back the first output for a few frames.</summary>
        public static ScriptedHost Echo(int lagFrames, string reply = "OK")
        {
            ScriptedHost? self = null;
            self = new ScriptedHost(async (input, output, _) =>
            {
                await output.WriteAsync(Encoding.ASCII.GetBytes(MaxineLipSyncHostSession.Handshake + "\n"));
                while (await ReadLineAsync(input) is { } line && line != "QUIT")
                {
                    lock (self!.Requests)
                    {
                        self.Requests.Add(line);
                    }

                    string[] parts = line.Split(' ');
                    int frameBytes = int.Parse(parts[1]) * int.Parse(parts[2]) * 3;
                    int frames = int.Parse(parts[5]);
                    var pending = new Queue<byte[]>();
                    for (int index = 0; index < frames; index++)
                    {
                        byte[] frame = new byte[frameBytes];
                        await input.ReadExactlyAsync(frame);
                        pending.Enqueue(frame.Select(value => (byte)~value).ToArray());
                        if (pending.Count > lagFrames)
                        {
                            await output.WriteAsync(pending.Dequeue());
                        }
                    }

                    while (pending.Count > 0)
                    {
                        await output.WriteAsync(pending.Dequeue());
                    }

                    await output.WriteAsync(Encoding.ASCII.GetBytes((reply == "OK" ? $"OK {frames} 1" : reply) + "\n"));
                }
            });
            return self;
        }

        public static async Task<string?> ReadLineAsync(Stream stream)
        {
            var bytes = new List<byte>();
            byte[] one = new byte[1];
            while (await stream.ReadAsync(one) == 1)
            {
                if (one[0] == '\n')
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(one[0]);
            }

            return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await script.WaitAsync(cancellationToken);
            return 0;
        }

        public void Kill()
        {
            Killed = true;
            stop.Cancel();
            toHost.Dispose();
            hostInput.Dispose();
            fromHost.Dispose();
            hostOutput.Dispose();
        }

        public void Dispose()
        {
            Disposed = true;
            if (!script.IsCompleted)
            {
                Kill();
            }

            toHost.Dispose();
            hostOutput.Dispose();
        }
    }
}
