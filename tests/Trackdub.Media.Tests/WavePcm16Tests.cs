using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Trackdub.Media.Waveforms;
using Trackdub.TestDoubles;

namespace Trackdub.Media.Tests;

public sealed class WavePcm16Tests : IDisposable
{
    private readonly string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));

    public WavePcm16Tests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task ReadSamplesAsync_clamps_extremely_large_start_without_overflow()
    {
        string path = Path.Join(tempDirectory, "stereo.wav");
        await WavePcm16.WriteSamplesAsync(
            path,
            [0.25f, -0.25f, 0.5f, -0.5f],
            sampleRate: 2,
            channelCount: 2,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadSamplesAsync(
            path,
            startSeconds: double.MaxValue / 2d,
            durationSeconds: 1d,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.SampleRate);
        Assert.Equal(2, samples.ChannelCount);
        Assert.Empty(samples.Samples);
    }

    [Fact]
    public async Task ReadSamplesAsync_clamps_extreme_internal_frame_metadata_before_long_conversion()
    {
        var info = new WavePcm16Info(
            SampleRate: 2,
            ChannelCount: 2,
            BitsPerSample: 16,
            BlockAlign: 4,
            DataStartPosition: 44,
            DataLengthBytes: long.MaxValue,
            SampleFrames: long.MaxValue);

        WavePcm16Samples samples = await WavePcm16.ReadSamplesAsync(
            Path.Join(tempDirectory, "not-read.wav"),
            info,
            startSeconds: double.MaxValue / 2d,
            durationSeconds: 1d,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.SampleRate);
        Assert.Equal(2, samples.ChannelCount);
        Assert.Empty(samples.Samples);
    }

    [Fact]
    public async Task ReadMonoSamplesAsync_clamps_extremely_large_duration_without_overflow()
    {
        string path = Path.Join(tempDirectory, "mono.wav");
        float[] input = [0.1f, 0.2f, 0.3f, 0.4f];
        await WavePcm16.WriteMonoAsync(path, input, sampleRate: 4, TestContext.Current.CancellationToken);

        WaveMonoSamples samples = await WavePcm16.ReadMonoSamplesAsync(
            path,
            startSeconds: 0d,
            durationSeconds: double.MaxValue / 2d,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, samples.SampleRate);
        Assert.Equal(input.Length, samples.Samples.Length);
        Assert.Collection(
            samples.Samples,
            sample => Assert.InRange(sample, 0.09f, 0.11f),
            sample => Assert.InRange(sample, 0.19f, 0.21f),
            sample => Assert.InRange(sample, 0.29f, 0.31f),
            sample => Assert.InRange(sample, 0.39f, 0.41f));
    }

    [Fact]
    public async Task ReadInfoAsync_preserves_high_bit_extensible_channel_mask()
    {
        string path = Path.Join(tempDirectory, "extensible-high-mask.wav");
        const uint channelMask = 0x80000003u;
        WriteWaveExtensible(path, samples: [0f, 0f], sampleRate: 4, channelCount: 2, channelMask);

        WavePcm16Info info = await WavePcm16.ReadInfoAsync(path, TestContext.Current.CancellationToken);
        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(channelMask, info.ChannelMask);
        Assert.Equal(channelMask, samples.ChannelMask);
    }

    [Fact]
    public async Task ReadSamplesAsync_reads_range_spanning_multiple_read_chunks()
    {
        string path = Path.Join(tempDirectory, "multi-chunk.wav");
        const int sampleRate = 48000;
        const int frameCount = 300_000;
        float[] input = new float[frameCount * 2];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = ((i % 2001) - 1000) / 1000f;
        }

        await WavePcm16.WriteSamplesAsync(path, input, sampleRate, channelCount: 2, TestContext.Current.CancellationToken);

        const int startFrame = 1_000;
        WavePcm16Samples samples = await WavePcm16.ReadSamplesAsync(
            path,
            startSeconds: startFrame / (double)sampleRate,
            durationSeconds: (frameCount - startFrame) / (double)sampleRate,
            TestContext.Current.CancellationToken);

        Assert.Equal((frameCount - startFrame) * 2, samples.Samples.Length);
        for (int i = 0; i < samples.Samples.Length; i++)
        {
            float expected = (short)Math.Round(input[(startFrame * 2) + i] * short.MaxValue) / 32768f;
            Assert.Equal(expected, samples.Samples[i]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadInfo_reads_riff_data_chunk_larger_than_int_max(bool synchronous)
    {
        // ~4.3 h of 48 kHz stereo PCM16: the chunk size has the sign bit set when read as int32.
        const uint dataLength = 3_000_000_000u;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: dataLength, ds64DataLength: null, dataLength);

        WavePcm16Info info = await ReadInfoAsync(stream, synchronous);

        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(2, info.ChannelCount);
        Assert.Equal(SparseWaveStream.RiffDataStart, info.DataStartPosition);
        Assert.Equal(dataLength, info.DataLengthBytes);
        Assert.Equal(dataLength / 4, info.SampleFrames);
        Assert.Equal(15_625d, info.DurationSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadInfo_reads_rf64_data_length_beyond_uint_max_from_ds64(bool synchronous)
    {
        const long dataLength = 5_000_000_000L;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: true, dataChunkSize: uint.MaxValue, ds64DataLength: dataLength, dataLength);

        WavePcm16Info info = await ReadInfoAsync(stream, synchronous);

        Assert.Equal(SparseWaveStream.Rf64DataStart, info.DataStartPosition);
        Assert.Equal(dataLength, info.DataLengthBytes);
        Assert.Equal(dataLength / 4, info.SampleFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadInfo_rejects_riff_data_chunk_size_placeholder(bool synchronous)
    {
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: uint.MaxValue, ds64DataLength: null, dataLength: 5_000_000_000L);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadInfoAsync(stream, synchronous));

        Assert.Contains("0xFFFFFFFF placeholder", exception.Message, StringComparison.Ordinal);
        Assert.Contains("-rf64 auto", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadInfo_rejects_rf64_data_chunk_without_ds64(bool synchronous)
    {
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: true, dataChunkSize: uint.MaxValue, ds64DataLength: null, dataLength: 5_000_000_000L);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadInfoAsync(stream, synchronous));

        Assert.Contains("ds64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteSamplesAsync_default_hard_clamps_overflowing_input_without_scaling()
    {
        string path = Path.Join(tempDirectory, "hot-clamp-mono.wav");
        float[] hot = [2.5f, -2.5f, 1.5f, -1.5f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            hot,
            sampleRate: 2,
            channelCount: 1,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(hot.Length, samples.Samples.Length);

        // Default path hard-clamps to ±1; quieter samples keep absolute amplitude (no track scale).
        Assert.InRange(samples.Samples[0], 0.99f, 1.01f);
        Assert.InRange(samples.Samples[1], -1.01f, -0.99f);
        Assert.InRange(samples.Samples[2], 0.99f, 1.01f);
        Assert.InRange(samples.Samples[3], -1.01f, -0.99f);
        Assert.InRange(samples.Samples[4], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[5], -0.51f, -0.49f);
    }

    [Fact]
    public async Task WriteSamplesAsync_scales_overflowing_input_to_unit_peak_when_normalize_peak_enabled()
    {
        string path = Path.Join(tempDirectory, "hot-mono.wav");
        float[] hot = [2.5f, -2.5f, 1.5f, -1.5f, 0f, 0f];
        await WavePcm16.WriteSamplesAsync(
            path,
            hot,
            sampleRate: 2,
            channelCount: 1,
            normalizePeak: true,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, samples.SampleRate);
        Assert.Equal(1, samples.ChannelCount);
        Assert.Equal(hot.Length, samples.Samples.Length);

        // maxAbs = 2.5; scale = 0.4 — 6 decimals + 32768 quantization
        const float ScaledTolerance = 0.01f;
        Assert.InRange(samples.Samples[0], 0.99f, 1.01f);
        Assert.InRange(samples.Samples[1], -1.01f, -0.99f);
        Assert.InRange(samples.Samples[2], 0.59f, 0.61f);
        Assert.InRange(samples.Samples[3], -0.61f, -0.59f);
        Assert.InRange(samples.Samples[4], -ScaledTolerance, ScaledTolerance);
        Assert.InRange(samples.Samples[5], -ScaledTolerance, ScaledTolerance);
    }

    [Fact]
    public async Task WriteSamplesAsync_does_not_scale_input_already_within_unit_peak()
    {
        string path = Path.Join(tempDirectory, "in-range-mono.wav");
        float[] inRange = [0.25f, -0.25f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            inRange,
            sampleRate: 2,
            channelCount: 1,
            normalizePeak: true,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(inRange.Length, samples.Samples.Length);

        Assert.InRange(samples.Samples[0], 0.24f, 0.26f);
        Assert.InRange(samples.Samples[1], -0.26f, -0.24f);
        Assert.InRange(samples.Samples[2], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[3], -0.51f, -0.49f);
    }

    [Fact]
    public async Task WriteSamplesAsync_silences_nan_and_infinity_samples()
    {
        string path = Path.Join(tempDirectory, "weird-mono.wav");
        float[] weird = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            weird,
            sampleRate: 4,
            channelCount: 1,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(4, samples.SampleRate);
        Assert.Equal(1, samples.ChannelCount);
        Assert.Equal(weird.Length, samples.Samples.Length);

        const float ZeroTolerance = 0.01f;
        Assert.InRange(samples.Samples[0], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[1], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[2], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[3], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[4], -0.51f, -0.49f);
    }

    [Fact]
    public async Task WriteSamplesAsync_normalizePeak_mixed_hot_and_nonfinite_samples_are_scaled_and_silenced_correctly()
    {
        // Finite peak = 3.0 from -3.0; NaN/Inf must not inflate maxAbs, then must be silenced.
        // scale = 1/3 → 2.5→~0.833, -3.0→~-1.0, 0.75→0.25.
        string path = Path.Join(tempDirectory, "normalize-peak-mixed-nonfinite.wav");
        float[] mixed =
        [
            2.5f,
            float.NaN,
            float.PositiveInfinity,
            -3.0f,
            float.NegativeInfinity,
            0.75f
        ];

        await WavePcm16.WriteSamplesAsync(
            path,
            mixed,
            sampleRate: 4,
            channelCount: 1,
            normalizePeak: true,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(mixed.Length, samples.Samples.Length);

        const float ZeroTolerance = 0.01f;
        Assert.InRange(samples.Samples[0], 0.82f, 0.84f);
        Assert.InRange(samples.Samples[1], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[2], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[3], -1.01f, -0.99f);
        Assert.InRange(samples.Samples[4], -ZeroTolerance, ZeroTolerance);
        Assert.InRange(samples.Samples[5], 0.24f, 0.26f);

        float peak = samples.Samples.Max(Math.Abs);

        Assert.InRange(peak, 0.99f, 1.01f);
    }

    [Fact]
    public async Task WriteSamplesAsync_normalizes_fixed_source_to_exact_pcm_shorts()
    {
        // maxAbs = 2.0 (power of 2) -> exact 0.5x scale; writer PCM 32767 -> reader /32768 -> 32766.
        string path = Path.Join(tempDirectory, "exact-scaling.wav");
        float[] fixedSource = [2.0f, 0.5f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            fixedSource,
            sampleRate: 4,
            channelCount: 1,
            normalizePeak: true,
            TestContext.Current.CancellationToken);

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(4, samples.SampleRate);
        Assert.Equal(1, samples.ChannelCount);
        Assert.Equal(fixedSource.Length, samples.Samples.Length);

        int[] reconstructedPcms = new int[samples.Samples.Length];
        for (int i = 0; i < samples.Samples.Length; i++)
        {
            reconstructedPcms[i] = (int)Math.Round(samples.Samples[i] * short.MaxValue);
        }

        Assert.Equal(32766, reconstructedPcms[0]); // writer PCM 32767 -> reconstructed 32766 via /32768 round-trip
        Assert.Equal(8192, reconstructedPcms[1]);
        Assert.Equal(8192, reconstructedPcms[2]);
        Assert.Equal(-8192, reconstructedPcms[3]);
    }

    [Fact]
    public async Task WriteSamplesAsync_cancelled_mid_write_leaves_existing_destination_file_untouched()
    {
        // WriteSamplesAsync now writes to a temp file and only File.Move-s it onto the destination
        // once the whole write succeeds. Before that change, opening the destination directly with
        // FileMode.Create truncated it up front, so a cancellation partway through (the peak scan
        // or the chunked write loop) left a half-written/corrupt WAV at the stable path, destroying
        // whatever valid file was there before. Verify the original file survives untouched and no
        // orphan temp file is left behind.
        string path = Path.Join(tempDirectory, "protected.wav");
        float[] original = [0.25f, -0.25f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            original,
            sampleRate: 4,
            channelCount: 1,
            TestContext.Current.CancellationToken);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        float[] replacement = [0.9f, -0.9f, 0.9f, -0.9f];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WavePcm16.WriteSamplesAsync(
                path,
                replacement,
                sampleRate: 4,
                channelCount: 1,
                normalizePeak: true,
                alreadyCancelled.Token));

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original.Length, samples.Samples.Length);
        Assert.InRange(samples.Samples[0], 0.24f, 0.26f);
        Assert.InRange(samples.Samples[1], -0.26f, -0.24f);
        Assert.InRange(samples.Samples[2], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[3], -0.51f, -0.49f);

        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WriteSamplesAsync_cancelled_when_destination_missing_does_not_create_destination_or_temp_files()
    {
        // When no destination exists yet, a failed WriteSamplesAsync must not publish a partial
        // artifact at the stable path and must leave no orphan .tmp files behind.
        string path = Path.Join(tempDirectory, "missing.wav");
        Assert.False(File.Exists(path));

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WavePcm16.WriteSamplesAsync(
                path,
                [0.25f, -0.25f, 0.5f, -0.5f],
                sampleRate: 4,
                channelCount: 1,
                normalizePeak: true,
                alreadyCancelled.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WriteSamplesAsync_cancelled_during_chunked_write_leaves_existing_destination_file_untouched()
    {
        // Cover cancellation after the temp file has been opened, the header written, and at least
        // one PCM chunk flushed, but before File.Move publishes. A list that cancels on the first
        // sample of the second write chunk trips WriteAsync's token check. Pre-cancelled tokens
        // only exercise the header short-circuit; this hits the mid-write cleanup path.
        string path = Path.Join(tempDirectory, "protected-midwrite.wav");
        float[] original = [0.25f, -0.25f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            original,
            sampleRate: 4,
            channelCount: 1,
            TestContext.Current.CancellationToken);

        const int framesPerChunk = 8192;
        const int sampleCount = framesPerChunk * 2;
        using var cts = new CancellationTokenSource();
        // Peak scan reads sampleCount samples first; cancel on the first sample of chunk 2 so
        // chunk 1's WriteAsync has already completed against an uncancelled token.
        var replacement = new CancellingAfterReadsSamples(
            sampleCount,
            cancelAfterReads: sampleCount + framesPerChunk + 1,
            cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WavePcm16.WriteSamplesAsync(
                path,
                replacement,
                sampleRate: sampleCount,
                channelCount: 1,
                normalizePeak: true,
                cts.Token));

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original.Length, samples.Samples.Length);
        Assert.InRange(samples.Samples[0], 0.24f, 0.26f);
        Assert.InRange(samples.Samples[1], -0.26f, -0.24f);
        Assert.InRange(samples.Samples[2], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[3], -0.51f, -0.49f);

        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WriteSamplesAsync_move_failure_deletes_temp_file_and_preserves_destination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // File.Move must not leave an orphan temp file if it cannot overwrite the destination.
        // Hold the destination open with FileShare.None so the move fails after the temp file
        // has been fully written and flushed.
        string path = Path.Join(tempDirectory, "move-locked.wav");
        float[] original = [0.25f, -0.25f, 0.5f, -0.5f];
        await WavePcm16.WriteSamplesAsync(
            path,
            original,
            sampleRate: 4,
            channelCount: 1,
            TestContext.Current.CancellationToken);

        using (var destinationLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                WavePcm16.WriteSamplesAsync(
                    path,
                    [0.9f, -0.9f, 0.9f, -0.9f],
                    sampleRate: 4,
                    channelCount: 1,
                    normalizePeak: true,
                    TestContext.Current.CancellationToken));

            Assert.True(
                ex is IOException or UnauthorizedAccessException,
                $"Expected IOException or UnauthorizedAccessException, got {ex.GetType().FullName}.");
        }

        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));

        WavePcm16Samples samples = await WavePcm16.ReadAllSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original.Length, samples.Samples.Length);
        Assert.InRange(samples.Samples[0], 0.24f, 0.26f);
        Assert.InRange(samples.Samples[1], -0.26f, -0.24f);
        Assert.InRange(samples.Samples[2], 0.49f, 0.51f);
        Assert.InRange(samples.Samples[3], -0.51f, -0.49f);
    }

    private sealed class CancellingAfterReadsSamples : IReadOnlyList<float>
    {
        private readonly int cancelAfterReads;
        private readonly CancellationTokenSource cts;
        private int reads;

        public CancellingAfterReadsSamples(int sampleCount, int cancelAfterReads, CancellationTokenSource cts)
        {
            Count = sampleCount;
            this.cancelAfterReads = cancelAfterReads;
            this.cts = cts;
        }

        public int Count { get; }

        public float this[int index]
        {
            get
            {
                reads++;
                if (reads == cancelAfterReads)
                {
                    cts.Cancel();
                }

                return 0.5f;
            }
        }

        public IEnumerator<float> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void WriteWaveExtensible(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate,
        int channelCount,
        uint channelMask)
    {
        int dataLength = samples.Count * sizeof(short);
        var header = new byte[68];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 60 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 0xFFFE);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), (short)channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * channelCount * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), (short)(channelCount * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(36, 2), 22);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(38, 2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), channelMask);
        byte[] pcmSubFormatGuid =
        [
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00,
            0x10, 0x00,
            0x80, 0x00,
            0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
        ];
        pcmSubFormatGuid.CopyTo(header.AsSpan(44));
        Encoding.ASCII.GetBytes("data").CopyTo(header, 60);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64, 4), dataLength);

        var data = new byte[dataLength];
        for (int i = 0; i < samples.Count; i++)
        {
            int pcm = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32768f);
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(i * sizeof(short), sizeof(short)),
                (short)Math.Clamp(pcm, short.MinValue, short.MaxValue));
        }

        File.WriteAllBytes(path, header.Concat(data).ToArray());
    }

    private static async Task<WavePcm16Info> ReadInfoAsync(Stream stream, bool synchronous) =>
        synchronous
            ? WavePcm16.ReadInfo(stream)
            : await WavePcm16.ReadInfoAsync(stream, TestContext.Current.CancellationToken);
}
