using System.Buffers.Binary;
using System.Text;

namespace Trackdub.Application.Transcripts;

// TODO: The WAV chunk scanning logic here (chunk-ID loop, fmt/data parsing, negative-size and
// overflow checks, padding alignment) is structurally similar to WaveAudioReader in
// Trackdub.Inference.Onnx/Audio/WaveAudioReader.cs. Extracting a shared internal WAV header
// parser (e.g. into a dedicated Trackdub.Media.Codecs assembly) would keep edge-case fixes
// consistent across both paths. Tracked for a follow-up refactoring PR.
public static class AudioArtifactValidator
{
    public sealed record AudioFileInfo(int SampleRate, int ChannelCount, double DurationSeconds);

    public static async Task<AudioFileInfo> ReadAndValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAndValidateAsync(stream, path, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AudioFileInfo> ReadAndValidateAsync(
        Stream stream,
        string path,
        CancellationToken cancellationToken)
    {
        byte[] buf8 = new byte[8];
        byte[] buf4 = new byte[4];
        byte[] buf2 = new byte[2];

        string riffMarker = await ReadFourCcAsync(stream, buf4, cancellationToken).ConfigureAwait(false);
        bool isRf64 = string.Equals(riffMarker, "RF64", StringComparison.Ordinal);
        if (!isRf64 && !string.Equals(riffMarker, "RIFF", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WAV marker 'RIFF' or 'RF64' but found '{riffMarker}'.");
        }

        await ReadUInt32Async(stream, buf4, cancellationToken).ConfigureAwait(false);
        await EnsureFourCcAsync(stream, buf4, "WAVE", cancellationToken).ConfigureAwait(false);

        ushort audioFormat = 0;
        ushort channelCount = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        ulong? ds64DataLength = null;
        long dataLength = 0;
        bool fmtChunkSeen = false;

        while (stream.Position < stream.Length)
        {
            string chunkId = await ReadFourCcAsync(stream, buf4, cancellationToken).ConfigureAwait(false);
            uint chunkSize = await ReadUInt32Async(stream, buf4, cancellationToken).ConfigureAwait(false);
            long chunkStart = stream.Position;
            long chunkLength = chunkSize;

            switch (chunkId)
            {
                case "ds64" when isRf64 && chunkSize >= 16:
                    await ReadUInt64Async(stream, buf8, cancellationToken).ConfigureAwait(false);
                    ds64DataLength = await ReadUInt64Async(stream, buf8, cancellationToken).ConfigureAwait(false);
                    break;
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw new InvalidOperationException($"WAV 'fmt ' chunk is too small ({chunkSize} bytes); minimum required is 16.");
                    }

                    audioFormat = await ReadUInt16Async(stream, buf2, cancellationToken).ConfigureAwait(false);
                    channelCount = await ReadUInt16Async(stream, buf2, cancellationToken).ConfigureAwait(false);
                    sampleRate = await ReadInt32Async(stream, buf4, cancellationToken).ConfigureAwait(false);
                    await ReadInt32Async(stream, buf4, cancellationToken).ConfigureAwait(false);
                    blockAlign = await ReadUInt16Async(stream, buf2, cancellationToken).ConfigureAwait(false);
                    bitsPerSample = await ReadUInt16Async(stream, buf2, cancellationToken).ConfigureAwait(false);
                    fmtChunkSeen = true;
                    break;
                case "data":
                    dataLength = chunkLength = ResolveDataChunkLength(chunkSize, isRf64, ds64DataLength);
                    break;
            }

            long remaining = stream.Length - chunkStart;
            if (chunkLength > remaining || chunkLength + (chunkLength % 2) > remaining)
            {
                throw new InvalidOperationException($"WAV chunk '{chunkId}' at offset {chunkStart} overflows file length {stream.Length}.");
            }

            stream.Position = chunkStart + chunkLength + (chunkLength % 2);
        }

        if (!fmtChunkSeen)
        {
            throw new InvalidOperationException("WAV file is missing required 'fmt ' chunk.");
        }

        if (audioFormat != 1)
        {
            throw new InvalidOperationException($"Unsupported WAV encoding {audioFormat}. Only PCM (1) is supported.");
        }

        if (bitsPerSample != 16)
        {
            throw new InvalidOperationException($"Unsupported WAV bit depth {bitsPerSample}. Only 16-bit PCM is supported.");
        }

        if (channelCount == 0 || sampleRate <= 0 || blockAlign == 0)
        {
            throw new InvalidOperationException("WAV header contains invalid channel count, sample rate, or block alignment.");
        }

        int expectedBlockAlign = channelCount * (bitsPerSample / 8);
        if (blockAlign != expectedBlockAlign)
        {
            throw new InvalidOperationException(
                $"WAV block alignment {blockAlign} is inconsistent with channel count {channelCount} and bit depth {bitsPerSample} (expected {expectedBlockAlign}).");
        }

        if (dataLength <= 0)
        {
            throw new InvalidOperationException($"WAV file '{path}' has no audio data (data chunk length = {dataLength}).");
        }

        if (dataLength % blockAlign != 0)
        {
            throw new InvalidOperationException(
                $"WAV data chunk length {dataLength} is not a multiple of block alignment {blockAlign}; file is malformed.");
        }

        long sampleFrames = dataLength / blockAlign;
        double durationSeconds = (double)sampleFrames / sampleRate;

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            throw new InvalidOperationException($"WAV file '{path}' computed duration {durationSeconds:G6}s is not a positive finite value.");
        }

        return new AudioFileInfo(sampleRate, channelCount, durationSeconds);
    }

    private static long ResolveDataChunkLength(uint chunkSize, bool isRf64, ulong? ds64DataLength)
    {
        if (chunkSize != uint.MaxValue)
        {
            return chunkSize;
        }

        if (!isRf64)
        {
            // Odd, so never a whole number of PCM16 frames: it is the size placeholder a writer left unpatched.
            throw new InvalidOperationException(
                "WAV data chunk size is the unpatched 0xFFFFFFFF placeholder, so the audio length is unknown. " +
                "The file was likely written past the 4 GiB RIFF limit without RF64 or its header was never finalized.");
        }

        if (ds64DataLength is not ulong length || length > long.MaxValue)
        {
            throw new InvalidOperationException("RF64 WAV data chunk defers its size to a ds64 chunk, but no valid ds64 chunk precedes it.");
        }

        return (long)length;
    }

    private static async Task EnsureFourCcAsync(
        Stream stream,
        byte[] buf,
        string expected,
        CancellationToken cancellationToken)
    {
        string actual = await ReadFourCcAsync(stream, buf, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WAV marker '{expected}' but found '{actual}'.");
        }
    }

    private static async Task<string> ReadFourCcAsync(
        Stream stream,
        byte[] buf,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buf.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(buf, 0, 4);
    }

    private static async Task<ushort> ReadUInt16Async(
        Stream stream,
        byte[] buf,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buf.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        byte[] buf,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buf.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static async Task<uint> ReadUInt32Async(
        Stream stream,
        byte[] buf,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buf.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static async Task<ulong> ReadUInt64Async(
        Stream stream,
        byte[] buf,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buf.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buf,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidOperationException("Unexpected end of WAV stream.");
            }

            read += n;
        }
    }
}
