namespace Trackdub.Inference.Onnx.NemotronAsr;

internal sealed class NemotronAsrSentencePieceVocab
{
    private readonly string[] pieces;
    private readonly HashSet<int> languageTagIds;

    internal NemotronAsrSentencePieceVocab(string[] pieces)
    {
        this.pieces = pieces;
        languageTagIds = pieces
            .Select((piece, index) => (piece, index))
            .Where(static item => IsLanguageTag(item.piece))
            .Select(static item => item.index)
            .ToHashSet();
    }

    public int Count => pieces.Length;

    public IReadOnlySet<int> LanguageTagIds => languageTagIds;

    public static async Task<NemotronAsrSentencePieceVocab> LoadAsync(
        string tokenizerPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        byte[] data = await File.ReadAllBytesAsync(tokenizerPath, cancellationToken).ConfigureAwait(false);
        string[] pieces = ParseSentencePieceModel(data).ToArray();
        if (pieces.Length == 0)
        {
            throw new InvalidOperationException("Nemotron tokenizer.model did not contain any SentencePiece pieces.");
        }

        return new NemotronAsrSentencePieceVocab(pieces);
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var builder = new System.Text.StringBuilder();
        foreach (int tokenId in tokenIds)
        {
            if (tokenId >= 0 && tokenId < pieces.Length)
            {
                builder.Append(pieces[tokenId].Replace('\u2581', ' '));
            }
        }

        return builder.ToString().TrimStart();
    }

    public string DecodeSingle(int tokenId) =>
        tokenId >= 0 && tokenId < pieces.Length
            ? pieces[tokenId].Replace('\u2581', ' ')
            : string.Empty;

    private static List<string> ParseSentencePieceModel(ReadOnlySpan<byte> data)
    {
        var pieces = new List<string>();
        int position = 0;
        while (position < data.Length)
        {
            if (!TryReadVarint(data[position..], out ulong header, out int headerBytes))
            {
                return pieces;
            }

            position += headerBytes;
            ulong fieldNumber = header >> 3;
            ulong wireType = header & 0x7;
            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadVarint(data[position..], out ulong length, out int lengthBytes))
                {
                    return pieces;
                }

                position += lengthBytes;
                if (length > int.MaxValue || position + (int)length > data.Length)
                {
                    return pieces;
                }

                string? piece = ParsePieceMessage(data.Slice(position, (int)length));
                position += (int)length;
                if (piece is not null)
                {
                    pieces.Add(piece);
                }

                continue;
            }

            if (!SkipUnknownField(data, ref position, wireType))
            {
                return pieces;
            }
        }

        return pieces;
    }

    private static string? ParsePieceMessage(ReadOnlySpan<byte> data)
    {
        string? piece = null;
        int position = 0;
        while (position < data.Length)
        {
            if (!TryReadVarint(data[position..], out ulong header, out int headerBytes))
            {
                return piece;
            }

            position += headerBytes;
            ulong fieldNumber = header >> 3;
            ulong wireType = header & 0x7;
            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadVarint(data[position..], out ulong length, out int lengthBytes))
                {
                    return piece;
                }

                position += lengthBytes;
                if (length > int.MaxValue || position + (int)length > data.Length)
                {
                    return piece;
                }

                piece = System.Text.Encoding.UTF8.GetString(data.Slice(position, (int)length));
                position += (int)length;
                continue;
            }

            if (!SkipUnknownField(data, ref position, wireType))
            {
                return piece;
            }
        }

        return piece;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        int shift = 0;
        while (bytesRead < data.Length && bytesRead < 10)
        {
            byte current = data[bytesRead];
            value |= (ulong)(current & 0x7F) << shift;
            bytesRead++;
            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static bool SkipUnknownField(ReadOnlySpan<byte> data, ref int position, ulong wireType)
    {
        switch (wireType)
        {
            case 0:
                if (!TryReadVarint(data[position..], out _, out int varintBytes))
                {
                    return false;
                }

                position += varintBytes;
                return position <= data.Length;
            case 1:
                position += 8;
                return position <= data.Length;
            case 2:
                if (!TryReadVarint(data[position..], out ulong length, out int lengthBytes) ||
                    length > int.MaxValue)
                {
                    return false;
                }

                position += lengthBytes + (int)length;
                return position <= data.Length;
            case 5:
                position += 4;
                return position <= data.Length;
            default:
                return false;
        }
    }

    private static bool IsLanguageTag(string piece)
    {
        if (piece.Length < 4 || piece[0] != '<' || piece[^1] != '>')
        {
            return false;
        }

        ReadOnlySpan<char> inner = piece.AsSpan(1, piece.Length - 2);
        return inner.Length switch
        {
            2 => char.IsAsciiLetterLower(inner[0]) && char.IsAsciiLetterLower(inner[1]),
            5 => char.IsAsciiLetterLower(inner[0]) &&
                 char.IsAsciiLetterLower(inner[1]) &&
                 inner[2] == '-' &&
                 char.IsAsciiLetterUpper(inner[3]) &&
                 char.IsAsciiLetterUpper(inner[4]),
            _ => false
        };
    }
}
