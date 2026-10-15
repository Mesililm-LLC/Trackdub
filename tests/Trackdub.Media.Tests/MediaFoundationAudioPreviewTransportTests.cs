#if WINDOWS
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class MediaFoundationAudioPreviewTransportTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public async Task Stalled_file_open_does_not_block_cancellation_or_install_a_late_session(string action)
    {
        string path = Path.Join(Path.GetTempPath(), $"audio-open-{Guid.NewGuid():N}.wav");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new TaskCompletionSource<FileStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken testToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        using var transport = new MediaFoundationAudioPreviewTransport(file =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release file creation.");
            var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            created.SetResult(stream);
            return stream;
        });
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                const int samples = 24000;
                writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(samples); writer.Write(samples * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) writer.Write((short)0);
            }
            Task opening = transport.OpenAsync(path, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            switch (action)
            {
                case "cancel": cancellation.Cancel(); break;
                case "stop": await transport.StopAsync(testToken).WaitAsync(TimeSpan.FromSeconds(2), testToken); break;
                case "replace": await transport.OpenAsync(path, testToken).WaitAsync(TimeSpan.FromSeconds(5), testToken); break;
                case "dispose": transport.Dispose(); break;
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => opening.WaitAsync(TimeSpan.FromSeconds(2), testToken));
            release.Set();
            FileStream late = await created.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (late.CanRead) await Task.Delay(10, cleanupTimeout.Token);
            Assert.Equal(action == "replace", (await transport.GetSnapshotAsync(testToken)).IsLoaded);
            await transport.StopAsync(testToken);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            release.Set();
            transport.Dispose();
            File.Delete(path);
        }
    }
}
#endif
