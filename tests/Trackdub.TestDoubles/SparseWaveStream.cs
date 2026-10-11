using System.Text;

namespace Trackdub.TestDoubles;

/// <summary>
/// A read-only seekable stream that reports a multi-GiB WAV length without backing storage: a real header,
/// virtual silence for the data chunk, then a trailing LIST chunk so parsers must seek past the oversized
/// data chunk and land on a valid chunk header.
/// </summary>
public sealed class SparseWaveStream : Stream
{
    private readonly byte[] prefix;
    private readonly long trailerOffset;
    private readonly byte[] trailer;
    private long position;

    private SparseWaveStream(byte[] prefix, long trailerOffset, byte[] trailer)
    {
        this.prefix = prefix;
        this.trailerOffset = trailerOffset;
        this.trailer = trailer;
    }

    /// <summary>Byte offset of the PCM data for <see cref="CreatePcm16Stereo48k"/> RIFF headers.</summary>
    public const long RiffDataStart = 44;

    /// <summary>Byte offset of the PCM data for <see cref="CreatePcm16Stereo48k"/> RF64 headers with a ds64 chunk.</summary>
    public const long Rf64DataStart = 80;

    /// <summary>
    /// 48 kHz stereo PCM16. <paramref name="dataChunkSize"/> is the 32-bit size field written to the data chunk;
    /// <paramref name="ds64DataLength"/> adds a ds64 chunk carrying the 64-bit size; <paramref name="dataLength"/>
    /// is how many data bytes the stream actually spans.
    /// </summary>
    public static SparseWaveStream CreatePcm16Stereo48k(
        bool rf64,
        uint dataChunkSize,
        long? ds64DataLength,
        long dataLength)
    {
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(rf64 ? "RF64"u8 : "RIFF"u8);
            writer.Write(uint.MaxValue);
            writer.Write("WAVE"u8);
            if (ds64DataLength is long ds64Length)
            {
                writer.Write("ds64"u8);
                writer.Write(28u);
                writer.Write(0UL);
                writer.Write((ulong)ds64Length);
                writer.Write(0UL);
                writer.Write(0u);
            }

            writer.Write("fmt "u8);
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Write(48000);
            writer.Write(48000 * 4);
            writer.Write((ushort)4);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write(dataChunkSize);
        }

        byte[] prefix = header.ToArray();
        byte[] trailer = [.. "LIST"u8, 4, 0, 0, 0, .. "INFO"u8];
        return new SparseWaveStream(prefix, prefix.Length + dataLength, trailer);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => trailerOffset + trailer.Length;

    public override long Position
    {
        get => position;
        set
        {
            // Mirror FileStream so a negative seek fails the way it does on disk.
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, Math.Max(0, Length - position));
        for (int i = 0; i < count; i++, position++)
        {
            buffer[i] = position < prefix.Length
                ? prefix[position]
                : position >= trailerOffset ? trailer[position - trailerOffset] : (byte)0;
        }

        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override long Seek(long offset, SeekOrigin origin) =>
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => Length + offset,
        };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
