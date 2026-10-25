using System.IO.Pipes;
using System.Text;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

/// <summary>A host process stand-in that runs a script over two anonymous pipes.</summary>
internal sealed class ScriptedHost : IPipedProcess
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
