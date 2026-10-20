using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.QwenTextRefinement;

public sealed record QwenRefinementGuardResult(
    string CleanedOutput,
    bool Accepted,
    TextRefinementGuardStatus GuardStatus,
    string DisplayedText,
    IReadOnlyList<string> AppliedCorrections);

public static partial class QwenRefinementOutputGuard
{
    private static readonly string[] ExplanationPatterns =
    [
        "here is",
        "here's",
        "the polished",
        "polished text",
        "polished transcript",
        "corrected text",
        "as an ai",
        "i have polished"
    ];

    private static readonly string[] SpecialTokenMarkers =
    [
        "<|im_start|>",
        "<|im_end|>",
        "<|endoftext|>",
        "<|assistant|>",
        "<|system|>",
        "<|user|>"
    ];

    public static QwenRefinementGuardResult Evaluate(string originalText, string modelOutput)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(modelOutput);

        var corrections = new List<string>();
        string cleaned = CleanModelOutput(modelOutput, corrections);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.ExplanationOutputRejected);
        }

        if (ContainsExplanationPattern(cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.ExplanationOutputRejected);
        }

        if (ContainsFormatViolation(cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.FormatGuardTriggered);
        }

        if (ContainsMultiSegmentLeakage(originalText, cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.FormatGuardTriggered);
        }

        if (TriggersLengthGuard(originalText, cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.LengthGuardTriggered);
        }

        if (TriggersEditDistanceGuard(originalText, cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.LengthGuardTriggered);
        }

        if (TriggersNameNumberGuard(originalText, cleaned))
        {
            return Fallback(originalText, TextRefinementCorrectionCodes.NameNumberGuardTriggered);
        }

        if (string.Equals(NormalizeWhitespace(originalText), NormalizeWhitespace(cleaned), StringComparison.Ordinal))
        {
            return new QwenRefinementGuardResult(
                cleaned,
                Accepted: false,
                TextRefinementGuardStatus.Unchanged,
                DisplayedText: originalText,
                [TextRefinementCorrectionCodes.FallbackUnchanged]);
        }

        return new QwenRefinementGuardResult(
            cleaned,
            Accepted: true,
            TextRefinementGuardStatus.Accepted,
            DisplayedText: cleaned,
            [TextRefinementCorrectionCodes.ModelPolishApplied]);
    }

    private static QwenRefinementGuardResult Fallback(string originalText, string reasonCode) =>
        new(
            originalText,
            Accepted: false,
            TextRefinementGuardStatus.Rejected,
            DisplayedText: originalText,
            [TextRefinementCorrectionCodes.FallbackUnchanged, reasonCode]);

    private static string CleanModelOutput(string modelOutput, List<string> corrections)
    {
        string result = modelOutput.Trim();
        bool stripped = false;

        foreach (string marker in SpecialTokenMarkers)
        {
            if (result.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(marker, string.Empty, StringComparison.OrdinalIgnoreCase);
                stripped = true;
            }
        }

        result = result.Trim().Trim('"', '\'', '`');
        if (stripped && !string.IsNullOrWhiteSpace(result))
        {
            corrections.Add(TextRefinementCorrectionCodes.SpecialTokenStripped);
        }

        return result.Trim();
    }

    private static bool ContainsExplanationPattern(string text)
    {
        string lower = text.ToLowerInvariant();
        return ExplanationPatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal));
    }

    private static bool ContainsFormatViolation(string text)
    {
        if (SpeakerLabelRegex().IsMatch(text))
        {
            return true;
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            return true;
        }

        return MarkdownListRegex().IsMatch(text);
    }

    private static bool ContainsMultiSegmentLeakage(string originalText, string cleaned)
    {
        if (!originalText.Contains('\n', StringComparison.Ordinal) &&
            cleaned.Contains("\n\n", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool TriggersLengthGuard(string originalText, string cleaned)
    {
        int inputLength = originalText.Length;
        if (inputLength == 0)
        {
            return false;
        }

        int maxLength = Math.Min((int)Math.Ceiling(inputLength * 1.35), inputLength + 80);
        return cleaned.Length > maxLength;
    }

    private static bool TriggersEditDistanceGuard(string originalText, string cleaned)
    {
        if (originalText.Length >= 40)
        {
            return false;
        }

        // Punctuation and case are the primary polish operations. Measure
        // lexical changes so short replies such as "hi" -> "Hi." remain valid.
        string originalWords = NormalizeForEditDistance(originalText);
        string cleanedWords = NormalizeForEditDistance(cleaned);
        double ratio = NormalizedLevenshteinRatio(originalWords, cleanedWords);
        return ratio > 0.45;
    }

    private static string NormalizeForEditDistance(string text) =>
        string.Join(" ", WordTokenRegex().Matches(text).Select(match => match.Value)).ToLowerInvariant();

    private static bool TriggersNameNumberGuard(string originalText, string cleaned)
    {
        IReadOnlyList<string> originalNumbers = ExtractNumericTokens(originalText);
        IReadOnlyList<string> cleanedNumbers = ExtractNumericTokens(cleaned);
        if (!NumericTokenSetsEqual(originalNumbers, cleanedNumbers))
        {
            return true;
        }

        // Capitalization is a requested correction, not evidence of a new name.
        // Protect name-like words from either text by comparing their occurrences
        // against all words in the other text, independent of capitalization.
        HashSet<string> protectedNames = ExtractNameLikeTokens(originalText)
            .Concat(ExtractNameLikeTokens(cleaned))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> originalNames = WordTokenRegex().Matches(originalText)
            .Select(match => match.Value).Where(protectedNames.Contains);
        IEnumerable<string> cleanedNames = WordTokenRegex().Matches(cleaned)
            .Select(match => match.Value).Where(protectedNames.Contains);
        return !originalNames.SequenceEqual(cleanedNames, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractNumericTokens(string text)
    {
        MatchCollection matches = NumericTokenRegex().Matches(text);
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            tokens.Add(NormalizeNumericToken(match.Value));
        }

        return tokens;
    }

    private static bool NumericTokenSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        string[] sortedLeft = left.OrderBy(static token => token, StringComparer.Ordinal).ToArray();
        string[] sortedRight = right.OrderBy(static token => token, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < sortedLeft.Length; index++)
        {
            if (!string.Equals(sortedLeft[index], sortedRight[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeNumericToken(string token) =>
        token.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('.', ';', ':');

    private static IReadOnlyList<string> ExtractNameLikeTokens(string text)
    {
        MatchCollection matches = NameLikeTokenRegex().Matches(text);
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            tokens.Add(NormalizeNameToken(match.Value));
        }

        return tokens;
    }

    private static string NormalizeNameToken(string token)
    {
        string trimmed = token.Trim().TrimEnd('.', ',', ';', ':');
        return trimmed.ToLowerInvariant();
    }

    private static string NormalizeWhitespace(string text) =>
        WhitespaceRegex().Replace(text.Trim(), " ");

    private static double NormalizedLevenshteinRatio(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        int distance = LevenshteinDistance(left, right);
        int maxLength = Math.Max(left.Length, right.Length);
        return maxLength == 0 ? 0 : (double)distance / maxLength;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        int[,] distances = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (int j = 0; j <= right.Length; j++)
        {
            distances[0, j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[left.Length, right.Length];
    }

    [GeneratedRegex(@"(?i)(?:speaker\s*\d+\s*:|^\s*\[[^\]]+\]\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLabelRegex();

    [GeneratedRegex(@"(?m)^\s*[-*]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownListRegex();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)*\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTokenRegex();

    [GeneratedRegex(@"\b\p{Lu}[\p{Ll}\p{M}]+(?:['’][\p{L}\p{M}]+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex NameLikeTokenRegex();

    [GeneratedRegex(@"\b\p{L}[\p{L}\p{M}]*(?:['’][\p{L}\p{M}]+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
