using System.Buffers.Binary;
using System.Text;
using Trackdub.Application.Transcripts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class AudioArtifactValidatorTests : IDisposable
{
    private readonly string tempDir = Path.Join(Path.GetTempPath(), $"AudioArtifactValidatorTests_{Guid.NewGuid():N}");

    public AudioArtifactValidatorTests() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        if (!Directory.Exists(tempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string WriteWav(byte[] bytes)
    {
        string path = Path.Join(tempDir, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ValidPcm16Mono_ReturnsCorrectInfo()
    {
        string path = WriteWav(BuildWav());
        AudioArtifactValidator.AudioFileInfo info = await AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.ChannelCount);
        Assert.True(info.DurationSeconds > 0);
    }

    [Fact]
    public async Task MissingRiffMarker_Throws()
    {
        byte[] wav = BuildWav();
        Encoding.ASCII.GetBytes("XXXX").CopyTo(wav, 0);
        string path = WriteWav(wav);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingWaveMarker_Throws()
    {
        byte[] wav = BuildWav();
        Encoding.ASCII.GetBytes("XXXX").CopyTo(wav, 8);
        string path = WriteWav(wav);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FmtChunkTooSmall_Throws()
    {
        byte[] wav = BuildWav(fmtSize: 8);
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("fmt", ex.Message);
        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task NegativeChunkSize_Throws()
    {
        byte[] wav = BuildWav();
        // Overwrite the data chunk size with -1
        int dataChunkSizeOffset = 40;
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(dataChunkSizeOffset, 4), -1);
        string path = WriteWav(wav);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RiffDataChunkLargerThanIntMax_ReturnsDuration()
    {
        // ~4.3 h of 48 kHz stereo: the normalized track a 4 h source produces.
        const uint dataLength = 3_000_000_000u;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: dataLength, ds64DataLength: null, dataLength);

        AudioArtifactValidator.AudioFileInfo info = await AudioArtifactValidator.ReadAndValidateAsync(stream, "normalized.wav", TestContext.Current.CancellationToken);

        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(2, info.ChannelCount);
        Assert.Equal(15_625d, info.DurationSeconds);
    }

    [Fact]
    public async Task Rf64DataLengthFromDs64_ReturnsDuration()
    {
        const long dataLength = 4_608_000_000L;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: true, dataChunkSize: uint.MaxValue, ds64DataLength: dataLength, dataLength);

        AudioArtifactValidator.AudioFileInfo info = await AudioArtifactValidator.ReadAndValidateAsync(stream, "normalized.wav", TestContext.Current.CancellationToken);

        Assert.Equal(24_000d, info.DurationSeconds);
    }

    [Fact]
    public async Task RiffDataChunkSizePlaceholder_ThrowsWithCause()
    {
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: uint.MaxValue, ds64DataLength: null, dataLength: 5_000_000_000L);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(stream, "normalized.wav", TestContext.Current.CancellationToken));

        Assert.Contains("0xFFFFFFFF placeholder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataLengthNotMultipleOfBlockAlign_Throws()
    {
        // Stereo 16-bit: blockAlign = 4. Use dataBytes = 6 (not a multiple of 4, but even so no pad-byte overflow).
        byte[] wav = BuildWav(channels: 2, dataBytes: 6);
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("not a multiple", ex.Message);
    }

    [Fact]
    public async Task UnsupportedAudioFormat_Throws()
    {
        byte[] wav = BuildWav(audioFormat: 3);
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("PCM", ex.Message);
    }

    [Fact]
    public async Task UnsupportedBitsPerSample_Throws()
    {
        byte[] wav = BuildWav(bitsPerSample: 8);
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("16-bit", ex.Message);
    }

    [Fact]
    public async Task InconsistentBlockAlign_Throws()
    {
        byte[] wav = BuildWav();
        // blockAlign is at offset 32; set it to a value inconsistent with channels*bytesPerSample
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 3);
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("inconsistent", ex.Message);
    }

    [Fact]
    public async Task MissingFmtChunk_Throws()
    {
        byte[] wav = BuildWavNoFmt();
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("fmt", ex.Message);
    }

    [Fact]
    public async Task MissingDataChunk_Throws()
    {
        byte[] wav = BuildWavNoData();
        string path = WriteWav(wav);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AudioArtifactValidator.ReadAndValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("no audio data", ex.Message);
    }

    // Builds a minimal valid 16-bit mono PCM WAV. Optional overrides corrupt specific fields.
    private static byte[] BuildWav(
        ushort audioFormat = 1,
        ushort channels = 1,
        int sampleRate = 16000,
        ushort bitsPerSample = 16,
        int? dataBytes = null,
        int? fmtSize = null)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        int actualDataBytes = dataBytes ?? blockAlign * 16; // 16 frames
        int actualFmtSize = fmtSize ?? 16;

        // RIFF(4) + fileSize(4) + WAVE(4) + fmt (4) + fmtSize(4) + fmt body(actualFmtSize) + data(4) + dataSize(4) + data body
        int totalSize = 12 + 8 + actualFmtSize + 8 + actualDataBytes;
        var bytes = new byte[totalSize];
        int pos = 0;

        Write4cc("RIFF", bytes, ref pos);
        WriteI32(totalSize - 8, bytes, ref pos);
        Write4cc("WAVE", bytes, ref pos);

        Write4cc("fmt ", bytes, ref pos);
        WriteI32(actualFmtSize, bytes, ref pos);
        if (actualFmtSize >= 2) WriteU16(audioFormat, bytes, ref pos);
        if (actualFmtSize >= 4) WriteU16(channels, bytes, ref pos);
        if (actualFmtSize >= 8) WriteI32(sampleRate, bytes, ref pos);
        if (actualFmtSize >= 12) WriteI32(sampleRate * blockAlign, bytes, ref pos);
        if (actualFmtSize >= 14) WriteU16((ushort)blockAlign, bytes, ref pos);
        if (actualFmtSize >= 16) WriteU16(bitsPerSample, bytes, ref pos);
        pos = 12 + 8 + actualFmtSize; // skip any extra fmt bytes

        Write4cc("data", bytes, ref pos);
        WriteI32(actualDataBytes, bytes, ref pos);

        return bytes;
    }

    // Builds a WAV with only a data chunk — no fmt chunk — to trigger the missing-fmt error.
    private static byte[] BuildWavNoFmt()
    {
        const int dataBytes = 32;
        // RIFF(4) + fileSize(4) + WAVE(4) + data(4) + dataSize(4) + data body
        int totalSize = 12 + 8 + dataBytes;
        var bytes = new byte[totalSize];
        int pos = 0;

        Write4cc("RIFF", bytes, ref pos);
        WriteI32(totalSize - 8, bytes, ref pos);
        Write4cc("WAVE", bytes, ref pos);

        Write4cc("data", bytes, ref pos);
        WriteI32(dataBytes, bytes, ref pos);

        return bytes;
    }

    // Builds a WAV with a fmt chunk but no data chunk — to trigger the missing-data error.
    private static byte[] BuildWavNoData()
    {
        const ushort audioFormat = 1;
        const ushort channels = 1;
        const int sampleRate = 16000;
        const ushort bitsPerSample = 16;
        const int fmtSize = 16;
        const int blockAlign = channels * (bitsPerSample / 8);

        // RIFF(4) + fileSize(4) + WAVE(4) + fmt (4) + fmtSize(4) + fmt body
        int totalSize = 12 + 8 + fmtSize;
        var bytes = new byte[totalSize];
        int pos = 0;

        Write4cc("RIFF", bytes, ref pos);
        WriteI32(totalSize - 8, bytes, ref pos);
        Write4cc("WAVE", bytes, ref pos);

        Write4cc("fmt ", bytes, ref pos);
        WriteI32(fmtSize, bytes, ref pos);
        WriteU16(audioFormat, bytes, ref pos);
        WriteU16(channels, bytes, ref pos);
        WriteI32(sampleRate, bytes, ref pos);
        WriteI32(sampleRate * blockAlign, bytes, ref pos);
        WriteU16(blockAlign, bytes, ref pos);
        WriteU16(bitsPerSample, bytes, ref pos);

        return bytes;
    }

    private static void Write4cc(string s, byte[] buf, ref int pos)
    {
        Encoding.ASCII.GetBytes(s).CopyTo(buf, pos);
        pos += 4;
    }

    private static void WriteI32(int v, byte[] buf, ref int pos)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), v);
        pos += 4;
    }

    private static void WriteU16(ushort v, byte[] buf, ref int pos)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), v);
        pos += 2;
    }
}
