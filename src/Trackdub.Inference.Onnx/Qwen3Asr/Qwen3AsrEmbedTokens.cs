using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal sealed class Qwen3AsrEmbedTokens
{
    private readonly float[,] embeddings;

    private Qwen3AsrEmbedTokens(float[,] embeddings)
    {
        this.embeddings = embeddings;
    }

    public int VocabularySize => embeddings.GetLength(0);

    public int HiddenSize => embeddings.GetLength(1);

    public static Qwen3AsrEmbedTokens Load(string embedTokensPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embedTokensPath);
        if (!File.Exists(embedTokensPath))
        {
            throw new FileNotFoundException("Qwen3-ASR embed_tokens.bin was not found.", embedTokensPath);
        }

        byte[] bytes = File.ReadAllBytes(embedTokensPath);
        int offset;
        int vocabSize;
        int hiddenSize;
        if (TryReadShapeHeader(bytes, out vocabSize, out hiddenSize))
        {
            offset = 8;
        }
        else if (TryInferMatrixShape(bytes.Length, out vocabSize, out hiddenSize))
        {
            offset = 0;
        }
        else
        {
            throw new InvalidDataException(
                $"embed_tokens.bin size {bytes.Length} is not a recognized float16 embedding matrix.");
        }

        int expectedBytes = offset + (vocabSize * hiddenSize * sizeof(ushort));
        if (bytes.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"embed_tokens.bin size {bytes.Length} does not match expected float16 matrix {vocabSize}x{hiddenSize}.");
        }

        var matrix = new float[vocabSize, hiddenSize];
        for (int tokenIndex = 0; tokenIndex < vocabSize; tokenIndex++)
        {
            for (int hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                ushort halfBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
                offset += 2;
                matrix[tokenIndex, hiddenIndex] = (float)BitConverter.UInt16BitsToHalf(halfBits);
            }
        }

        return new Qwen3AsrEmbedTokens(matrix);
    }

    public float[] Lookup(int tokenId)
    {
        var vector = new float[HiddenSize];
        Lookup(tokenId, vector.AsSpan());
        return vector;
    }

    /// <summary>Allocation-free lookup: writes the token row into <paramref name="destination"/>.</summary>
    public void Lookup(int tokenId, Span<float> destination)
    {
        if (tokenId < 0 || tokenId >= VocabularySize)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenId));
        }

        if (destination.Length != HiddenSize)
        {
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match hidden size {HiddenSize}.",
                nameof(destination));
        }

        for (int hiddenIndex = 0; hiddenIndex < HiddenSize; hiddenIndex++)
        {
            destination[hiddenIndex] = embeddings[tokenId, hiddenIndex];
        }
    }

    private static bool TryReadShapeHeader(ReadOnlySpan<byte> bytes, out int vocabSize, out int hiddenSize)
    {
        vocabSize = 0;
        hiddenSize = 0;
        if (bytes.Length < 8)
        {
            return false;
        }

        int candidateVocab = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        int candidateHidden = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
        int expectedBytes = 8 + (candidateVocab * candidateHidden * sizeof(ushort));
        if (candidateVocab <= 0 ||
            candidateHidden <= 0 ||
            bytes.Length != expectedBytes)
        {
            return false;
        }

        vocabSize = candidateVocab;
        hiddenSize = candidateHidden;
        return true;
    }

    private static bool TryInferMatrixShape(int byteLength, out int vocabSize, out int hiddenSize)
    {
        vocabSize = 0;
        hiddenSize = 0;
        if (byteLength % sizeof(ushort) != 0)
        {
            return false;
        }

        int elementCount = byteLength / sizeof(ushort);
        (int vocab, int hidden)[] knownShapes =
        [
            (151_936, 1024),
            (151_936, 2048),
        ];

        foreach ((int vocab, int hidden) in knownShapes)
        {
            if (vocab * hidden == elementCount)
            {
                vocabSize = vocab;
                hiddenSize = hidden;
                return true;
            }
        }

        return false;
    }
}
