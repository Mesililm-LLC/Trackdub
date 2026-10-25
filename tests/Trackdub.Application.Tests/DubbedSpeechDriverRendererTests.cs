using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Mixing;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Mixing;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class DubbedSpeechDriverRendererTests : IDisposable
{
    private readonly string root = Path.Join(Path.GetTempPath(), "Trackdub.Application.Tests", "driver-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Renders_only_the_dubbed_takes_with_the_original_bed_and_media_muted()
    {
        var store = new FakeArtifactStore(root);
        await new MixPlanStore(store).SaveAsync(CreatePlan(speechEnd: 9.5, silentOnly: false), CancellationToken.None);
        var renderer = new RecordingRenderer();
        var driver = new DubbedSpeechDriverRenderer(new MixPlanStore(store), renderer, store);

        string? path = await driver.TryRenderAsync(mediaDurationSeconds: 10.1, CancellationToken.None);

        Assert.Equal(store.GetPath(DubbedSpeechDriverRenderer.RelativePath), path);
        PreviewRangeRenderRequest request = Assert.Single(renderer.Requests);
        Assert.Equal(DubbedSpeechDriverRenderer.MutedSourceGainDb, request.MixPlan.SourceGainDb);
        Assert.False(request.MixPlan.ApplyTimbrePolish);
        Assert.Empty(request.MixPlan.MediaClips);
        Assert.Equal(0d, request.StartSeconds);
        Assert.Equal(10.1, request.EndSeconds);
    }

    [Fact]
    public async Task Extends_the_render_to_the_last_take_when_it_runs_past_the_media()
    {
        var store = new FakeArtifactStore(root);
        await new MixPlanStore(store).SaveAsync(CreatePlan(speechEnd: 12.0, silentOnly: false), CancellationToken.None);
        var renderer = new RecordingRenderer();

        await new DubbedSpeechDriverRenderer(new MixPlanStore(store), renderer, store)
            .TryRenderAsync(mediaDurationSeconds: 10.1, CancellationToken.None);

        Assert.Equal(12.0, Assert.Single(renderer.Requests).EndSeconds);
    }

    [Fact]
    public async Task Returns_null_so_the_caller_keeps_the_mix_when_there_is_no_mix_plan()
    {
        var store = new FakeArtifactStore(root);
        var renderer = new RecordingRenderer();

        string? path = await new DubbedSpeechDriverRenderer(new MixPlanStore(store), renderer, store)
            .TryRenderAsync(10, CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(renderer.Requests);
    }

    [Fact]
    public async Task Returns_null_when_the_plan_has_only_silent_gaps()
    {
        var store = new FakeArtifactStore(root);
        await new MixPlanStore(store).SaveAsync(CreatePlan(speechEnd: 9.5, silentOnly: true), CancellationToken.None);

        string? path = await new DubbedSpeechDriverRenderer(new MixPlanStore(store), new RecordingRenderer(), store)
            .TryRenderAsync(10, CancellationToken.None);

        Assert.Null(path);
    }

    [Fact]
    public async Task Returns_null_when_rendering_fails()
    {
        var store = new FakeArtifactStore(root);
        await new MixPlanStore(store).SaveAsync(CreatePlan(speechEnd: 9.5, silentOnly: false), CancellationToken.None);

        string? path = await new DubbedSpeechDriverRenderer(new MixPlanStore(store), new RecordingRenderer { Fail = true }, store)
            .TryRenderAsync(10, CancellationToken.None);

        Assert.Null(path);
    }

    private static MixPlan CreatePlan(double speechEnd, bool silentOnly) =>
        new(
            ProjectId: Guid.NewGuid(),
            MediaAssetId: Guid.NewGuid(),
            SourceAudioKind: ArtifactKind.Ambiance,
            SourceAudioRelativePath: "artifacts/stems/ambiance.wav",
            SourceGainDb: 0,
            DubbedSpeechGainDb: 0,
            DuckingGainDb: 0,
            DuckingLeadSeconds: 0.05,
            DuckingTailSeconds: 0.18,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SpeechClips:
            [
                new MixSpeechClip(0, Guid.NewGuid(), 0.6, speechEnd, Guid.NewGuid(), Guid.NewGuid(),
                    silentOnly ? null : "artifacts/tts/take.wav", 8.9, silentOnly, null),
            ],
            DuckingRegions: [],
            Warnings: [],
            MediaClips: [new MixMediaClip("artifacts/media/music.wav", 0, 10)]);

    private sealed class RecordingRenderer : IPreviewRangeRenderer
    {
        public List<PreviewRangeRenderRequest> Requests { get; } = [];

        public bool Fail { get; init; }

        public Task<PreviewRangeRenderResult> RenderAsync(PreviewRangeRenderRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Fail)
            {
                throw new FileNotFoundException("Dubbed speech take file was not found.");
            }

            File.WriteAllBytes(request.OutputPath, [0]);
            return Task.FromResult(new PreviewRangeRenderResult(request.OutputPath, request.EndSeconds, 48000, 1));
        }
    }
}
