using Trackdub.Application.Editing;
using Trackdub.Contracts;
using Trackdub.Contracts.Editing;
using Trackdub.Domain.Editing;
using Trackdub.Domain.Media;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SequenceExportDeliveryServiceTests
{
    [Fact]
    public async Task Selected_export_keeps_source_range_and_publishes_only_after_probe_and_tier_check()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out Guid selectedPlacementId);
        var renderer = new RecordingSequenceRenderer();
        var probe = new FakeMediaProbe { Snapshot = OutputSnapshot(durationSeconds: 4.5d) };
        var tierGate = new RecordingTierGate(maxDurationSeconds: 60, requiresWatermark: true);
        var service = new SequenceExportDeliveryService(renderer, probe, tierGate);

        SequenceExportDeliveryResult result = await service.ExportAsync(plan, selectedPlacementId, cancellationToken);

        AssertPublishedResult(result, plan, TimeSpan.FromSeconds(4.5d));
        AssertRenderedFileContents(plan);
        AssertSelectedPlacement(renderer, selectedPlacementId);
        AssertScopedRenderOptions(renderer, plan);
        AssertDeliveryChecks(tierGate, probe);
    }

    [Fact]
    public async Task Planned_duration_block_skips_rendering()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out _);
        var renderer = new RecordingSequenceRenderer();
        var probe = new FakeMediaProbe();
        var tierGate = new RecordingTierGate(maxDurationSeconds: 5, requiresWatermark: true);
        var service = new SequenceExportDeliveryService(renderer, probe, tierGate);

        SequenceExportDeliveryResult result = await service.ExportAsync(plan, cancellationToken: cancellationToken);

        AssertDurationBlocked(result);
        AssertNoRenderOccurred(renderer, probe);
        Assert.False(File.Exists(plan.OutputPath));
        Assert.Equal([9.5d], tierGate.CheckedDurations);
    }

    [Fact]
    public async Task Rendered_duration_block_preserves_previous_output_and_cleans_temporary_file()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out _);
        await File.WriteAllTextAsync(plan.OutputPath, "previous", cancellationToken);
        var renderer = new RecordingSequenceRenderer();
        var probe = new FakeMediaProbe { Snapshot = OutputSnapshot(durationSeconds: 301d) };
        var tierGate = new RecordingTierGate(maxDurationSeconds: 60, requiresWatermark: true);
        var service = new SequenceExportDeliveryService(renderer, probe, tierGate);

        SequenceExportDeliveryResult result = await service.ExportAsync(plan, cancellationToken: cancellationToken);

        AssertDurationBlocked(result);
        AssertPriorOutputPreservedAndTemporaryFileRemoved(plan, files);
        Assert.Equal([9.5d, 301d], tierGate.CheckedDurations);
        Assert.Single(probe.Calls);
    }

    [Fact]
    public async Task Render_failure_preserves_previous_output_and_cleans_temporary_file()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out _);
        await File.WriteAllTextAsync(plan.OutputPath, "previous", cancellationToken);
        var renderer = new RecordingSequenceRenderer
        {
            Failure = new InvalidOperationException("render failed")
        };
        var service = new SequenceExportDeliveryService(renderer, new FakeMediaProbe());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync(plan, cancellationToken: cancellationToken));

        Assert.Equal("previous", await File.ReadAllTextAsync(plan.OutputPath, cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial.mp4"));
    }

    [Fact]
    public async Task Output_cannot_replace_an_unselected_sequence_source()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out Guid selectedPlacementId);
        string unselectedSourcePath = plan.PlacementAssets[plan.Sequence.Placements[1].Id].SourceMediaPath;
        var renderer = new RecordingSequenceRenderer();
        var service = new SequenceExportDeliveryService(renderer, new FakeMediaProbe());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync(plan with { OutputPath = unselectedSourcePath }, selectedPlacementId, cancellationToken));

        Assert.Empty(renderer.Plans);
    }

    [Fact]
    public async Task Output_extension_must_match_selected_container()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out _);
        var renderer = new RecordingSequenceRenderer();
        var service = new SequenceExportDeliveryService(renderer, new FakeMediaProbe());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportAsync(plan with { OutputPath = Path.Join(files.Root, "sequence.mkv") }, cancellationToken: cancellationToken));

        Assert.Empty(renderer.Plans);
    }

    [Fact]
    public async Task Selected_export_still_requires_a_complete_asset_map_for_safe_output_validation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out Guid selectedPlacementId);
        Guid unselectedPlacementId = plan.Sequence.Placements[0].Id;
        var renderer = new RecordingSequenceRenderer();
        var service = new SequenceExportDeliveryService(renderer, new FakeMediaProbe());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExportAsync(
                plan with
                {
                    PlacementAssets = new Dictionary<Guid, SequenceRenderPlacementAssets>
                    {
                        [selectedPlacementId] = plan.PlacementAssets[selectedPlacementId]
                    }
                },
                selectedPlacementId,
                cancellationToken));

        Assert.True(plan.PlacementAssets.ContainsKey(unselectedPlacementId));
        Assert.Empty(renderer.Plans);
    }

    [Fact]
    public async Task Selected_export_rejects_burn_in_subtitles_until_they_are_rebased()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out Guid selectedPlacementId) with
        {
            BurnInSubtitlePath = files.Create("sequence.srt")
        };
        var renderer = new RecordingSequenceRenderer();
        var service = new SequenceExportDeliveryService(renderer, new FakeMediaProbe());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync(plan, selectedPlacementId, cancellationToken));

        Assert.Empty(renderer.Plans);
    }

    [Fact]
    public async Task Rendered_output_shorter_than_planned_sequence_is_blocked()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TemporaryFiles();
        SequenceExportPlan plan = CreatePlan(files, out _);
        var renderer = new RecordingSequenceRenderer();
        var probe = new FakeMediaProbe { Snapshot = OutputSnapshot(durationSeconds: 4.5d) };
        var tierGate = new RecordingTierGate(maxDurationSeconds: 60, requiresWatermark: false);
        var service = new SequenceExportDeliveryService(renderer, probe, tierGate);

        SequenceExportDeliveryResult result = await service.ExportAsync(plan, cancellationToken: cancellationToken);

        AssertDurationBlocked(result);
        Assert.False(File.Exists(plan.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial.mp4"));
    }

    private static SequenceExportPlan CreatePlan(TemporaryFiles files, out Guid selectedPlacementId)
    {
        Guid projectId = Guid.NewGuid();
        var firstSource = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 20d, 1920, 1080);
        var secondSource = new SequenceSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sequence-sources/second", 40d, 1280, 720);
        var firstPlacement = new SequencePlacement(Guid.NewGuid(), firstSource.Id, 0d, 5d, new SequenceCrop());
        selectedPlacementId = Guid.NewGuid();
        var selectedPlacement = new SequencePlacement(selectedPlacementId, secondSource.Id, 12d, 16.5d, new SequenceCrop());
        SequenceDocument sequence = SequenceDocument.Create(projectId);
        sequence = SequenceEditor.AddSource(sequence, firstSource);
        sequence = SequenceEditor.AddSource(sequence, secondSource);
        sequence = SequenceEditor.AddPlacement(sequence, firstPlacement);
        sequence = SequenceEditor.AddPlacement(sequence, selectedPlacement);

        string firstVideo = files.Create("first.mp4");
        string secondVideo = files.Create("second.mp4");
        string firstAudio = files.Create("first.wav");
        string secondAudio = files.Create("second.wav");
        return new SequenceExportPlan(
            sequence,
            new Dictionary<Guid, SequenceRenderPlacementAssets>
            {
                [firstPlacement.Id] = new(firstVideo, firstAudio),
                [selectedPlacement.Id] = new(secondVideo, secondAudio)
            },
            Path.Join(files.Root, "sequence.mp4"),
            ExportOutputContainer.Mp4,
            SequenceExportAudioMode.Dubbed);
    }

    private static void AssertPublishedResult(
        SequenceExportDeliveryResult result,
        SequenceExportPlan plan,
        TimeSpan expectedDuration)
    {
        Assert.False(result.IsBlocked);
        Assert.Equal(plan.OutputPath, result.OutputPath);
        Assert.Equal(expectedDuration.TotalSeconds, result.DurationSeconds);
    }

    private static void AssertRenderedFileContents(SequenceExportPlan plan) =>
        Assert.Equal("rendered", File.ReadAllText(plan.OutputPath));

    private static void AssertSelectedPlacement(RecordingSequenceRenderer renderer, Guid expectedPlacementId)
    {
        SequencePlacement placement = Assert.Single(Assert.Single(renderer.Plans).Sequence.Placements);
        Assert.Equal(expectedPlacementId, placement.Id);
        Assert.Equal(12d, placement.SourceInSeconds);
        Assert.Equal(16.5d, placement.SourceOutSeconds);
    }

    private static void AssertScopedRenderOptions(RecordingSequenceRenderer renderer, SequenceExportPlan plan)
    {
        SequenceExportPlan renderedPlan = Assert.Single(renderer.Plans);
        Assert.Single(renderedPlan.PlacementAssets);
        Assert.NotEqual(plan.OutputPath, renderedPlan.OutputPath);
        Assert.True(renderedPlan.RequiresWatermark);
    }

    private static void AssertDeliveryChecks(RecordingTierGate tierGate, FakeMediaProbe probe)
    {
        Assert.Equal([4.5d, 4.5d], tierGate.CheckedDurations);
        Assert.Single(probe.Calls);
    }

    private static void AssertDurationBlocked(SequenceExportDeliveryResult result)
    {
        Assert.True(result.IsBlocked);
        Assert.Contains("duration", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoRenderOccurred(RecordingSequenceRenderer renderer, FakeMediaProbe probe)
    {
        Assert.Empty(renderer.Plans);
        Assert.Empty(probe.Calls);
    }

    private static void AssertPriorOutputPreservedAndTemporaryFileRemoved(SequenceExportPlan plan, TemporaryFiles files)
    {
        Assert.Equal("previous", File.ReadAllText(plan.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial.mp4"));
    }

    private static MediaProbeSnapshot OutputSnapshot(double durationSeconds) => new(
        "mp4",
        "MP4",
        durationSeconds,
        BitRate: null,
        AudioStreams: [],
        VideoStreams: [new MediaVideoStream(0, "h264", 1920, 1080, 30, durationSeconds)]);

    private sealed class RecordingSequenceRenderer : ISequenceExportRenderer
    {
        public List<SequenceExportPlan> Plans { get; } = [];

        public Exception? Failure { get; init; }

        public async Task<SequenceExportRenderResult> RenderAsync(
            SequenceExportPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plans.Add(plan);
            await File.WriteAllTextAsync(plan.OutputPath, "rendered", cancellationToken);
            if (Failure is not null)
            {
                throw Failure;
            }

            return new SequenceExportRenderResult(plan.OutputPath, ["test warning"]);
        }
    }

    private sealed class RecordingTierGate(double maxDurationSeconds, bool requiresWatermark) : IExportTierGate
    {
        public bool RequiresWatermark { get; } = requiresWatermark;

        public List<double> CheckedDurations { get; } = [];

        public string? CheckDurationGate(TimeSpan sourceDuration)
        {
            CheckedDurations.Add(sourceDuration.TotalSeconds);
            return sourceDuration.TotalSeconds > maxDurationSeconds
                ? $"Export duration exceeds {maxDurationSeconds:0} seconds."
                : null;
        }
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("trackdub-sequence-delivery-").FullName;

        public string Create(string name)
        {
            string path = Path.Join(Root, name);
            File.WriteAllText(path, "test input");
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
