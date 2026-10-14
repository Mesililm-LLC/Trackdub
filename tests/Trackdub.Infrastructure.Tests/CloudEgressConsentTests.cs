using System.Net;
using System.Text;
using Trackdub.Application.Cloud;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;
using Trackdub.Infrastructure.Dubbing;
using Trackdub.Infrastructure.Transcription;
using Trackdub.Infrastructure.Transcripts;
using Trackdub.Infrastructure.Translation;
using Trackdub.Infrastructure.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Infrastructure.Tests;

public sealed class CloudEgressConsentTests
{
    [Fact]
    public async Task CloudEgressConsentService_HasConsent_and_SetConsentAsync_persist_and_revoke_keys()
    {
        var settingsService = new FakeStudioSettingsService();
        var consentService = new CloudEgressConsentService(settingsService, _ => null);

        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.AudioGemini));
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.TextGemini));
        Assert.False(consentService.HasConsent(""));

        await consentService.SetConsentAsync(CloudEgressConsentKeys.AudioGemini, true, TestContext.Current.CancellationToken);

        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.AudioGemini));
        Assert.True(consentService.HasConsent(" AUDIO:GEMINI "));
        // Audio consent for Gemini must not grant text consent for Gemini
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.TextGemini));

        StudioSettings persisted = await settingsService.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.CloudEgressConsents);
        Assert.True(persisted.CloudEgressConsents![CloudEgressConsentKeys.AudioGemini]);

        await consentService.SetConsentAsync(CloudEgressConsentKeys.AudioGemini, false, TestContext.Current.CancellationToken);
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.AudioGemini));
    }

    [Fact]
    public void CloudEgressConsentService_audio_and_text_and_media_consents_are_independent()
    {
        var settingsService = new FakeStudioSettingsService();
        var consentService = new CloudEgressConsentService(settingsService, _ => null);

        settingsService.SaveAsync(
            StudioSettings.Default with
            {
                CloudEgressConsents = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [CloudEgressConsentKeys.TextOpenAi] = true,
                    [CloudEgressConsentKeys.TextGemini] = true,
                    [CloudEgressConsentKeys.TextElevenLabs] = true,
                }
            },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.TextOpenAi));
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.AudioOpenAi));

        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.TextGemini));
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.AudioGemini));

        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.TextElevenLabs));
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.MediaElevenLabs));
    }

    [Theory]
    [InlineData(StageNames.Asr, AsrModelOverrideSettings.OpenAiWhisperCloudAlias, CloudEgressConsentKeys.AudioOpenAi)]
    [InlineData(StageNames.Asr, AsrModelOverrideSettings.GeminiAsrCloudAlias, CloudEgressConsentKeys.AudioGemini)]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.DeepLModelAlias, CloudEgressConsentKeys.TextDeepL)]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.OpenAiGptCloudAlias, CloudEgressConsentKeys.TextOpenAi)]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.GeminiTranslationCloudAlias, CloudEgressConsentKeys.TextGemini)]
    [InlineData(StageNames.TextRefinementAsr, GeminiCloudTextRefinementEngine.EngineFamilyName, CloudEgressConsentKeys.TextGemini)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.ElevenLabsCloudAlias, CloudEgressConsentKeys.TextElevenLabs)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.OpenAiTtsCloudAlias, CloudEgressConsentKeys.TextOpenAi)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.GoogleTtsCloudAlias, CloudEgressConsentKeys.TextGoogle)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.GeminiTtsCloudAlias, CloudEgressConsentKeys.TextGemini)]
    [InlineData("cloud-dub", ElevenLabsCloudDubbingEngine.EngineFamilyName, CloudEgressConsentKeys.MediaElevenLabs)]
    public void CloudEgressConsentService_GetRequiredConsentKeys_maps_cloud_aliases(
        string stageName,
        string modelAlias,
        string expectedKey)
    {
        var consentService = new CloudEgressConsentService(new FakeStudioSettingsService(), _ => null);

        IReadOnlyList<string> keys = consentService.GetRequiredConsentKeys(stageName, modelAlias);

        Assert.Equal([expectedKey], keys);
    }

    [Fact]
    public void CloudEgressConsentService_honors_environment_variable_override()
    {
        var consentService = new CloudEgressConsentService(
            new FakeStudioSettingsService(),
            name => name == CloudEgressConsentService.ConsentEnvironmentVariable
                ? "audio:gemini, text:deepl"
                : null);

        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.AudioGemini));
        Assert.True(consentService.HasConsent(CloudEgressConsentKeys.TextDeepL));
        Assert.False(consentService.HasConsent(CloudEgressConsentKeys.MediaElevenLabs));
    }

    [Fact]
    public void CloudEgressConsentCatalog_contains_all_eight_canonical_keys()
    {
        Assert.Equal(8, CloudEgressConsentCatalog.All.Count);
        foreach (string key in CloudEgressConsentKeys.All)
        {
            CloudEgressDescription? description = CloudEgressConsentCatalog.Find(key);
            Assert.NotNull(description);
            Assert.Equal(key, description!.ConsentKey);
            Assert.NotEmpty(description.ProviderDisplayName);
            Assert.NotEmpty(description.DataDescription);
            Assert.NotEmpty(description.SentTo);
        }
    }

    [Fact]
    public async Task GeminiCloudTranscriptionEngine_fails_without_consent_and_succeeds_with_consent()
    {
        string tempAudioPath = Path.Join(Path.GetTempPath(), $"trackdub-asr-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tempAudioPath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                    {
                      "candidates": [
                        {
                          "content": {
                            "parts": [
                              { "text": "[{\"start\": 0.0, \"end\": 1.2, \"text\": \"Hello world\"}]" }
                            ]
                          }
                        }
                      ]
                    }
                    """)
            });
            using var httpClient = new HttpClient(handler);
            var consent = new FakeCloudEgressConsentService(defaultConsented: false);
            var apiKeyProvider = new StaticCloudApiKeyProvider("test-gemini-key");
            var engine = new GeminiCloudTranscriptionEngine(httpClient, apiKeyProvider, consent);

            StageReadiness unconsentedReadiness = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReadinessState.CloudEgressConsentRequired, unconsentedReadiness.Status);
            Assert.Equal("grant-egress-consent", unconsentedReadiness.ResolveAction);

            // Text consent does not satisfy audio consent
            consent.Grant(CloudEgressConsentKeys.TextGemini);
            CloudEgressConsentException ex = await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
                engine.TranscribeAsync(
                    new AudioTranscriptionRequest(tempAudioPath, []),
                    TestContext.Current.CancellationToken));
            Assert.Equal(CloudEgressConsentKeys.AudioGemini, ex.ConsentKey);
            Assert.Equal(0, handler.CallCount);

            // Grant audio:gemini consent
            consent.Grant(CloudEgressConsentKeys.AudioGemini);
            StageReadiness readyState = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReadinessState.Ready, readyState.Status);

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                new AudioTranscriptionRequest(tempAudioPath, []),
                TestContext.Current.CancellationToken);

            Assert.Single(segments);
            Assert.Equal("Hello world", segments[0].Text);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            if (File.Exists(tempAudioPath))
            {
                File.Delete(tempAudioPath);
            }
        }
    }

    [Fact]
    public async Task GeminiCloudTranslationEngine_fails_without_consent_and_succeeds_with_consent()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Bonjour\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var consent = new FakeCloudEgressConsentService(defaultConsented: false);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            consent);

        StageReadiness unconsentedReadiness = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.CloudEgressConsentRequired, unconsentedReadiness.Status);
        Assert.Equal("grant-egress-consent", unconsentedReadiness.ResolveAction);

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.TranslateAsync(
                new TranslationRequest("en", "fr", [new TranslationInputSegment(0, 0, 1.0, "Hello")]),
                TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);

        consent.Grant(CloudEgressConsentKeys.TextGemini);
        StageReadiness readyState = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.Ready, readyState.Status);

        IReadOnlyList<TranslatedTextSegment> translated = await engine.TranslateAsync(
            new TranslationRequest("en", "fr", [new TranslationInputSegment(0, 0, 1.0, "Hello")]),
            TestContext.Current.CancellationToken);

        Assert.Single(translated);
        Assert.Equal("Bonjour", translated[0].Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GeminiCloudTextRefinementEngine_fails_without_consent_and_succeeds_with_consent()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Refined text.\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var consent = new FakeCloudEgressConsentService(defaultConsented: false);
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            consent);

        StageReadiness unconsentedReadiness = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.CloudEgressConsentRequired, unconsentedReadiness.Status);

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.RefineAsync(
                new TextRefinementRequest([new TextRefinementInputSegment(0, 0, 1.0, "raw text")], "en"),
                TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);

        consent.Grant(CloudEgressConsentKeys.TextGemini);
        StageReadiness readyState = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.Ready, readyState.Status);

        IReadOnlyList<RefinedTextSegment> refined = await engine.RefineAsync(
            new TextRefinementRequest([new TextRefinementInputSegment(0, 0, 1.0, "raw text")], "en"),
            TestContext.Current.CancellationToken);

        Assert.Single(refined);
        Assert.Equal("Refined text.", refined[0].RefinedText);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GeminiCloudTtsEngine_fails_without_consent_and_succeeds_with_consent()
    {
        string base64Pcm = Convert.ToBase64String([0, 0, 10, 0]);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent($$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "inlineData": {
                              "mimeType": "audio/pcm;rate=24000",
                              "data": "{{base64Pcm}}"
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var consent = new FakeCloudEgressConsentService(defaultConsented: false);
        var engine = new GeminiCloudTtsEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            consent);

        StageReadiness unconsentedReadiness = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.CloudEgressConsentRequired, unconsentedReadiness.Status);

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.SynthesizeAsync(
                new TtsSynthesisRequest("Hello", "en", new VoiceCatalogEntry("Kore", "en", "neutral", "Kore")),
                TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);

        consent.Grant(CloudEgressConsentKeys.TextGemini);
        StageReadiness readyState = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.Ready, readyState.Status);

        TtsSynthesisResult ttsResult = await engine.SynthesizeAsync(
            new TtsSynthesisRequest("Hello", "en", new VoiceCatalogEntry("Kore", "en", "neutral", "Kore")),
            TestContext.Current.CancellationToken);

        Assert.Equal("gemini", ttsResult.Provider);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ElevenLabsCloudDubbingEngine_fails_without_consent_and_succeeds_with_progress_and_remote_deletion()
    {
        string tempMediaPath = Path.Join(Path.GetTempPath(), $"trackdub-media-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempMediaPath, [0x00, 0x01, 0x02, 0x03], TestContext.Current.CancellationToken);

        try
        {
            var requests = new List<(HttpMethod Method, string Uri)>();
            var handler = new CapturingHandler(req =>
            {
                string uri = req.RequestUri?.ToString() ?? string.Empty;
                requests.Add((req.Method, uri));

                if (req.Method == HttpMethod.Post && uri.EndsWith("/v1/dubbing", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent("""{"dubbing_id":"dub-123","expected_duration_sec":4.5}""")
                    };
                }

                if (req.Method == HttpMethod.Get && uri.EndsWith("/v1/dubbing/dub-123", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent("""{"dubbing_id":"dub-123","status":"dubbed"}""")
                    };
                }

                if (req.Method == HttpMethod.Get && uri.EndsWith("/v1/dubbing/dub-123/audio/es", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([10, 20, 30, 40])
                    };
                }

                if (req.Method == HttpMethod.Delete && uri.EndsWith("/v1/dubbing/dub-123", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(handler);
            var consent = new FakeCloudEgressConsentService(defaultConsented: false);
            var engine = new ElevenLabsCloudDubbingEngine(
                httpClient,
                new StaticCloudApiKeyProvider("test-elevenlabs-key"),
                consent,
                pollInterval: TimeSpan.FromMilliseconds(5));

            StageReadiness unconsentedReadiness = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReadinessState.CloudEgressConsentRequired, unconsentedReadiness.Status);
            Assert.Equal("grant-egress-consent", unconsentedReadiness.ResolveAction);

            // Text consent for ElevenLabs TTS must not satisfy full-media egress consent
            consent.Grant(CloudEgressConsentKeys.TextElevenLabs);
            CloudEgressConsentException ex = await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
                engine.DubAsync(
                    new CloudDubbingRequest(tempMediaPath, "en", "es"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(CloudEgressConsentKeys.MediaElevenLabs, ex.ConsentKey);
            Assert.Empty(requests);

            // Grant media:elevenlabs consent
            consent.Grant(CloudEgressConsentKeys.MediaElevenLabs);
            StageReadiness readyState = await engine.EvaluateReadinessAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReadinessState.Ready, readyState.Status);

            var progressStages = new List<CloudDubbingStage>();
            var progress = new SynchronousProgress<CloudDubbingProgress>(p => progressStages.Add(p.Stage));

            CloudDubbingResult result = await engine.DubAsync(
                new CloudDubbingRequest(tempMediaPath, "en", "es", Progress: progress, DeleteRemoteProjectOnCompletion: true),
                TestContext.Current.CancellationToken);

            Assert.Equal("dub-123", result.DubbingId);
            Assert.True(result.RemoteProjectDeleted);
            Assert.Equal([10, 20, 30, 40], result.AudioBytes);
            Assert.Contains(CloudDubbingStage.Submitting, progressStages);
            Assert.Contains(CloudDubbingStage.Polling, progressStages);
            Assert.Contains(CloudDubbingStage.Downloading, progressStages);
            Assert.Contains(CloudDubbingStage.DeletingRemoteProject, progressStages);
            Assert.Contains(CloudDubbingStage.Completed, progressStages);
            Assert.Contains(requests, r => r.Method == HttpMethod.Delete && r.Uri.EndsWith("/v1/dubbing/dub-123", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(tempMediaPath))
            {
                File.Delete(tempMediaPath);
            }
        }
    }

    [Fact]
    public async Task All_other_cloud_engines_enforce_egress_consent_guards()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => throw new InvalidOperationException("Network must not be called.")));
        var unconsented = new FakeCloudEgressConsentService(defaultConsented: false);
        var keyProvider = new StaticCloudApiKeyProvider("test-key");

        var openAiAsr = new OpenAiCloudTranscriptionEngine(httpClient, keyProvider, unconsented);
        var openAiTranslation = new OpenAiCloudTranslationEngine(httpClient, keyProvider, unconsented);
        var openAiTts = new OpenAiCloudTtsEngine(httpClient, keyProvider, unconsented);
        var elevenLabsTts = new ElevenLabsCloudTtsEngine(httpClient, keyProvider, unconsented);
        var googleTts = new GoogleCloudTtsEngine(httpClient, keyProvider, unconsented);

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            openAiAsr.TranscribeAsync(new AudioTranscriptionRequest("dummy.wav", []), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            openAiTranslation.TranslateAsync(new TranslationRequest("en", "es", [new TranslationInputSegment(0, 0, 1, "Hi")]), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            openAiTts.SynthesizeAsync(new TtsSynthesisRequest("Hi", "en", new VoiceCatalogEntry("alloy", "en", "neutral", "Alloy")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            elevenLabsTts.SynthesizeAsync(new TtsSynthesisRequest("Hi", "en", new VoiceCatalogEntry("v1", "en", "neutral", "V1")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            googleTts.SynthesizeAsync(new TtsSynthesisRequest("Hi", "en", new VoiceCatalogEntry("en-US-Standard-A", "en-US", "neutral", "A")), TestContext.Current.CancellationToken));
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
