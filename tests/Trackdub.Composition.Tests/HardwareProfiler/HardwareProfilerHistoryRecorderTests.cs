using Trackdub.Composition.HardwareProfiler;
using Trackdub.Domain;
using Xunit;

namespace Trackdub.Composition.Tests.HardwareProfiler;

public sealed class HardwareProfilerHistoryRecorderTests
{
    [Fact]
    public void MapScenarioToRecord_UsesDeterministicIdAndProfilerModelId()
    {
        Guid evidenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var fingerprint = HardwareFingerprint.Create("Windows", "x64", "GPU", 16L * 1024 * 1024 * 1024, null);
        var scenario = new StageBenchmarkScenarioResult(
            StageBenchmarkScenario.Asr,
            BenchmarkStatus.Completed,
            "auto",
            "dml",
            42.5,
            0.2,
            null);
        var snapshot = HardwareProfilerSnapshot.Create(
            fingerprint,
            [scenario],
            new HardwarePresetRecommendation(
                HardwareQualityPreset.Balanced,
                "Balanced",
                ["ok"],
                "balanced"));

        string reportsRoot = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            BenchmarkRunRecord record = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot with { EvidenceId = evidenceId },
                scenario,
                reportsRoot,
                runCount: 3);

            BenchmarkRunRecord repeat = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot with { EvidenceId = evidenceId },
                scenario,
                reportsRoot,
                runCount: 3);

            Assert.Equal(record.Id, repeat.Id);
            Assert.Equal(
                HardwareProfilerHistoryRecorder.BuildProfilerModelId(evidenceId, fingerprint.Hash, "asr"),
                record.ModelId);
            Assert.Equal(BenchmarkStatus.Completed, record.Status);
            Assert.Equal("auto", record.RequestedProvider);
            Assert.Equal("dml", record.SelectedProvider);
            Assert.Equal(42.5, record.WarmLatencyAverageMilliseconds);
        }
        finally
        {
            if (Directory.Exists(reportsRoot))
            {
                Directory.Delete(reportsRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void MapScenarioToRecord_copies_measurement_scalars_and_metadata_from_report()
    {
        var fingerprint = HardwareFingerprint.Create("Windows", "x64", "GPU", null, null);
        var scenario = new StageBenchmarkScenarioResult(
            StageBenchmarkScenario.Asr, BenchmarkStatus.Completed, "auto", "dml", 42.5, 0.2, null);
        var snapshot = HardwareProfilerSnapshot.Create(fingerprint, [scenario],
            new HardwarePresetRecommendation(HardwareQualityPreset.Balanced, "b", ["ok"], "balanced"));

        string reportsRoot = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportsRoot);
        try
        {
            File.WriteAllText(Path.Join(reportsRoot, "asr-1.json"), """
                {
                  "scenario": "asr", "modelPath": "/models/asr.onnx", "reportPath": "/r/asr-1.json",
                  "status": "completed", "requestedProvider": "auto", "selectedProvider": "dml",
                  "runCount": 3, "supportsExecution": true, "modelSizeBytes": 100,
                  "measurements": {
                    "warmLatencyAverageMilliseconds": 42.5,
                    "firstRunMilliseconds": 7.0,
                    "warmLatencyP95Milliseconds": 44.0,
                    "sampleCount": 3,
                    "audioDurationSeconds": 2.0
                  },
                  "metadata": { "comparisonKey": "dml|asr", "warnings": [] },
                  "generatedAtUtc": "2026-10-17T00:00:00Z"
                }
                """);

            BenchmarkRunRecord record = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot, scenario, reportsRoot, runCount: 3);

            Assert.Equal(7.0, record.FirstRunMilliseconds);
            Assert.Equal(44.0, record.WarmLatencyP95Milliseconds);
            Assert.Equal(3, record.SampleCount);
            Assert.Equal(2.0, record.AudioDurationSeconds);
            Assert.Equal(0.2, record.RealTimeFactorAverage);
            Assert.Equal("dml|asr", record.ComparisonKey);
            Assert.NotNull(record.Metadata);
            Assert.Equal("dml|asr", record.Metadata!.ComparisonKey);

            BenchmarkRunRecord missing = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot, scenario, Path.Join(reportsRoot, "absent"), runCount: 3);
            Assert.Null(missing.FirstRunMilliseconds);
            Assert.Null(missing.SampleCount);
            Assert.Null(missing.Metadata);
        }
        finally
        {
            Directory.Delete(reportsRoot, recursive: true);
        }
    }
}
