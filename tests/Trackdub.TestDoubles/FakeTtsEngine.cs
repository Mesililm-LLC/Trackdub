using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public class FakeTtsEngine : ITtsEngine, ITtsEngineWithExecutionSummary
{
    private const int DefaultSampleRate = 24000;
    private const int DefaultDurationSamples = 240; // 10 ms

    public string? LastInputText { get; private set; }
    public VoiceCatalogEntry? LastVoicepack { get; private set; }
    public InferenceRequestOptions? LastOptions { get; private set; }
    public TtsSynthesisRequest? LastRequest { get; private set; }
    public List<TtsSynthesisRequest> Requests { get; } = [];
    private int synthesizeCallCount;

    public int SynthesizeCallCount => Volatile.Read(ref synthesizeCallCount);
    public int DurationSamples { get; set; } = DefaultDurationSamples;
    public int SampleRate { get; set; } = DefaultSampleRate;

    public virtual Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        LastInputText = request.Text;
        LastVoicepack = request.Voice;
        LastOptions = request.Options;
        LastRequest = request;
        lock (Requests)
        {
            Requests.Add(request);
        }

        Interlocked.Increment(ref synthesizeCallCount);

        byte[] wav = BuildMinimalSilentWav(SampleRate, DurationSamples);
        return Task.FromResult(new TtsSynthesisResult(
            wav,
            DurationSamples,
            SampleRate,
            ModelId: "fake",
            request.Voice.VoiceId,
            Provider: "fake"));
    }

    public Task<(TtsSynthesisResult Result, StageRuntimeExecutionSummary? Summary)> SynthesizeWithSummaryAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        // Delegate to SynthesizeAsync for consistency, then attach a fresh summary
        // (avoiding the cross-call mutable state race that the summary interface was designed to solve).
        Task<TtsSynthesisResult> resultTask = SynthesizeAsync(request, cancellationToken);
        var summary = new StageRuntimeExecutionSummary(
            "auto",
            "cpu",
            "fake/tts",
            "fake-tts-model",
            null,
            "Fake TTS execution summary");
        return resultTask.ContinueWith(
            static (t, s) => ((TtsSynthesisResult, StageRuntimeExecutionSummary?))(t.Result, (StageRuntimeExecutionSummary?)s),
            summary,
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static byte[] BuildMinimalSilentWav(int sampleRate, int durationSamples)
    {
        // PCM 16-bit mono WAV
        const int bitsPerSample = 16;
        const int channels = 1;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        int dataBytes = durationSamples * blockAlign;

        byte[] wav = new byte[44 + dataBytes];
        int pos = 0;

        void WriteBytes(byte[] src) { Array.Copy(src, 0, wav, pos, src.Length); pos += src.Length; }
        void WriteAscii(string s) { WriteBytes(System.Text.Encoding.ASCII.GetBytes(s)); }
        void WriteInt32(int v) { WriteBytes(BitConverter.GetBytes(v)); }
        void WriteInt16(short v) { WriteBytes(BitConverter.GetBytes(v)); }

        WriteAscii("RIFF");
        WriteInt32(36 + dataBytes);
        WriteAscii("WAVE");
        WriteAscii("fmt ");
        WriteInt32(16);              // chunk size
        WriteInt16(1);               // PCM
        WriteInt16((short)channels);
        WriteInt32(sampleRate);
        WriteInt32(byteRate);
        WriteInt16((short)blockAlign);
        WriteInt16(bitsPerSample);
        WriteAscii("data");
        WriteInt32(dataBytes);
        // remaining bytes are zero (silence)

        return wav;
    }
}
