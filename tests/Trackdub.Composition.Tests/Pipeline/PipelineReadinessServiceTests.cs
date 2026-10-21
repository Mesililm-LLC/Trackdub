using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Composition.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Pipeline;

public sealed class PipelineReadinessServiceTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("uk", true)]
    public async Task SpeechReadinessChecksSelectedModelsDeclaredTargetLanguages(string target, bool blocked)
    {
        Assert.True(BundledModelManifestRegistry.TryLoadDefault(out var registry, out var error), error);
        var model = registry!.Entries.Single(entry => entry.Aliases.Contains("kokoro-onnx"));
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage, Status = StageRuntimePlanStatus.Ready,
                ModelId = model.ModelId, ModelAlias = "kokoro-onnx"
            }
        };
        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService(), modelRegistry: registry);
        var selections = new RuntimeModelSelections(AsrModelOverride.Auto, false,
            new Dictionary<string, ExecutionProviderKind>(), TtsModelAlias: "kokoro-onnx");

        var report = await service.EvaluateAsync([RuntimeStage.Tts], selections, state: null, targetLanguageCode: target);

        var stage = Assert.Single(report.Stages);
        Assert.Equal(blocked, stage.Status.IsBlocking());
        Assert.Equal(blocked ? ReadinessState.UnsupportedLanguage : ReadinessState.Ready, stage.Status);
        if (blocked) Assert.Contains("Translation is available", stage.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    public async Task EvaluateAsync_defers_automatic_translation_without_planning_fallback(string? sourceLanguage)
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };
        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(AsrModelOverride.Auto, false,
            new Dictionary<string, ExecutionProviderKind>(), TranslationModelAlias: "m2m100",
            SelectionIntents: new Dictionary<RuntimeStage, RuntimeModelSelectionIntent>
            {
                [RuntimeStage.Translation] = RuntimeModelSelectionIntent.Automatic
            });

        var report = await service.EvaluateAsync([RuntimeStage.Translation], selections, state: null,
            sourceLanguageCode: sourceLanguage, targetLanguageCode: "es");

        var stage = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.PendingTranslationResolution, stage.Status);
        Assert.False(stage.Status.IsBlocking());
        Assert.Null(stage.ModelAlias);
        Assert.Null(stage.ResolveAction);
        Assert.Equal(0, planCalls);
    }

    [Fact]
    public async Task EvaluateAsync_normalizes_source_language_for_translation_planning()
    {
        string? capturedSourceLanguage = null;
        string? capturedTargetLanguage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedSourceLanguage = req.SourceLanguage;
                capturedTargetLanguage = req.TargetLanguage;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation],
            selections,
            state: null,
            sourceLanguageCode: "pt-BR",
            targetLanguageCode: "es-MX");

        Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, report.Stages[0].Status);
        Assert.Equal("pt", capturedSourceLanguage);
        Assert.Equal("es", capturedTargetLanguage);
    }

    [Fact]
    public async Task EvaluateAsync_uses_planner_for_lip_sync_instead_of_hardcoded_skip()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                Assert.Equal(RuntimeStage.LipSync, req.Stage);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            state: null);

        Assert.Equal(1, planCalls);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(StageNames.LipSync, lipSync.StageName);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_sync_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSyncModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_synthesis_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSynthesis],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSynthesisModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSynthesis = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSynthesis.Status);
    }

    [Fact]
    public async Task EvaluateAsync_validate_runtime_false_skips_provider_smoke_test()
    {
        var captured = new List<bool>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                captured.Add(req.SkipProviderSmokeTest);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        await service.EvaluateAsync(
            [RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync(
            [RuntimeStage.Asr], selections, state: null, validateRuntime: true);

        Assert.Equal([true, false], captured);
    }

    [Fact]
    public async Task EvaluateAsync_caches_per_validate_runtime_mode()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        // Same stage evaluated in both modes must not share a cache entry.
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: true);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: true);

        Assert.Equal(2, planCalls);
    }

    [Fact]
    public async Task EvaluateAsync_reports_speech_enhancement_under_canonical_stage_name()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
                new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.SpeechEnhancement],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            state: null);

        StageReadiness stage = Assert.Single(report.Stages);
        Assert.Equal(StageNames.SpeechEnhancement, stage.StageName);
        Assert.Equal(ReadinessState.Ready, stage.Status);
    }

    [Fact]
    public async Task EvaluateAsync_replans_when_execution_provider_override_changes()
    {
        int planCalls = 0;
        var capturedProviders = new List<ExecutionProviderKind?>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                capturedProviders.Add(req.PreferredExecutionProvider);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var cpuSelections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            TranslationModelAlias: "opus-mt-en-es",
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>
            {
                ["Translation"] = ExecutionProviderKind.Cpu,
            });
        var dmlSelections = cpuSelections with
        {
            HardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["Translation"] = ExecutionProviderKind.DirectMl,
            },
        };

        await service.EvaluateAsync([RuntimeStage.Translation], cpuSelections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], dmlSelections, state: null);
        // A repeated evaluation under the first override reuses its own cache entry.
        await service.EvaluateAsync([RuntimeStage.Translation], cpuSelections, state: null);

        Assert.Equal(2, planCalls);
        Assert.Equal(
            [ExecutionProviderKind.Cpu, ExecutionProviderKind.DirectMl],
            capturedProviders);
    }

    [Fact]
    public async Task EvaluateAsync_replans_when_model_variant_override_changes()
    {
        int planCalls = 0;
        var capturedVariants = new List<string?>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                capturedVariants.Add(req.PreferredModelVariantAlias);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var fp16Selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "opus-mt-en-es",
            ModelVariantOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "opus-mt-en-es")] = "fp16",
            });
        var int8Selections = fp16Selections with
        {
            ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "opus-mt-en-es")] = "int8",
            },
        };

        await service.EvaluateAsync([RuntimeStage.Translation], fp16Selections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], int8Selections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], fp16Selections, state: null);

        Assert.Equal(2, planCalls);
        Assert.Equal(["fp16", "int8"], capturedVariants);
    }

    [Fact]
    public async Task EvaluateAsync_reports_unverified_when_smoke_test_skipped_for_non_cpu_provider()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Ready,
                ExecutionProvider = ExecutionProviderKind.DirectMl,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(), TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        StageReadiness stage = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Unverified, stage.Status);
        Assert.False(stage.Status.IsBlocking());
    }

    [Fact]
    public async Task EvaluateAsync_reports_ready_when_smoke_test_skipped_for_cpu_plan()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Ready,
                ExecutionProvider = ExecutionProviderKind.Cpu,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(), TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        // CPU plans are never smoke-tested, so metadata checks are their full check.
        Assert.Equal(ReadinessState.Ready, Assert.Single(report.Stages).Status);
    }

    [Fact]
    public async Task EvaluateAsync_reports_ready_when_plan_verified_despite_skipped_smoke_test()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Verified,
                ExecutionProvider = ExecutionProviderKind.DirectMl,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(), TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        Assert.Equal(ReadinessState.Ready, Assert.Single(report.Stages).Status);
    }

    [Fact]
    public async Task EvaluateAsync_applies_tts_consent_overlay_on_cache_hit()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var consent = new FakeConsentService();
        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), consent);
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());
        TranscriptProjectState cloneState = CreateStateWithVoiceCloneAssignment();

        PipelineReadinessReport blocked = await service.EvaluateAsync(
            [RuntimeStage.Tts], selections, cloneState);
        Assert.Equal(ReadinessState.ConsentRequired, Assert.Single(blocked.Stages).Status);

        // Consent granted mid-session must clear the overlay even though the
        // underlying plan is served from cache (no invalidation, no replan).
        consent.GrantVoiceCloningConsent();
        PipelineReadinessReport allowed = await service.EvaluateAsync(
            [RuntimeStage.Tts], selections, cloneState);
        Assert.Equal(ReadinessState.Ready, Assert.Single(allowed.Stages).Status);
        Assert.Equal(1, planCalls);
    }

    [Fact]
    public async Task EvaluateAsync_preserves_stage_order_under_parallel_evaluation()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandlerAsync = async (req, ct) =>
            {
                // Stagger completions so out-of-order finishes would show up if slots were wrong.
                await Task.Delay(req.Stage == RuntimeStage.Asr ? 40 : 5, ct);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(
            planner,
            new NullCloudApiKeyProvider(),
            new FakeConsentService(),
            maxConcurrentStageEvaluations: 4);
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        RuntimeStage[] stages = [RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Diarization, RuntimeStage.Translation];
        PipelineReadinessReport report = await service.EvaluateAsync(stages, selections, state: null);

        Assert.Equal(
            stages.Select(StageNameForTest),
            report.Stages.Select(s => s.StageName));
    }

    [Fact]
    public async Task EvaluateAsync_limits_concurrent_stage_evaluations()
    {
        int concurrent = 0;
        int maxConcurrent = 0;
        var gate = new object();
        var planner = new FakeRuntimePlanner
        {
            PlanHandlerAsync = async (req, ct) =>
            {
                lock (gate)
                {
                    concurrent++;
                    maxConcurrent = Math.Max(maxConcurrent, concurrent);
                }

                await Task.Delay(30, ct);

                lock (gate)
                {
                    concurrent--;
                }

                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(
            planner,
            new NullCloudApiKeyProvider(),
            new FakeConsentService(),
            maxConcurrentStageEvaluations: 2);
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        await service.EvaluateAsync(
            [RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Diarization, RuntimeStage.Translation, RuntimeStage.Tts],
            selections,
            state: null);

        Assert.InRange(maxConcurrent, 1, 2);
    }

    [Fact]
    public async Task EvaluateAsync_reports_timeout_as_runtime_missing_without_aborting_sweep()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandlerAsync = async (req, ct) =>
            {
                if (req.Stage == RuntimeStage.Tts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }

                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(
            planner,
            new NullCloudApiKeyProvider(),
            new FakeConsentService(),
            maxConcurrentStageEvaluations: 4,
            stageEvaluationTimeout: TimeSpan.FromMilliseconds(80));
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Vad, RuntimeStage.Tts, RuntimeStage.Asr],
            selections,
            state: null);

        Assert.Equal(3, report.Stages.Count);
        Assert.Equal(ReadinessState.Ready, report.Stages[0].Status);
        StageReadiness timedOut = report.Stages[1];
        Assert.Equal(ReadinessState.RuntimeMissing, timedOut.Status);
        Assert.Contains("timed out", timedOut.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReadinessState.Ready, report.Stages[2].Status);
        Assert.False(report.IsRunReady);
    }

    [Fact]
    public async Task EvaluateAsync_returns_CloudEgressConsentRequired_before_CloudKeyMissing_and_Ready_after_consent_and_key()
    {
        var planner = new FakeRuntimePlanner();
        var consentService = new FakeCloudEgressConsentService(defaultConsented: false);
        string? configuredApiKey = null;
        var apiKeyProvider = new DelegateCloudApiKeyProvider(_ => configuredApiKey);

        var service = new PipelineReadinessService(
            planner,
            apiKeyProvider,
            new FakeConsentService(),
            cloudEgressConsentService: consentService);

        var selections = new RuntimeModelSelections(
            AsrModelOverride.GeminiAsr,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            AsrModelAlias: AsrModelOverrideSettings.GeminiAsrCloudAlias,
            TranslationModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
            TtsModelAlias: TtsModelOverrideSettings.GeminiTtsCloudAlias);

        // 1. Neither consent nor API key: CloudEgressConsentRequired takes precedence
        PipelineReadinessReport unconsentedReport = await service.EvaluateAsync(
            [RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts],
            selections,
            state: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(unconsentedReport.Stages, stage =>
        {
            Assert.Equal(ReadinessState.CloudEgressConsentRequired, stage.Status);
            Assert.Equal("grant-egress-consent", stage.ResolveAction);
            Assert.True(stage.Status.IsBlocking());
        });

        // 2. Grant text:gemini only: Translation and TTS move to CloudKeyMissing, ASR stays CloudEgressConsentRequired
        consentService.Grant(CloudEgressConsentKeys.TextGemini);
        PipelineReadinessReport partialConsentReport = await service.EvaluateAsync(
            [RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts],
            selections,
            state: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReadinessState.CloudEgressConsentRequired, partialConsentReport.Stages[0].Status);
        Assert.Equal(ReadinessState.CloudKeyMissing, partialConsentReport.Stages[1].Status);
        Assert.Equal(ReadinessState.CloudKeyMissing, partialConsentReport.Stages[2].Status);

        // 3. Grant audio:gemini and set API key: all three stages become Ready
        consentService.Grant(CloudEgressConsentKeys.AudioGemini);
        configuredApiKey = "gemini-test-key";

        PipelineReadinessReport readyReport = await service.EvaluateAsync(
            [RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts],
            selections,
            state: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(readyReport.Stages, stage => Assert.Equal(ReadinessState.Ready, stage.Status));
        Assert.True(readyReport.IsRunReady);
    }

    private sealed class DelegateCloudApiKeyProvider(Func<string, string?> resolver) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(resolver(providerKey));
    }

    private static string StageNameForTest(RuntimeStage stage) => stage switch
    {
        RuntimeStage.Vad => StageNames.Vad,
        RuntimeStage.Asr => StageNames.Asr,
        RuntimeStage.Diarization => StageNames.Diarization,
        RuntimeStage.Translation => StageNames.Translation,
        RuntimeStage.Tts => StageNames.Tts,
        _ => stage.ToString(),
    };

    private static TranscriptProjectState CreateStateWithVoiceCloneAssignment()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "test", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "media/source.mp4",
            "source.mp4",
            "abc123",
            1024L,
            now,
            "mp4",
            DurationSeconds: 30.0,
            HasAudio: true,
            HasVideo: true,
            now);
        var openResult = new OpenProjectResult(
            project,
            mediaAsset,
            SourceReference: null,
            SourceMediaStatus.Available,
            SourceStatusMessage: null,
            Artifacts: [],
            TranscriptLanguage: "en");

        VoiceAssignment cloneAssignment = VoiceAssignment.Create(
            projectId,
            Guid.NewGuid(),
            "chatterbox-clone",
            referenceClipArtifactId: Guid.NewGuid());

        return new TranscriptProjectState(
            openResult,
            CurrentTranscriptRevision: null,
            TranscriptSegments: [],
            Speakers: [],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [cloneAssignment],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }

    private sealed class NullCloudApiKeyProvider : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
