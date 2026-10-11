using System.Buffers.Binary;
using System.Text;

namespace Trackdub.Media.Waveforms;

internal sealed record WavePcm16Info(
    int SampleRate,
    int ChannelCount,
    int BitsPerSample,
    int BlockAlign,
    long DataStartPosition,
    long DataLengthBytes,
    long SampleFrames,
    uint? ChannelMask = null)
{
    public double DurationSeconds => SampleRate == 0 ? 0 : (double)SampleFrames / SampleRate;
}

internal sealed record WaveMonoSamples(
    int SampleRate,
    float[] Samples);

internal sealed record WavePcm16Samples(
    int SampleRate,
    int ChannelCount,
    float[] Samples,
    uint? ChannelMask = null)
{
    public int FrameCount => ChannelCount <= 0 ? 0 : Samples.Length / ChannelCount;
}

internal static class WavePcm16
{
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const uint Ds64MinimumSize = 16;
    private const int ReadChunkBytes = 1024 * 1024;
    private static readonly byte[] PcmSubFormatGuid =
    [
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00,
        0x10, 0x00,
        0x80, 0x00,
        0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
    ];

    public static async Task<WavePcm16Info> ReadInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadInfoAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WavePcm16Info> ReadInfoAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer8 = new byte[8];
        byte[] buffer4 = new byte[4];
        byte[] buffer2 = new byte[2];

        bool isRf64 = IsRf64(await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false));
        _ = await ReadUInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
        EnsureFourCc(await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false), "WAVE");

        ushort audioFormat = 0;
        ushort channelCount = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        uint? channelMask = null;
        bool isExtensiblePcmSubFormat = false;
        ulong? ds64DataLength = null;
        long dataStart = 0;
        long dataLength = 0;

        while (stream.Position < stream.Length)
        {
            string chunkId = await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false);
            uint chunkSize = await ReadUInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
            long chunkStart = stream.Position;
            long chunkLength = chunkSize;

            switch (chunkId)
            {
                case "ds64" when isRf64 && chunkSize >= Ds64MinimumSize:
                    _ = await ReadUInt64Async(stream, buffer8, cancellationToken).ConfigureAwait(false);
                    ds64DataLength = await ReadUInt64Async(stream, buffer8, cancellationToken).ConfigureAwait(false);
                    break;

                case "fmt ":
                    audioFormat = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    channelCount = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    sampleRate = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                    _ = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                    blockAlign = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    bitsPerSample = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    if (audioFormat == WaveFormatExtensible && chunkSize >= 40)
                    {
                        ushort extensionSize = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                        if (extensionSize >= 22)
                        {
                            _ = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                            channelMask = await ReadUInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                            byte[] subFormat = new byte[16];
                            await ReadExactAsync(stream, subFormat.AsMemory(), cancellationToken).ConfigureAwait(false);
                            isExtensiblePcmSubFormat = subFormat.AsSpan().SequenceEqual(PcmSubFormatGuid);
                        }
                    }

                    break;

                case "data":
                    dataStart = chunkStart;
                    dataLength = chunkLength = ResolveDataChunkLength(chunkSize, isRf64, ds64DataLength);
                    break;
            }

            stream.Position = GetPaddedChunkEnd(chunkStart, chunkLength, stream.Length);
        }

        return CreateInfo(audioFormat, channelCount, sampleRate, blockAlign, bitsPerSample, dataStart, dataLength, channelMask, isExtensiblePcmSubFormat);
    }

    public static WavePcm16Info ReadInfo(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadInfo(stream);
    }

    internal static WavePcm16Info ReadInfo(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        bool isRf64 = IsRf64(new string(reader.ReadChars(4)));
        _ = reader.ReadUInt32();
        EnsureFourCc(reader.ReadChars(4), "WAVE");

        ushort audioFormat = 0;
        ushort channelCount = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        uint? channelMask = null;
        bool isExtensiblePcmSubFormat = false;
        ulong? ds64DataLength = null;
        long dataStart = 0;
        long dataLength = 0;

        while (stream.Position < stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.Position;
            long chunkLength = chunkSize;

            switch (chunkId)
            {
                case "ds64" when isRf64 && chunkSize >= Ds64MinimumSize:
                    _ = reader.ReadUInt64();
                    ds64DataLength = reader.ReadUInt64();
                    break;

                case "fmt ":
                    audioFormat = reader.ReadUInt16();
                    channelCount = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    blockAlign = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                    if (audioFormat == WaveFormatExtensible && chunkSize >= 40)
                    {
                        ushort extensionSize = reader.ReadUInt16();
                        if (extensionSize >= 22)
                        {
                            _ = reader.ReadUInt16();
                            channelMask = reader.ReadUInt32();
                            byte[] subFormat = reader.ReadBytes(16);
                            isExtensiblePcmSubFormat = subFormat.AsSpan().SequenceEqual(PcmSubFormatGuid);
                        }
                    }

                    break;

                case "data":
                    dataStart = chunkStart;
                    dataLength = chunkLength = ResolveDataChunkLength(chunkSize, isRf64, ds64DataLength);
                    break;
            }

            stream.Position = GetPaddedChunkEnd(chunkStart, chunkLength, stream.Length);
        }

        return CreateInfo(audioFormat, channelCount, sampleRate, blockAlign, bitsPerSample, dataStart, dataLength, channelMask, isExtensiblePcmSubFormat);
    }

    public static async Task<WaveMonoSamples> ReadAllMonoSamplesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        WavePcm16Info info = await ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadMonoSamplesAsync(
            path,
            info,
            startFrame: 0,
            frameCount: info.SampleFrames,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WavePcm16Samples> ReadAllSamplesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        WavePcm16Info info = await ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadSamplesAsync(
            path,
            info,
            startFrame: 0,
            frameCount: info.SampleFrames,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WaveMonoSamples> ReadMonoSamplesAsync(
        string path,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Start time must be finite and non-negative.");
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be finite and non-negative.");
        }

        WavePcm16Info info = await ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        long startFrame = ClampSecondsToFrameIndex(startSeconds, info.SampleRate, info.SampleFrames);
        long requestedFrames = ClampSecondsToFrameCount(durationSeconds, info.SampleRate, info.SampleFrames);
        long availableFrames = Math.Max(0, info.SampleFrames - startFrame);
        long frameCount = Math.Min(requestedFrames, availableFrames);
        return await ReadMonoSamplesAsync(path, info, startFrame, frameCount, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WavePcm16Samples> ReadSamplesAsync(
        string path,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateReadRange(startSeconds, durationSeconds);
        WavePcm16Info info = await ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadSamplesForValidatedRangeAsync(path, info, startSeconds, durationSeconds, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WavePcm16Samples> ReadSamplesAsync(
        string path,
        WavePcm16Info info,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateReadRange(startSeconds, durationSeconds);
        return await ReadSamplesForValidatedRangeAsync(path, info, startSeconds, durationSeconds, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WavePcm16Samples> ReadSamplesForValidatedRangeAsync(
        string path,
        WavePcm16Info info,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        long startFrame = ClampSecondsToFrameIndex(startSeconds, info.SampleRate, info.SampleFrames);
        long requestedFrames = ClampSecondsToFrameCount(durationSeconds, info.SampleRate, info.SampleFrames);
        long availableFrames = Math.Max(0, info.SampleFrames - startFrame);
        long frameCount = Math.Min(requestedFrames, availableFrames);
        return await ReadSamplesAsync(path, info, startFrame, frameCount, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteMonoAsync(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        await WriteSamplesAsync(
                path,
                samples,
                sampleRate,
                channelCount: 1,
                normalizePeak: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task WriteSamplesAsync(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken = default) =>
        WriteSamplesAsync(
            path,
            samples,
            sampleRate,
            channelCount,
            normalizePeak: false,
            cancellationToken);

    /// <param name="normalizePeak">
    /// When <c>true</c>, scale the whole buffer uniformly so the finite peak fits in [-1, 1]
    /// (avoids hard-clip on hot cumulative mixes). Default <c>false</c> keeps historical
    /// per-sample hard-clamp semantics so existing callers do not see unexpected loudness shifts.
    /// </param>
    public static async Task WriteSamplesAsync(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate,
        int channelCount,
        bool normalizePeak,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");
        }

        if (samples.Count % channelCount != 0)
        {
            throw new ArgumentException("Interleaved sample count must be divisible by the channel count.", nameof(samples));
        }

        string fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrEmpty(Path.GetDirectoryName(fullPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }

        const int framesPerChunk = 8192;

        // Write to a per-call temp file and atomically publish via File.Move rather than opening
        // fullPath directly. Opening the destination path with FileMode.Create truncates it
        // immediately; if the peak scan or write loop below is then cancelled (or throws), the
        // stable path would be left holding a half-written/corrupt WAV, destroying any previously
        // valid file there. Matches the temp-then-move pattern used elsewhere in the repo (e.g.
        // FileSystemArtifactStore, LocalModelCacheRecordStore).
        string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool tempFileCreated = false;
        try
        {
            checked
            {
                int sampleCount = samples.Count;
                int dataLength = sampleCount * sizeof(short);
                int frameCount = sampleCount / channelCount;
                var header = new byte[44];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
                Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
                Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), (short)channelCount);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * channelCount * sizeof(short));
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), (short)(channelCount * sizeof(short)));
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
                Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);

                var fileOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 81920,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                };

                // Restrictive permissions on the temp file so the artifact is readable only by the
                // owner on Unix before any data is written. FileStreamOptions.UnixCreateMode applies
                // the mode atomically at creation; File.Move preserves the mode across the publish.
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using var stream = new FileStream(tempPath, fileOptions);
                tempFileCreated = true;
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

                if (sampleCount > 0)
                {
                    int bytesPerFrame = channelCount * sizeof(short);
                    int maxChunkBytes = framesPerChunk * bytesPerFrame;
                    byte[] chunkBuffer = new byte[maxChunkBytes];

                    // Opt-in per-track peak scan: scale uniformly downward whenever any finite sample
                    // exceeds |1| (notably multichannel -> stereo downmix in PreviewRangeRenderer,
                    // where additive peaks can reach ~2.41). Skipped when normalizePeak is false so
                    // default callers keep hard-clamp loudness. No-op scale when already within [-1, 1].
                    float scale = 1f;
                    if (normalizePeak)
                    {
                        float maxAbs = 1f;
                        for (int i = 0; i < sampleCount; i++)
                        {
                            if ((i & 0xFFFF) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            float abs = Math.Abs(samples[i]);
                            if (float.IsFinite(abs) && abs > maxAbs)
                            {
                                maxAbs = abs;
                            }
                        }

                        scale = 1f / maxAbs;
                    }

                    for (int frameOffset = 0; frameOffset < frameCount; frameOffset += framesPerChunk)
                    {
                        int chunkFrames = Math.Min(framesPerChunk, frameCount - frameOffset);
                        int chunkSamples = chunkFrames * channelCount;
                        int chunkBytes = chunkSamples * sizeof(short);
                        int sourceSampleOffset = frameOffset * channelCount;

                        for (int sampleIndex = 0; sampleIndex < chunkSamples; sampleIndex++)
                        {
                            float value = samples[sourceSampleOffset + sampleIndex] * scale;
                            // NaN stays NaN after scaling; Infinity stays Infinity.
                            // Collapse non-finite values to digital silence to avoid undefined casts and audible artifacts.
                            // Clamp finite values after scale: float reciprocal rounding can leave values slightly
                            // outside [-1, 1] (e.g. 1.0000001f), and Math.Round(value * 32767) can
                            // then yield 32768 which wraps to -32768 on cast to short.
                            value = !float.IsFinite(value) ? 0f : Math.Clamp(value, -1f, 1f);

                            short pcm = (short)Math.Round(value * short.MaxValue);
                            BinaryPrimitives.WriteInt16LittleEndian(chunkBuffer.AsSpan(sampleIndex * sizeof(short)), pcm);
                        }

                        await stream.WriteAsync(chunkBuffer.AsMemory(0, chunkBytes), cancellationToken).ConfigureAwait(false);
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            // Only clean up a temp file this call actually created. If CreateNew failed
            // because the GUID collided with an existing file, that pre-existing file is
            // not ours to delete.
            if (tempFileCreated)
            {
                TryDeleteTempFile(tempPath);
            }

            throw;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception)
        {
            // Best-effort cleanup only. Must never throw: an exception here would suppress the
            // original write/cancel failure propagating through catch { TryDeleteTempFile; throw; }.
        }
    }

    private static async Task<WaveMonoSamples> ReadMonoSamplesAsync(
        string path,
        WavePcm16Info info,
        long startFrame,
        long frameCount,
        CancellationToken cancellationToken)
    {
        WavePcm16Samples interleaved = await ReadSamplesAsync(path, info, startFrame, frameCount, cancellationToken)
            .ConfigureAwait(false);

        if (interleaved.Samples.Length == 0)
        {
            return new WaveMonoSamples(info.SampleRate, []);
        }

        var samples = new float[interleaved.FrameCount];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            float sum = 0f;
            int frameOffset = frame * interleaved.ChannelCount;
            for (int channel = 0; channel < interleaved.ChannelCount; channel++)
            {
                sum += interleaved.Samples[frameOffset + channel];
            }

            samples[frame] = sum / interleaved.ChannelCount;
        }

        return new WaveMonoSamples(info.SampleRate, samples);
    }

    private static async Task<WavePcm16Samples> ReadSamplesAsync(
        string path,
        WavePcm16Info info,
        long startFrame,
        long frameCount,
        CancellationToken cancellationToken)
    {
        if (frameCount == 0)
        {
            return new WavePcm16Samples(info.SampleRate, info.ChannelCount, [], info.ChannelMask);
        }

        checked
        {
            // Only the float[] has to fit in an array; the PCM bytes are streamed in chunks so a range whose
            // byte count exceeds int.MaxValue (over ~3.1 h of 48 kHz stereo) still reads.
            int sampleCount = (int)(frameCount * info.ChannelCount);
            var samples = new float[sampleCount];
            int framesPerChunk = Math.Max(1, ReadChunkBytes / info.BlockAlign);
            var bytes = new byte[(int)Math.Min(frameCount, framesPerChunk) * info.BlockAlign];
            await using var stream = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = info.DataStartPosition + (startFrame * info.BlockAlign);

            int sampleIndex = 0;
            for (long frame = 0; frame < frameCount; frame += framesPerChunk)
            {
                int chunkFrames = (int)Math.Min(framesPerChunk, frameCount - frame);
                await ReadExactAsync(stream, bytes.AsMemory(0, chunkFrames * info.BlockAlign), cancellationToken).ConfigureAwait(false);
                for (int chunkFrame = 0; chunkFrame < chunkFrames; chunkFrame++)
                {
                    int frameOffset = chunkFrame * info.BlockAlign;
                    for (int channel = 0; channel < info.ChannelCount; channel++)
                    {
                        int sampleOffset = frameOffset + (channel * sizeof(short));
                        samples[sampleIndex++] =
                            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(sampleOffset, sizeof(short))) / 32768f;
                    }
                }
            }

            return new WavePcm16Samples(info.SampleRate, info.ChannelCount, samples, info.ChannelMask);
        }
    }

    private static long ClampSecondsToFrameIndex(double seconds, int sampleRate, long maxFrames)
    {
        if (maxFrames <= 0)
        {
            return 0;
        }

        double maxSeconds = maxFrames / (double)sampleRate;
        double boundedSeconds = Math.Min(seconds, maxSeconds);
        double frames = Math.Floor(boundedSeconds * sampleRate);
        return ClampFrameValue(frames, maxFrames);
    }

    private static long ClampSecondsToFrameCount(double seconds, int sampleRate, long maxFrames)
    {
        if (maxFrames <= 0)
        {
            return 0;
        }

        double maxSeconds = maxFrames / (double)sampleRate;
        double boundedSeconds = Math.Min(seconds, maxSeconds);
        double frames = Math.Ceiling(boundedSeconds * sampleRate);
        return ClampFrameValue(frames, maxFrames);
    }

    private static void ValidateReadRange(double startSeconds, double durationSeconds)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Start time must be finite and non-negative.");
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be finite and non-negative.");
        }
    }

    private static long ClampFrameValue(double frames, long maxFrames)
    {
        if (!double.IsFinite(frames) || frames <= 0d)
        {
            return 0;
        }

        if (frames >= maxFrames)
        {
            return maxFrames;
        }

        return (long)frames;
    }

    private static void EnsureFourCc(string actualText, string expected)
    {
        if (!string.Equals(actualText, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WAV marker '{expected}' but found '{actualText}'.");
        }
    }

    private static void EnsureFourCc(char[] actual, string expected)
    {
        EnsureFourCc(new string(actual), expected);
    }

    private static bool IsRf64(string riffMarker)
    {
        if (string.Equals(riffMarker, "RF64", StringComparison.Ordinal))
        {
            return true;
        }

        EnsureFourCc(riffMarker, "RIFF");
        return false;
    }

    private static long ResolveDataChunkLength(uint chunkSize, bool isRf64, ulong? ds64DataLength)
    {
        if (chunkSize != uint.MaxValue)
        {
            return chunkSize;
        }

        if (!isRf64)
        {
            // A PCM16 data chunk holds whole frames of an even block size, so an odd 0xFFFFFFFF can only be
            // the placeholder a writer leaves unpatched: ffmpeg does this when the data outgrows 32-bit RIFF
            // without `-rf64 auto`, and streamed/pipe output never patches it at all.
            throw new InvalidOperationException(
                "WAV data chunk size is the unpatched 0xFFFFFFFF placeholder, so the audio length is unknown. " +
                "The file was likely written past the 4 GiB RIFF limit without RF64 (ffmpeg '-rf64 auto') or its header was never finalized.");
        }

        if (ds64DataLength is not ulong length)
        {
            throw new InvalidOperationException("RF64 WAV data chunk defers its size to a ds64 chunk, but no ds64 chunk precedes it.");
        }

        if (length > long.MaxValue)
        {
            throw new InvalidOperationException($"RF64 WAV ds64 data size {length} is out of range.");
        }

        return (long)length;
    }

    private static long GetPaddedChunkEnd(long chunkStart, long chunkLength, long streamLength)
    {
        long remaining = streamLength - chunkStart;
        if (chunkLength > remaining || chunkLength + (chunkLength % 2) > remaining)
        {
            throw new InvalidOperationException("WAV chunk padding exceeded the file length.");
        }

        return chunkStart + chunkLength + (chunkLength % 2);
    }

    private static WavePcm16Info CreateInfo(
        ushort audioFormat,
        ushort channelCount,
        int sampleRate,
        ushort blockAlign,
        ushort bitsPerSample,
        long dataStart,
        long dataLength,
        uint? channelMask,
        bool isExtensiblePcmSubFormat)
    {
        if (audioFormat != WaveFormatPcm && (audioFormat != WaveFormatExtensible || !isExtensiblePcmSubFormat))
        {
            throw new InvalidOperationException($"Unsupported WAV encoding '{audioFormat}'. Only PCM is supported.");
        }

        if (bitsPerSample != 16)
        {
            throw new InvalidOperationException($"Unsupported WAV bit depth '{bitsPerSample}'. Only 16-bit PCM is supported.");
        }

        if (channelCount == 0 || sampleRate <= 0 || blockAlign == 0)
        {
            throw new InvalidOperationException("WAV header contains invalid channel count, sample rate, or block alignment.");
        }

        if (dataStart == 0 || dataLength == 0)
        {
            throw new InvalidOperationException("WAV file does not contain a data chunk.");
        }

        long sampleFrames = dataLength / blockAlign;
        return new WavePcm16Info(sampleRate, channelCount, bitsPerSample, blockAlign, dataStart, dataLength, sampleFrames, channelMask);
    }

    private static async Task<string> ReadFourCcAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(buffer, 0, 4);
    }

    private static async Task<ushort> ReadUInt16Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<ulong> ReadUInt64Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static async Task<uint> ReadUInt32Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Unexpected end of WAV header.");
            }

            totalBytesRead += bytesRead;
        }
    }
}
