using System.Buffers.Binary;
using System.Reflection;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Madlad;
using Trackdub.Inference.Onnx.OpusMt;
using Trackdub.Inference.Onnx.SileroVad;
using Trackdub.Inference.Onnx.NemotronAsr;
using Trackdub.Inference.Onnx.Qwen3Asr;
using Trackdub.Inference.Onnx.Whisper;
using Trackdub.Inference.Pipelines.Transcript;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class OnnxTranscriptEnginesTests
{
    [Fact]
    public void WhisperGenAi_EncodeClip_ProducesInMemoryWavOfExactRange()
    {
        // 0.5 s of a ramp at 16 kHz; encode only the middle quarter.
        const int sampleRate = 16000;
        var samples = new float[8000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp((i / (float)samples.Length) - 0.5f, -1f, 1f);
        }

        using var audio = new MemoryAudioSamples(samples);
        byte[] wav = WhisperGenAiAudioTranscriptionEngine.EncodeClip(audio, 0.125, 0.375, CancellationToken.None);

        int expectedSamples = 4000; // [0.125, 0.375) s at 16 kHz
        Assert.Equal(44 + (expectedSamples * 2), wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav.AsSpan(0, 4)));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav.AsSpan(8, 4)));
        Assert.Equal(36 + (expectedSamples * 2), BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4, 4)));
        Assert.Equal(sampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal(expectedSamples * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));

        // First encoded sample must be source[2000] = 0.125*16000 = index 2000.
        short first = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44, 2));
        short expectedFirst = (short)Math.Clamp(samples[2000] * 32767f, short.MinValue, short.MaxValue);
        Assert.Equal(expectedFirst, first);
        // Reads must be limited to the encoded window, not the whole buffer.
        Assert.Equal(2000, audio.MinRead);
        Assert.True(audio.MaxRead <= 6000);
    }

    [Fact]
    public void WhisperGenAi_EncodeClip_PreCanceledToken_ThrowsEvenForEmptyRange()
    {
        using var audio = new MemoryAudioSamples(new float[1600]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WhisperGenAiAudioTranscriptionEngine.EncodeClip(audio, 2.0, 1.0, canceled.Token));
    }

    [Fact]
    public void WhisperGenAi_EncodeClip_EmptyRange_ProducesValidEmptyWav()
    {
        using var audio = new MemoryAudioSamples(new float[1600]);
        byte[] wav = WhisperGenAiAudioTranscriptionEngine.EncodeClip(audio, 2.0, 1.0, CancellationToken.None);
        Assert.Equal(44, wav.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));
    }

    private sealed class MemoryAudioSamples(float[] data) : IAudioSamples
    {
        public int SampleRate => 16000;

        public long SampleFrameCount => data.Length;

        public long MinRead { get; private set; } = long.MaxValue;

        public long MaxRead { get; private set; }

        public void ReadMonoSamples(long startFrame, Span<float> destination)
        {
            MinRead = Math.Min(MinRead, startFrame);
            MaxRead = Math.Max(MaxRead, startFrame + destination.Length);
            data.AsSpan((int)startFrame, destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_MergesShortNearbyVadRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [
                new SpeechRegion(0, 4.03, 4.38),
                new SpeechRegion(1, 4.74, 5.15),
                new SpeechRegion(2, 5.47, 6.01),
                new SpeechRegion(3, 6.59, 6.88),
                new SpeechRegion(4, 8.00, 8.61),
                new SpeechRegion(5, 8.74, 10.30),
                new SpeechRegion(6, 10.47, 10.72),
                new SpeechRegion(7, 12.13, 12.99),
                new SpeechRegion(8, 14.34, 16.06),
                new SpeechRegion(9, 16.26, 16.57)
            ],
            durationSeconds: 20.0);

        SpeechRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Index);
        Assert.Equal(3.53, region.StartSeconds, precision: 2);
        Assert.Equal(17.07, region.EndSeconds, precision: 2);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_ExpandsIsolatedShortRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [new SpeechRegion(8, 10.0, 10.4)],
            durationSeconds: 30.0);

        SpeechRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Index);
        Assert.Equal(3.0, region.EndSeconds - region.StartSeconds, precision: 2);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_AvoidsOverlappingExpandedRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [
                new SpeechRegion(0, 0.0, 0.3),
                new SpeechRegion(1, 2.0, 2.3)
            ],
            durationSeconds: 10.0);

        Assert.NotEmpty(regions);
        for (int index = 1; index < regions.Count; index++)
        {
            Assert.True(regions[index - 1].EndSeconds <= regions[index].StartSeconds);
        }
    }

    [Fact]
    public async Task ScriptedAudioTranscriptionEngine_EmitsWordConfidenceRows()
    {
        var engine = new ScriptedAudioTranscriptionEngine();

        IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
            "normalized.wav",
            [new SpeechRegion(1, 2.0, 5.0)],
            CancellationToken.None);

        RecognizedTranscriptSegment segment = Assert.Single(segments);
        Assert.NotEmpty(segment.Words);
        Assert.All(segment.Words, word =>
        {
            Assert.InRange(word.Confidence!.Value, 0d, 1d);
            Assert.InRange(word.StartSeconds, 2.0, 5.0);
            Assert.InRange(word.EndSeconds, 2.0, 5.0);
        });
        Assert.Contains(segment.Words, word => Math.Abs(word.Confidence!.Value - 0.68d) < 0.0001d);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildRecognizedWords_PreservesTimingAndConfidence()
    {
        IReadOnlyList<RecognizedTranscriptWord> words = WhisperOnnxAudioTranscriptionEngine.BuildRecognizedWordsForTesting(
            "hello world",
            10.0d,
            12.0d,
            [0.80d, 0.60d]);

        Assert.Collection(
            words,
            first =>
            {
                Assert.Equal(0, first.WordIndex);
                Assert.Equal("hello", first.Text);
                Assert.Equal(10.0d, first.StartSeconds);
                Assert.Equal(11.0d, first.EndSeconds);
                Assert.Equal(0.70d, first.Confidence!.Value, precision: 3);
            },
            second =>
            {
                Assert.Equal(1, second.WordIndex);
                Assert.Equal("world", second.Text);
                Assert.Equal(11.0d, second.StartSeconds);
                Assert.Equal(12.0d, second.EndSeconds);
                Assert.Equal(0.70d, second.Confidence!.Value, precision: 3);
            });
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_TrimsRunawayTail()
    {
        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting(
                [9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2, 3]);

        Assert.True(repetitionGuarded);
        Assert.Equal([9, 8, 7, 1, 2, 3], tokens);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_LeavesNonTailShortRepeats()
    {
        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting(
                [9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 42]);

        Assert.False(repetitionGuarded);
        Assert.Equal([9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 42], tokens);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_DetectsLargeNgramLoop()
    {
        // 15-token pattern repeated twice — too large for the original 3..8 ngram guard.
        int[] pattern = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115];

        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting([.. pattern, .. pattern]);

        Assert.True(repetitionGuarded);
        Assert.Equal(pattern, tokens);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(". . .", true)]
    [InlineData("。。。", true)]
    [InlineData("。 。 。", true)]
    [InlineData("...", true)]
    [InlineData("S S S", false)]
    [InlineData("Pas. . .", false)]
    [InlineData("hello world", false)]
    [InlineData("Hello, world!", false)]
    public void WhisperGenAiAudioTranscriptionEngine_IsDegenerateTranscriptText(
        string? text,
        bool expectedDegenerate)
    {
        Assert.Equal(expectedDegenerate, WhisperGenAiAudioTranscriptionEngine.IsDegenerateTranscriptText(text));
    }

#if WINDOWS
    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_CleansSpecialTokensAndInfersLanguage()
    {
        const string decoded = "<|startoftranscript|><|en|><|transcribe|><|notimestamps|> hello world <|endoftext|>";

        Assert.Equal("hello world", WhisperGenAiAudioTranscriptionEngine.CleanDecodedText(decoded));
        Assert.Equal("en", WhisperGenAiAudioTranscriptionEngine.TryInferDetectedLanguage(decoded));
    }

    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_InfersLanguageFromTokenIds()
    {
        // ONNX Runtime GenAI strips special tokens when decoding to text, so the spoken
        // language must be read from the raw token-id sequence. <|startoftranscript|> (50258)
        // is not a language token and must be skipped so the first real language token wins.
        var languageTokensById = new Dictionary<int, string>
        {
            [50259] = "en",
            [50262] = "es",
        };

        Assert.Equal(
            "es",
            WhisperGenAiAudioTranscriptionEngine.InferLanguageFromTokenIds(
                [50258, 50262, 50359, 1000, 2000],
                languageTokensById));
        Assert.Null(
            WhisperGenAiAudioTranscriptionEngine.InferLanguageFromTokenIds(
                [50258, 50359, 1000],
                languageTokensById));
    }

    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_UsesRequiredAudioProcessorPrompt()
    {
        Assert.Equal(
            "<|startoftranscript|><|transcribe|><|notimestamps|>",
            WhisperGenAiAudioTranscriptionEngine.GetAudioProcessorPromptForTesting());
        Assert.Equal(
            "<|startoftranscript|>",
            WhisperGenAiAudioTranscriptionEngine.GetLanguageDetectionPromptForTesting());
    }

    [Theory]
    [InlineData("es", "<|startoftranscript|><|es|><|transcribe|><|notimestamps|>")]
    [InlineData("ES", "<|startoftranscript|><|es|><|transcribe|><|notimestamps|>")]
    [InlineData(null, "<|startoftranscript|><|transcribe|><|notimestamps|>")]
    [InlineData("", "<|startoftranscript|><|transcribe|><|notimestamps|>")]
    public void WhisperGenAiAudioTranscriptionEngine_BuildsSourceLanguageTranscriptionPrompt(
        string? detectedLanguage,
        string expectedPrompt)
    {
        Assert.Equal(
            expectedPrompt,
            WhisperGenAiAudioTranscriptionEngine.BuildTranscriptionPromptForTesting(detectedLanguage));
    }

    [RequiresBundledModelFact("whisper-tiny-genai/genai_config.json", "whisper-tiny-genai/audio_processor_config.json", "whisper-tiny-genai/encoder.onnx", "whisper-tiny-genai/decoder.onnx")]
    public async Task WhisperGenAiAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new WhisperGenAiAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "openai/whisper-tiny",
                    ModelAlias = "whisper-tiny-genai",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [ new SpeechRegion(0, 0.0, 0.8) ],
                CancellationToken.None);

            Assert.All(segments, static segment => Assert.False(string.IsNullOrWhiteSpace(segment.Text)));
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
            Assert.Equal("whisper-tiny-genai", engine.LastExecutionSummary.ModelAlias);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("whisper-tiny-genai/tokenizer.json")]
    public async Task WhisperGenAiAudioTranscriptionEngine_LoadsLanguageTokenIdsFromBundledTokenizer()
    {
        string modelRoot = Path.Join(TestRepoRootResolver.FindRepoRoot(), "models", "whisper-tiny-genai");

        IReadOnlyDictionary<int, string> languageTokensById =
            await WhisperGenAiAudioTranscriptionEngine.LoadLanguageTokenIdsAsync(modelRoot);

        // Real-model guard for token-id-based language detection: if this id<->code mapping ever
        // drifts (different model export, vocab change), auto-detect would silently mislabel the
        // language. These ids are the canonical whisper language tokens (<|en|>=50259, <|es|>=50262).
        Assert.Equal("en", languageTokensById[50259]);
        Assert.Equal("es", languageTokensById[50262]);
        Assert.True(
            languageTokensById.Count >= 90,
            $"Expected ~99 whisper language tokens, found {languageTokensById.Count}.");
    }
#endif

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task SileroVadSpeechRegionDetector_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var detector = new SileroVadSpeechRegionDetector(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Vad,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "onnx-community/silero-vad",
                    ModelAlias = "silero-vad",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<SpeechRegion> regions = await detector.DetectAsync(wavePath, 1.0, CancellationToken.None);

            Assert.NotNull(regions);
            Assert.NotNull(detector.LastExecutionSummary);
            Assert.Equal("cpu", detector.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [Fact]
    public void Qwen3AsrPromptBuilder_UsesChatTemplateRoleTokens()
    {
        IReadOnlyList<int> ids = Qwen3AsrPromptBuilder.BuildPromptIds(audioTokenCount: 2);

        // Chat template: im_start "system" NL im_end NL im_start "user" NL audio_start ... (vocab.json ids).
        Assert.Equal(
            [
                Qwen3AsrPromptTokens.ImStartTokenId, 8948, 198, Qwen3AsrPromptTokens.ImEndTokenId, 198,
                Qwen3AsrPromptTokens.ImStartTokenId, 872, 198, Qwen3AsrPromptTokens.AudioStartTokenId,
                Qwen3AsrPromptTokens.AudioPadTokenId, Qwen3AsrPromptTokens.AudioPadTokenId,
                Qwen3AsrPromptTokens.AudioEndTokenId, Qwen3AsrPromptTokens.ImEndTokenId, 198,
                Qwen3AsrPromptTokens.ImStartTokenId, 77091, 198,
            ],
            ids);
    }

    [Fact]
    public void Qwen3AsrMelFeatureExtractor_MatchesWhisperFrameCount()
    {
        Assert.Equal(100, new Qwen3AsrMelFeatureExtractor().Extract(new float[16_000]).Dimensions[2]);
    }

    [Fact]
    public void Qwen3AsrFeatureLengths_MatchesReferenceFormula()
    {
        Assert.Equal(1, Qwen3AsrFeatureLengths.GetEncoderOutputLength(1));
        Assert.Equal(13, Qwen3AsrFeatureLengths.GetEncoderOutputLength(100));
        Assert.Equal(26, Qwen3AsrFeatureLengths.GetEncoderOutputLength(200));
    }

    [Fact]
    public void Qwen3AsrOutputParser_ParsesLanguageTaggedOutput()
    {
        // Real Qwen3-ASR auto-detect output: the language metadata line plus the
        // "<asr_text>" marker (no pipes — see Qwen3AsrPromptTokens.AsrTextTag).
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "language English<asr_text>Hello world");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
        Assert.Equal("en", Qwen3AsrLanguageCodes.TryGetIsoCode(language));
    }

    [Fact]
    public void Qwen3AsrOutputParser_DoesNotLeakLanguageMetadataIntoText()
    {
        // Regression guard for the "<asr_text>" tag-format bug: the metadata prefix and
        // marker must never appear in the transcript text shown to the user.
        (_, string text) = Qwen3AsrOutputParser.Parse(
            "language Chinese<asr_text>对。");

        Assert.DoesNotContain("language", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asr_text", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("对。", text);
    }

    [Fact]
    public void Qwen3AsrOutputParser_StripsTrailingEosTagOnForcedLanguagePath()
    {
        // Forced-language path returns the decode verbatim; the greedy decoder appends the
        // terminal EOS token which tokenizer.Decode renders as literal "<|im_end|>".
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "Hello world<|im_end|>", forcedLanguageName: "English");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void Qwen3AsrOutputParser_StripsControlTokensFromTaggedOutput()
    {
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "language English<asr_text>Hello world<|endoftext|>");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void NemotronAsrLanguagePrompts_ResolveSupportedLanguagesAndFallbackToAuto()
    {
        Assert.Equal(0, NemotronAsrLanguagePrompts.ResolvePromptIndex("en-US"));
        Assert.Equal(2, NemotronAsrLanguagePrompts.ResolvePromptIndex("es-ES"));
        Assert.Equal(NemotronAsrLanguagePrompts.AutoPromptIndex, NemotronAsrLanguagePrompts.ResolvePromptIndex("zz-ZZ"));
        Assert.Equal("en", NemotronAsrLanguagePrompts.TryGetIsoCode("en-US"));
        Assert.Null(NemotronAsrLanguagePrompts.TryGetIsoCode("zz-ZZ"));
    }

    [Fact]
    public async Task NemotronAsrLanguagePrompts_LoadsBundlePromptDictionary()
    {
        string configPath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "prompt_dictionary": {
                    "auto": 101,
                    "id-ID": 34,
                    "fr-CA": 100
                  }
                }
                """,
                CancellationToken.None);

            NemotronAsrPromptDictionary promptDictionary = await NemotronAsrLanguagePrompts
                .LoadAsync(configPath, CancellationToken.None);

            Assert.Equal(101, promptDictionary.AutoPromptIndex);
            Assert.Equal(34, promptDictionary.ResolvePromptIndex("id"));
            Assert.Equal(100, promptDictionary.ResolvePromptIndex("fr_CA"));
            Assert.Equal(101, promptDictionary.ResolvePromptIndex("zz-ZZ"));
            Assert.Equal("id", promptDictionary.TryGetIsoCode("id"));
            Assert.Null(promptDictionary.TryGetIsoCode("zz-ZZ"));
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task NemotronAsrLanguagePrompts_LoadsAutoPromptIndexFromDictionary()
    {
        string configPath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "prompt_dictionary": {
                    "auto": 999,
                    "en-US": 0
                  }
                }
                """,
                CancellationToken.None);

            NemotronAsrPromptDictionary promptDictionary = await NemotronAsrLanguagePrompts
                .LoadAsync(configPath, CancellationToken.None);

            Assert.Equal(999, promptDictionary.AutoPromptIndex);
            Assert.Equal(999, promptDictionary.ResolvePromptIndex(null));
            Assert.Equal(999, promptDictionary.ResolvePromptIndex("zz-ZZ"));
            Assert.Null(promptDictionary.TryGetIsoCode("zz-ZZ"));
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public void NemotronAsrEncoderTrtProfiles_OmitsPromptIndexWhenEncoderDoesNotExposeIt()
    {
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(hasPromptInput: false);

        Assert.DoesNotContain("prompt_index", options["trt_profile_min_shapes"], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_index", options["trt_profile_max_shapes"], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_index", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
    }

    [Fact]
    public void NemotronAsrEncoderTrtProfiles_IncludesPromptIndexWhenEncoderExposesIt()
    {
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(hasPromptInput: true);

        Assert.Contains("prompt_index:1", options["trt_profile_min_shapes"], StringComparison.Ordinal);
        Assert.Contains("prompt_index:1", options["trt_profile_max_shapes"], StringComparison.Ordinal);
        Assert.Contains("prompt_index:1", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
    }

    [RequiresBundledModelFact(
        "nemotron-3.5-asr-onnx/encoder.onnx")]
    public void NemotronAsrEncoderTrtProfiles_MatchesBundledEncoderPromptInput()
    {
        string encoderPath = BenchmarkModelPathResolver.CreateDefault()
            .ResolveSingle("nemotron-3.5-asr-onnx/encoder.onnx")
            .ModelPath;

        bool hasPromptInput = NemotronAsrEncoderTrtProfiles.EncoderHasPromptInput(encoderPath);
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(encoderPath);

        if (hasPromptInput)
        {
            Assert.Contains("prompt_index:1", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("prompt_index", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NemotronAsrMelFeatureExtractor_ProducesChunkableMelFrames()
    {
        var extractor = new NemotronAsrMelFeatureExtractor();
        float[,] mel = extractor.Extract(new float[16000]);

        Assert.Equal(NemotronAsrMelFeatureExtractor.MelBins, mel.GetLength(0));
        Assert.True(mel.GetLength(1) > NemotronAsrMelFeatureExtractor.ChunkFrames);

        float[] chunk = NemotronAsrMelFeatureExtractor.BuildChunk(
            mel,
            frameOffset: 0,
            mainFrameCount: NemotronAsrMelFeatureExtractor.ChunkFrames,
            includePreEncodeCache: false);

        Assert.Equal(
            NemotronAsrMelFeatureExtractor.MelBins * NemotronAsrMelFeatureExtractor.ChunkInputFrames,
            chunk.Length);
    }

    [Fact]
    public void NemotronAsrOnnxAudioTranscriptionEngine_PreservesCallerSpeechRegions()
    {
        IReadOnlyList<SpeechRegion> regions = NemotronAsrOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
        [
            new SpeechRegion(42, 10.0, 10.8),
            new SpeechRegion(7, 1.5, 2.2),
            new SpeechRegion(99, 5.0, 5.0),
        ]);

        Assert.Collection(
            regions,
            first =>
            {
                Assert.Equal(7, first.Index);
                Assert.Equal(1.5, first.StartSeconds);
                Assert.Equal(2.2, first.EndSeconds);
            },
            second =>
            {
                Assert.Equal(42, second.Index);
                Assert.Equal(10.0, second.StartSeconds);
                Assert.Equal(10.8, second.EndSeconds);
            });
    }

    [Fact]
    public void Qwen3AsrOnnxAudioTranscriptionEngine_PreservesCallerSpeechRegions()
    {
        IReadOnlyList<SpeechRegion> regions = Qwen3AsrOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
        [
            new SpeechRegion(2, 8.0, 12.0),
            new SpeechRegion(0, 0.2, 3.5),
            new SpeechRegion(1, 3.7, 7.4),
            new SpeechRegion(99, 5.0, 5.0),
        ]);

        Assert.Collection(
            regions,
            first =>
            {
                Assert.Equal(0, first.Index);
                Assert.Equal(0.2, first.StartSeconds);
                Assert.Equal(3.5, first.EndSeconds);
            },
            second =>
            {
                Assert.Equal(1, second.Index);
                Assert.Equal(3.7, second.StartSeconds);
                Assert.Equal(7.4, second.EndSeconds);
            },
            third =>
            {
                Assert.Equal(2, third.Index);
                Assert.Equal(8.0, third.StartSeconds);
                Assert.Equal(12.0, third.EndSeconds);
            });
    }

    [Fact]
    public void Qwen3AsrOnnxAudioTranscriptionEngine_DoesNotMergeNearbySpeakerTurns()
    {
        SpeechRegion[] speakerTurns =
        [
            new SpeechRegion(0, 0.32, 7.42),
            new SpeechRegion(1, 7.68, 12.10),
            new SpeechRegion(2, 12.35, 18.80),
            new SpeechRegion(3, 19.05, 24.40),
        ];

        IReadOnlyList<SpeechRegion> whisperMerged = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            speakerTurns,
            durationSeconds: 24.5);
        IReadOnlyList<SpeechRegion> qwenRegions = Qwen3AsrOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(speakerTurns);

        Assert.Single(whisperMerged);
        Assert.Equal(4, qwenRegions.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, qwenRegions.Select(region => region.Index));
    }

    [RequiresBundledModelFact(
        "nemotron-3.5-asr-onnx/config.json",
        "nemotron-3.5-asr-onnx/encoder.onnx",
        "nemotron-3.5-asr-onnx/encoder.onnx.data",
        "nemotron-3.5-asr-onnx/decoder_joint.onnx",
        "nemotron-3.5-asr-onnx/tokenizer.model")]
    public async Task NemotronAsrOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new NemotronAsrOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx",
                    ModelAlias = "nemotron-3.5-asr",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    EngineFamily = NemotronAsrOnnxAudioTranscriptionEngine.EngineFamilyName,
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            _ = segments;
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact(
        "qwen3-asr-0.6b-onnx/encoder.onnx",
        "qwen3-asr-0.6b-onnx/decoder_init.onnx",
        "qwen3-asr-0.6b-onnx/decoder_step.onnx",
        "qwen3-asr-0.6b-onnx/embed_tokens.bin",
        "qwen3-asr-0.6b-onnx/tokenizer.json")]
    public async Task Qwen3AsrOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        await RunQwen3AsrSilenceSmokeAsync("qwen3-asr-0.6b", "tonythethompson/qwen3-asr-0.6b-onnx").ConfigureAwait(false);
    }

    [RequiresBundledModelFact(
        "qwen3-asr-1.7b-onnx/encoder.onnx",
        "qwen3-asr-1.7b-onnx/decoder_init.onnx",
        "qwen3-asr-1.7b-onnx/decoder_step.onnx",
        "qwen3-asr-1.7b-onnx/embed_tokens.bin",
        "qwen3-asr-1.7b-onnx/tokenizer.json")]
    public async Task Qwen3AsrOnnxAudioTranscriptionEngine_RunsBundled1Point7BModelOnSilence()
    {
        await RunQwen3AsrSilenceSmokeAsync("qwen3-asr-1.7b", "tonythethompson/qwen3-asr-1.7b-onnx").ConfigureAwait(false);
    }

    private static async Task RunQwen3AsrSilenceSmokeAsync(string modelAlias, string modelId)
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new Qwen3AsrOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = modelId,
                    ModelAlias = modelAlias,
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    EngineFamily = Qwen3AsrOnnxAudioTranscriptionEngine.EngineFamilyName,
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            _ = segments;
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("whisper-tiny-onnx/onnx/encoder_model.onnx", "whisper-tiny-onnx/onnx/decoder_model.onnx", "whisper-tiny-onnx/vocab.json", "whisper-tiny-onnx/config.json")]
    public async Task WhisperOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new WhisperOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "onnx-community/whisper-tiny",
                    ModelAlias = "whisper-tiny-onnx",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            Assert.All(segments, static segment => Assert.False(string.IsNullOrWhiteSpace(segment.Text)));
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-en-es")]
    public async Task OpusMtTranslationEngine_TranslatesBundledEnglishToSpanishSentence()
    {
        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/opus-mt-en-es",
                ModelAlias = "opus-en-es",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello, I am Brenna Romaniello, your Spanish teacher from Ole Spanish.")],
                PreferredModelAlias: "opus-en-es"),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.Index);
        Assert.Equal("Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.", segment.Text);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("opus-en-es", engine.LastExecutionSummary.ModelAlias);
        Assert.Equal("merged-decoder", engine.LastExecutionSummary.ModelVariant);
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-en-es")]
    public async Task OpusMtTranslationEngine_StreamParity_MatchesBatchPerSegment()
    {
        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/opus-mt-en-es",
                ModelAlias = "opus-en-es",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        var request = new TranslationRequest(
            "en",
            "es",
            [
                new TranslationInputSegment(0, 0.0, 1.0, "Hello, I am Brenna Romaniello, your Spanish teacher from Ole Spanish."),
                new TranslationInputSegment(1, 1.0, 2.0, "Hello world.")
            ],
            PreferredModelAlias: "opus-en-es");

        IReadOnlyList<TranslatedTextSegment> batch =
            await engine.TranslateAsync(request, CancellationToken.None);

        Guid runId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        List<PipelineStreamItem<TranslatedTextSegment>> streamed = [];
        await foreach (PipelineStreamItem<TranslatedTextSegment> item in engine.TranslateStreamAsync(
            request, runId, "snap-parity", revision, CancellationToken.None))
        {
            streamed.Add(item);
        }

        Assert.Equal(batch, streamed.Select(i => i.Payload).ToArray());
        Assert.Equal(2, streamed.Count);
        Assert.Equal([0L, 1L], streamed.Select(i => i.Identity.Sequence).ToArray());
        Assert.All(streamed, item =>
        {
            Assert.Equal(runId, item.Identity.RunId);
            Assert.Equal("snap-parity", item.Identity.SnapshotId);
            Assert.Equal(RuntimeStage.Translation, item.Identity.Stage);
            Assert.Equal(revision, item.Identity.RevisionId);
            Assert.Equal(item.Payload.Index, item.Identity.SegmentIndex);
        });
        Assert.Equal(
            "Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.",
            streamed[0].Payload.Text);
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-es-en")]
    public async Task OpusMtTranslationEngine_TranslatesBundledSpanishToEnglishSentence()
    {
        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/opus-mt-es-en",
                ModelAlias = "opus-es-en",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "es",
                "en",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.")],
                PreferredModelAlias: "opus-es-en"),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.Index);
        Assert.Equal("Hi, I'm Brenna Romaniello, your Spanish teacher at Ole Spanish.", segment.Text);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("opus-es-en", engine.LastExecutionSummary.ModelAlias);
        Assert.Equal("merged-decoder", engine.LastExecutionSummary.ModelVariant);
    }

    [FixtureFact("TRACKDUB_OPUS_FIXTURE_ROOT", "encoder_model.onnx")]
    [Trait("Category", "Integration")]
    public async Task OpusMtTranslationEngine_UsesFixtureModelWhenProvided()
    {
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_OPUS_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model.onnx");

        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/opus-mt",
                ModelAlias = "fixture-opus-mt",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello world.")],
                PreferredModelAlias: "fixture-opus-mt",
                ResolvedModelEntryPath: encoderModelPath),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.False(string.IsNullOrWhiteSpace(segment.Text));
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
    }

    [FixtureFact("TRACKDUB_MADLAD_FIXTURE_ROOT", "encoder_model_int8.onnx")]
    [Trait("Category", "Integration")]
    public async Task MadladTranslationEngine_UsesFixtureModelWhenProvided()
    {
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_MADLAD_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model_int8.onnx");

        var engine = new MadladTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/madlad400",
                ModelAlias = "fixture-madlad400",
                Variant = "int8",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello world.")],
                PreferredModelAlias: "fixture-madlad400",
                ResolvedModelEntryPath: encoderModelPath),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.False(string.IsNullOrWhiteSpace(segment.Text));
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
    }

    [FixtureFact("TRACKDUB_MADLAD_FIXTURE_ROOT", "encoder_model_int8.onnx")]
    [Trait("Category", "Integration")]
    public async Task MadladTranslationEngine_StreamParity_MatchesBatchPerSegment()
    {
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_MADLAD_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model_int8.onnx");

        var engine = new MadladTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/madlad400",
                ModelAlias = "fixture-madlad400",
                Variant = "int8",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        var request = new TranslationRequest(
            "en",
            "fr",
            [
                new TranslationInputSegment(0, 0.0, 1.0, "Hello world."),
                new TranslationInputSegment(1, 1.0, 2.0, "Good morning.")
            ],
            PreferredModelAlias: "fixture-madlad400",
            ResolvedModelEntryPath: encoderModelPath);

        IReadOnlyList<TranslatedTextSegment> batch =
            await engine.TranslateAsync(request, CancellationToken.None);

        Guid runId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        List<PipelineStreamItem<TranslatedTextSegment>> streamed = [];
        await foreach (PipelineStreamItem<TranslatedTextSegment> item in engine.TranslateStreamAsync(
            request, runId, "snap-parity", revision, CancellationToken.None))
        {
            streamed.Add(item);
        }

        Assert.Equal(batch, streamed.Select(i => i.Payload).ToArray());
        Assert.Equal(2, streamed.Count);
        Assert.Equal([0L, 1L], streamed.Select(i => i.Identity.Sequence).ToArray());
        Assert.All(streamed, item =>
        {
            Assert.Equal(runId, item.Identity.RunId);
            Assert.Equal("snap-parity", item.Identity.SnapshotId);
            Assert.Equal(RuntimeStage.Translation, item.Identity.Stage);
            Assert.Equal(revision, item.Identity.RevisionId);
            Assert.Equal(item.Payload.Index, item.Identity.SegmentIndex);
        });
    }

    [FixtureFact("TRACKDUB_MADLAD_TRT_FIXTURE_ROOT", "decoder_model.onnx")]
    [Trait("Category", "Integration")]
    public async Task MadladTranslationEngine_TensorRtRtx_StaticShapesMatchUnpaddedFp16Reference()
    {
        // The trt-fp16 variant on TensorRT RTX pads encoder and decoder inputs to length buckets.
        // The expected texts are unpadded fp16 greedy decodes of the same export, so any leak of
        // padding into attention would change tokens.
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_MADLAD_TRT_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model.onnx");

        var engine = new MadladTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/madlad400",
                ModelAlias = "fixture-madlad400",
                Variant = "trt-fp16",
                ExecutionProvider = ExecutionProviderKind.TensorRTRtx
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [
                    new TranslationInputSegment(0, 0.0, 1.0, "The quick brown fox jumps over the lazy dog near the riverbank every morning."),
                    new TranslationInputSegment(1, 1.0, 2.0, "We will meet at the station at eight o'clock and then take the train to the coast.")
                ],
                PreferredModelAlias: "fixture-madlad400",
                ResolvedModelEntryPath: encoderModelPath),
            CancellationToken.None);

        Assert.Equal("tensorrt-rtx", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal(
            [
                "Le renard brun rapide saute par-dessus le chien paresseux près de la rive chaque matin.",
                "Nous nous retrouverons à la gare à huit heures et prendrons ensuite le train pour la côte."
            ],
            segments.Select(static segment => segment.Text).ToArray());
    }

    [FixtureFact("TRACKDUB_MADLAD_FIXTURE_ROOT", "spiece.model")]
    [Trait("Category", "Integration")]
    public async Task MadladTokenizerDecoder_EncodeSourceText_TerminatesWithEndOfSentence()
    {
        // T5-family encoders are trained on inputs closed by </s>. Without it greedy decoding
        // repeats the sentence or appends invented clauses.
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_MADLAD_FIXTURE_ROOT");
        MadladTokenizerDecoder tokenizer = await MadladTokenizerDecoder.LoadAsync(fixtureRoot);

        long[] ids = tokenizer.EncodeSourceText("Hello, how are you today?", "<2fr>");

        Assert.Equal(tokenizer.EndOfSentenceTokenId, ids[^1]);
        Assert.Single(ids, id => id == tokenizer.EndOfSentenceTokenId);
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-en-fr")]
    public async Task OpusMtTranslationEngine_ConfiglessModelRoot_TranslatesWithVocabDerivedSpecialTokens()
    {
        // Regression for the desktop model cache shape: the manifest only downloads the ONNX and
        // tokenizer files, so config.json and generation_config.json are absent. The legacy
        // hardcoded 65000 decoder-start/pad token was out of range for this model's 59514-entry
        // vocabulary and surfaced as an opaque ONNX Gather out-of-bounds failure on the first
        // decoder step. The engine must derive the ids from the vocabulary and translate normally.
        string bundledRoot = Path.GetFullPath(Path.Join(
            TestRepoRootResolver.FindRepoRoot(), "models", "opus", "onnx-community-opus-mt-en-fr"));
        string configlessRoot = StageConfiglessModelRoot(bundledRoot);
        try
        {
            var logger = new RecordingApplicationLogger();
            var engine = new OpusMtTranslationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Translation,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "onnx-community/opus-mt-en-fr",
                    ModelAlias = "opus-en-fr",
                    Variant = "merged-decoder",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault(),
                applicationLogger: logger);

            IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "fr",
                    [new TranslationInputSegment(0, 0.0, 1.0, "Hello, how are you today?")],
                    PreferredModelAlias: "opus-en-fr",
                    ResolvedModelEntryPath: Path.Join(configlessRoot, "onnx", "encoder_model.onnx")),
                CancellationToken.None);

            TranslatedTextSegment segment = Assert.Single(segments);
            Assert.False(string.IsNullOrWhiteSpace(segment.Text));
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);

            string provenance = Assert.Single(
                logger.InformationMessages,
                message => message.Contains("Opus tokenizer provenance", StringComparison.Ordinal));
            Assert.Contains("config.json=absent", provenance);
            Assert.Contains("generation_config.json=absent", provenance);
            Assert.Contains("decoder_start=59513", provenance);
            Assert.Contains("pad=59513", provenance);
        }
        finally
        {
            TryDeleteDirectory(configlessRoot);
        }
    }

    [Fact]
    public void MadladTranslationEngine_ResolveEncoderModelPath_prefers_planner_model_entry_path()
    {
        string tempRoot = Directory.CreateTempSubdirectory("trackdub-madlad-plan-entry-").FullName;
        try
        {
            string defaultEncoderPath = Path.Join(tempRoot, "encoder_model.onnx");
            string selectedRoot = Path.Join(tempRoot, "optimized", "olive-cpu-fp32");
            string selectedEncoderPath = Path.Join(selectedRoot, "encoder_model_int8.onnx");
            Directory.CreateDirectory(selectedRoot);
            File.WriteAllBytes(defaultEncoderPath, [1]);
            File.WriteAllBytes(selectedEncoderPath, [2]);
            var engine = new MadladTranslationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Translation,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "fixture/madlad400",
                    ModelAlias = tempRoot,
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    ModelEntryPath = selectedEncoderPath
                }),
                new BenchmarkModelPathResolver());
            MethodInfo method = typeof(MadladTranslationEngine).GetMethod(
                "ResolveEncoderModelPath",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            string resolved = (string)method.Invoke(
                engine,
                [
                    new StageRuntimePlan
                    {
                        Stage = RuntimeStage.Translation,
                        Status = StageRuntimePlanStatus.Ready,
                        ModelId = "fixture/madlad400",
                        ModelAlias = tempRoot,
                        ExecutionProvider = ExecutionProviderKind.Cpu,
                        ModelEntryPath = selectedEncoderPath
                    },
                    null
                ])!;

            Assert.Equal(Path.GetFullPath(selectedEncoderPath), resolved);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateSilenceWaveFile(double durationSeconds)
    {
        const int sampleRate = 48000;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        int sampleCount = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate));
        int blockAlign = channelCount * (bitsPerSample / 8);
        int dataLength = sampleCount * blockAlign;
        string path = Path.Join(Path.GetTempPath(), $"trackdub-onnx-silence-{Guid.NewGuid():N}.wav");

        byte[] buffer = new byte[44 + dataLength];
        WriteAscii(buffer, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), 36 + dataLength);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22, 2), channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34, 2), bitsPerSample);
        WriteAscii(buffer, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), dataLength);

        File.WriteAllBytes(path, buffer);
        return path;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            buffer[offset + index] = (byte)text[index];
        }
    }

    /// <summary>
    /// Copies the bundled model into a staging directory that mirrors the desktop model cache:
    /// the ONNX and tokenizer files only, never <c>config.json</c>/<c>generation_config.json</c>.
    /// </summary>
    private static string StageConfiglessModelRoot(string bundledModelRoot)
    {
        string stagedRoot = Path.Join(Path.GetTempPath(), "trackdub-opus-configless-model");
        // A prior run can leave the staged copy behind when pooled sessions still hold the files,
        // so clear the fixed staging path before writing a fresh copy.
        TryDeleteDirectory(stagedRoot);
        Directory.CreateDirectory(stagedRoot);
        foreach (string sourcePath in Directory.EnumerateFiles(bundledModelRoot, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(sourcePath);
            if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("generation_config.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destinationPath = Path.Join(stagedRoot, Path.GetRelativePath(bundledModelRoot, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return stagedRoot;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // Pooled ONNX sessions may still hold the staged model files; cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RequireFixtureRoot(string environmentVariableName)
    {
        string? fixtureRoot = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(fixtureRoot) || !Directory.Exists(fixtureRoot))
        {
            throw new InvalidOperationException($"Set {environmentVariableName} to a fixture model directory to run this integration test.");
        }

        return fixtureRoot;
    }

    private static string RequireFixtureFile(string fixtureRoot, string relativePath)
    {
        string fullPath = Path.Join(fixtureRoot, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Fixture file '{relativePath}' was not found under '{fixtureRoot}'.", fullPath);
        }

        return fullPath;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute(string environmentVariableName, string requiredRelativePath)
        {
            string? fixtureRoot = Environment.GetEnvironmentVariable(environmentVariableName);
            if (string.IsNullOrWhiteSpace(fixtureRoot) || !Directory.Exists(fixtureRoot))
            {
                Skip = $"Set {environmentVariableName} to a fixture model directory to run this integration test.";
                return;
            }

            string requiredPath = Path.Join(fixtureRoot, requiredRelativePath);
            if (!File.Exists(requiredPath))
            {
                Skip = $"Fixture file '{requiredRelativePath}' was not found under '{fixtureRoot}'.";
            }
        }
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        public List<string> InformationMessages { get; } = [];

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message) => InformationMessages.Add(message);

        public void LogWarning(string message, Exception? exception = null)
        {
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with
            {
                Stage = request.Stage
            });
    }
}
