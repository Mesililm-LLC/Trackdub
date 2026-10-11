using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Audio;

internal static class WaveAudioReader
{
    public static Task<IAudioChannelSamples> ReadPcm16Async(
        string path,
        CancellationToken cancellationToken) =>
        ReadPcm16CoreAsync(path, cancellationToken);

    public static async Task<IAudioSamples> ReadMonoPcm16Async(
        string path,
        CancellationToken cancellationToken) =>
        await ReadPcm16CoreAsync(path, cancellationToken).ConfigureAwait(false);

    private static async Task<IAudioChannelSamples> ReadPcm16CoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileStream? stream = null;
        System.IO.MemoryMappedFiles.MemoryMappedFile? mmf = null;
        System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? accessor = null;

        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            WavePcm16Layout layout = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

            mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                stream,
                null,
                0,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);

            accessor = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

            var reader = new MemoryMappedWaveAudioReader(
                mmf,
                accessor,
                stream,
                layout.ChannelCount,
                layout.BlockAlign,
                layout.DataStart,
                layout.SampleRate,
                layout.SampleFrameCount);
            mmf = null;
            accessor = null;
            stream = null;
            return reader;
        }
        finally
        {
            accessor?.Dispose();
            mmf?.Dispose();
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static async Task<WavePcm16Layout> ReadHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer8 = new byte[8];
        byte[] buffer4 = new byte[4];
        byte[] buffer2 = new byte[2];

        string riffMarker = await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false);
        bool isRf64 = string.Equals(riffMarker, "RF64", StringComparison.Ordinal);
        if (!isRf64)
        {
            EnsureFourCc(riffMarker, "RIFF");
        }

        _ = await ReadUInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
        EnsureFourCc(await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false), "WAVE");

        ushort audioFormat = 0;
        ushort channelCount = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
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
                case "ds64" when isRf64 && chunkSize >= 16:
                    _ = await ReadUInt64Async(stream, buffer8, cancellationToken).ConfigureAwait(false);
                    ds64DataLength = await ReadUInt64Async(stream, buffer8, cancellationToken).ConfigureAwait(false);
                    break;
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw new InvalidOperationException($"WAV 'fmt ' chunk is too small ({chunkSize} bytes); minimum required is 16.");
                    }

                    audioFormat = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    channelCount = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    sampleRate = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                    _ = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                    blockAlign = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    bitsPerSample = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                    break;
                case "data":
                    dataStart = chunkStart;
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

        ValidateHeader(audioFormat, channelCount, sampleRate, blockAlign, bitsPerSample, dataStart, dataLength);

        return new WavePcm16Layout(channelCount, blockAlign, dataStart, sampleRate, dataLength / blockAlign);
    }

    private static void ValidateHeader(
        ushort audioFormat,
        ushort channelCount,
        int sampleRate,
        ushort blockAlign,
        ushort bitsPerSample,
        long dataStart,
        long dataLength)
    {
        if (audioFormat != 1)
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

        ushort expectedBlockAlign = checked((ushort)(channelCount * (bitsPerSample / 8)));
        if (blockAlign != expectedBlockAlign)
        {
            throw new InvalidOperationException(
                $"WAV block alignment {blockAlign} is inconsistent with channel count {channelCount} and bit depth {bitsPerSample} (expected {expectedBlockAlign}).");
        }

        if (dataStart == 0 || dataLength == 0)
        {
            throw new InvalidOperationException("WAV file does not contain a data chunk.");
        }
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

    private static void EnsureFourCc(string actualText, string expected)
    {
        if (!string.Equals(actualText, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WAV marker '{expected}' but found '{actualText}'.");
        }
    }

    private static async Task<string> ReadFourCcAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, 4);
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

    private static async Task<uint> ReadUInt32Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static async Task<ulong> ReadUInt64Async(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
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

internal readonly record struct WavePcm16Layout(
    ushort ChannelCount,
    ushort BlockAlign,
    long DataStart,
    int SampleRate,
    long SampleFrameCount);
