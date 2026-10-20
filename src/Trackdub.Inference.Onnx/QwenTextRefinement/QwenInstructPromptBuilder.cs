using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.QwenTextRefinement;

public static class QwenInstructPromptBuilder
{
    private const string AsrPolishSystemPrompt =
        """
        You polish automatic speech recognition transcript text.
        Fix punctuation, capitalization, and obvious grammar errors.
        Remove only obvious ASR artifacts such as duplicated phrase loops or impossible repeated fragments.
        When unsure, preserve the original wording.
        Do not remove natural speech filler unless it is an obvious duplicated loop.
        Do not add, remove, or change factual meaning. Preserve names and numbers exactly.
        Output only the polished transcript text — no explanation, labels, quotes, or speaker tags.
        """;

    public static string BuildAsrPolishPrompt(string segmentText, string? sourceLanguage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentText);

        string languagePrefix = string.IsNullOrWhiteSpace(sourceLanguage)
            ? string.Empty
            : $"The transcript language is {sourceLanguage.Trim()}.\n";

        return
            $"<|im_start|>system\n{AsrPolishSystemPrompt}<|im_end|>\n" +
            $"<|im_start|>user\n{languagePrefix}{segmentText}<|im_end|>\n" +
            "<|im_start|>assistant\n";
    }

    public static string BuildTranslationPolishPrompt(string segmentText, string? targetLanguage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentText);

        string languageSuffix = string.IsNullOrWhiteSpace(targetLanguage)
            ? string.Empty
            : $" Keep the output in {targetLanguage.Trim()}.";

        const string systemPrompt =
            """
            You polish translated transcript text for fluency and readability.
            Fix punctuation, capitalization, and obvious grammar errors.
            When unsure, preserve the original wording.
            Do not add, remove, or change factual meaning. Preserve names and numbers exactly.
            Output only the polished translation text — no explanation, labels, quotes, or speaker tags.
            """;

        return
            $"<|im_start|>system\n{systemPrompt}{languageSuffix}<|im_end|>\n" +
            $"<|im_start|>user\n{segmentText}<|im_end|>\n" +
            "<|im_start|>assistant\n";
    }

    public static string BuildPrompt(TextRefinementScope scope, string segmentText, string? sourceLanguage, string? targetLanguage) =>
        scope switch
        {
            TextRefinementScope.Asr => BuildAsrPolishPrompt(segmentText, sourceLanguage),
            TextRefinementScope.Translation => BuildTranslationPolishPrompt(segmentText, targetLanguage),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported text refinement scope.")
        };
}
