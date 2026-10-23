using System.Text.Json;
using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Unit tests for <see cref="RunManifestWriter"/>.
///
/// **Validates: Requirements 6.1, 6.2, 6.3**
/// </summary>
public sealed class RunManifestWriterTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly RunManifestWriter _writer = new();

    [Fact]
    public async Task WriteAsync_WritesManifestWithAllRequiredFields()
    {
        // Arrange
        string outputDir = CreateTempDirectory();
        var result = CreateFullResult(DubbingRunStatus.Succeeded);

        // Act
        await _writer.WriteAsync(result, outputDir);

        // Assert
        string manifestPath = Path.Join(outputDir, "run-manifest.json");
        Assert.True(File.Exists(manifestPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("runId", out var runIdProp));
        Assert.Equal(result.RunId.ToString(), runIdProp.GetString());

        Assert.True(root.TryGetProperty("startTime", out _));
        Assert.True(root.TryGetProperty("endTime", out _));

        Assert.True(root.TryGetProperty("overallStatus", out var statusProp));
        Assert.Equal("succeeded", statusProp.GetString());

        Assert.True(root.TryGetProperty("stageOutcomes", out var stagesProp));
        Assert.Equal(JsonValueKind.Array, stagesProp.ValueKind);
        Assert.Equal(2, stagesProp.GetArrayLength());

        // Verify per-stage fields
        var firstStage = stagesProp[0];
        Assert.True(firstStage.TryGetProperty("stageName", out var stageNameProp));
        Assert.Equal("ASR", stageNameProp.GetString());
        Assert.True(firstStage.TryGetProperty("status", out _));
        Assert.True(firstStage.TryGetProperty("startTime", out _));
        Assert.True(firstStage.TryGetProperty("endTime", out _));
        Assert.True(firstStage.TryGetProperty("artifactPaths", out _));

        // Verify execution snapshot is present
        Assert.True(root.TryGetProperty("executionSnapshot", out var snapshotProp));
        Assert.Equal(JsonValueKind.Object, snapshotProp.ValueKind);
    }

    [Fact]
    public async Task WriteAsync_WritesManifestOnPartialSuccess()
    {
        // Arrange
        string outputDir = CreateTempDirectory();
        var result = CreateFullResult(DubbingRunStatus.PartialSuccess);

        // Act
        await _writer.WriteAsync(result, outputDir);

        // Assert
        string manifestPath = Path.Join(outputDir, "run-manifest.json");
        Assert.True(File.Exists(manifestPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("overallStatus", out var statusProp));
        Assert.Equal("partialSuccess", statusProp.GetString());
    }

    [Fact]
    public async Task WriteAsync_WritesManifestOnFailure()
    {
        // Arrange
        string outputDir = CreateTempDirectory();
        var result = CreateFullResult(DubbingRunStatus.Failed);

        // Act
        await _writer.WriteAsync(result, outputDir);

        // Assert
        string manifestPath = Path.Join(outputDir, "run-manifest.json");
        Assert.True(File.Exists(manifestPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("overallStatus", out var statusProp));
        Assert.Equal("failed", statusProp.GetString());
    }

    [Fact]
    public async Task WriteAsync_CreatesHistoryFile()
    {
        // Arrange
        string outputDir = CreateTempDirectory();
        var runId = Guid.NewGuid();
        var result = CreateFullResult(DubbingRunStatus.Succeeded, runId);

        // Act
        await _writer.WriteAsync(result, outputDir);

        // Assert
        string historyDir = Path.Join(outputDir, "run-manifests");
        Assert.True(Directory.Exists(historyDir));

        string historyPath = Path.Join(historyDir, $"run-{runId}.json");
        Assert.True(File.Exists(historyPath));

        // Verify history file content matches latest
        string latestContent = await File.ReadAllTextAsync(Path.Join(outputDir, "run-manifest.json"));
        string historyContent = await File.ReadAllTextAsync(historyPath);
        Assert.Equal(latestContent, historyContent);
    }

    [Fact]
    public async Task WriteAsync_OverwritesLatestManifest()
    {
        // Arrange
        string outputDir = CreateTempDirectory();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var firstResult = CreateFullResult(DubbingRunStatus.Succeeded, firstRunId);
        var secondResult = CreateFullResult(DubbingRunStatus.PartialSuccess, secondRunId);

        // Act — write twice with different run IDs
        await _writer.WriteAsync(firstResult, outputDir);
        await _writer.WriteAsync(secondResult, outputDir);

        // Assert — latest manifest has the second run's data
        string manifestPath = Path.Join(outputDir, "run-manifest.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("runId", out var runIdProp));
        Assert.Equal(secondRunId.ToString(), runIdProp.GetString());

        Assert.True(root.TryGetProperty("overallStatus", out var statusProp));
        Assert.Equal("partialSuccess", statusProp.GetString());

        // Both history files should exist
        string firstHistoryPath = Path.Join(outputDir, "run-manifests", $"run-{firstRunId}.json");
        string secondHistoryPath = Path.Join(outputDir, "run-manifests", $"run-{secondRunId}.json");
        Assert.True(File.Exists(firstHistoryPath));
        Assert.True(File.Exists(secondHistoryPath));
    }

    [Fact]
    public async Task WriteAsync_WritesColdStartEvidence()
    {
        // Cold-start evidence must reach the durable run manifest so real pipeline-start
        // to first-inference-completion numbers can be collected across runs.
        string outputDir = CreateTempDirectory();
        DubbingRunResult result = CreateFullResult(DubbingRunStatus.Succeeded) with
        {
            ColdStartTiming = new PipelineColdStartTiming
            {
                RunElapsedMs = 1234.5,
                FirstStageStartElapsedMs = 12.5,
                FirstStageStartedStageName = "vad",
                FirstInferenceCompletionElapsedMs = 987.25,
                FirstInferenceSource = "onnx-run",
                FirstInferencePhase = ColdStartPhase.Stage,
                FirstInferenceStageName = "vad",
                FirstStageInferenceCompletionElapsedMs = 987.25,
                FirstStageInferenceSource = "onnx-run",
            },
        };

        await _writer.WriteAsync(result, outputDir);

        string manifestPath = Path.Join(outputDir, "run-manifest.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.True(doc.RootElement.TryGetProperty("coldStartTiming", out var coldStart));
        Assert.True(coldStart.TryGetProperty("firstInferenceCompletionElapsedMs", out var firstInference));
        Assert.Equal(987.25, firstInference.GetDouble());
        Assert.True(coldStart.TryGetProperty("firstInferenceSource", out var source));
        Assert.Equal("onnx-run", source.GetString());
    }

    private static DubbingRunResult CreateFullResult(DubbingRunStatus status, Guid? runId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DubbingRunResult
        {
            RunId = runId ?? Guid.NewGuid(),
            StartTime = now.AddMinutes(-5),
            EndTime = now,
            OverallStatus = status,
            StageOutcomes =
            [
                new StageOutcome
                {
                    StageName = "ASR",
                    Status = StageStatus.Succeeded,
                    StartTime = now.AddMinutes(-5),
                    EndTime = now.AddMinutes(-3),
                    ArtifactPaths = ["transcripts/asr-output.json"],
                    DegradationRecords = null,
                    ReasonCode = null,
                },
                new StageOutcome
                {
                    StageName = "Translation",
                    Status = status == DubbingRunStatus.Failed ? StageStatus.Failed : StageStatus.Succeeded,
                    StartTime = now.AddMinutes(-3),
                    EndTime = now.AddMinutes(-1),
                    ArtifactPaths = status == DubbingRunStatus.Failed
                        ? []
                        : ["translations/es.json"],
                    DegradationRecords = status == DubbingRunStatus.PartialSuccess
                        ? ["Fallback model used for segment 3"]
                        : null,
                    ReasonCode = status == DubbingRunStatus.Failed ? "STAGE_FAILED" : null,
                },
            ],
            ExecutionSnapshot = new Dictionary<string, string>
            {
                ["asr.provider"] = "whisper-onnx",
                ["asr.model"] = "large-v3",
                ["translation.provider"] = "nllb-onnx",
                ["translation.model"] = "nllb-200-distilled",
            },
        };
    }

    private string CreateTempDirectory()
    {
        string dir = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
