using System.Text.Json;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Domain.Tests;

public sealed class BenchmarkContractCompatibilityTests
{
    [Fact]
    public void LegacyJsonDoesNotInventNewMetricsOrSchemaVersion()
    {
        const string json = """
            {
              "Scenario": "onnx-model",
              "ModelPath": "model.onnx",
              "ReportPath": "report.json",
              "Status": 1,
              "RequestedProvider": "cpu",
              "SelectedProvider": "cpu",
              "RunCount": 3,
              "SupportsExecution": true,
              "ModelSizeBytes": 10,
              "Measurements": {
                "ColdLoadMilliseconds": 100,
                "WarmLatencyAverageMilliseconds": 25
              },
              "Notes": [],
              "GeneratedAtUtc": "2026-10-16T00:00:00Z"
            }
            """;

        BenchmarkReport report = Assert.IsType<BenchmarkReport>(
            JsonSerializer.Deserialize<BenchmarkReport>(json));

        Assert.Equal(1, report.SchemaVersion);
        Assert.Null(report.Metadata);
        Assert.Null(report.RunId);
        Assert.Null(report.HistoryPersistence);
        Assert.Null(report.Measurements.WarmLatencyP95Milliseconds);
        Assert.Null(report.Measurements.SampleCount);
        Assert.Null(report.Measurements.ThroughputUnitsPerSecond);
        Assert.Null(report.Measurements.LatencySamplesMilliseconds);
        Assert.Equal(25, report.Measurements.WarmLatencyAverageMilliseconds);
    }

    [Fact]
    public void ExecutionDefaultsPreserveExactCountPolicyForExistingCallers()
    {
        var request = new BenchmarkRequest("model.onnx", "report.json", BenchmarkProviderPreference.Cpu, 3);
        var settings = BenchmarkExecutionSettings.Default;

        Assert.Null(request.Execution);
        Assert.Equal(1, settings.MinimumWarmupRuns);
        Assert.Equal(200, settings.MinimumWarmupMilliseconds);
        Assert.Equal(0, settings.MinimumMeasurementMilliseconds);
        Assert.Equal(42, settings.InputSeed);
        Assert.Equal(BenchmarkInputMode.Synthetic, settings.InputMode);
        Assert.Equal(BenchmarkIoMode.Managed, settings.IoMode);
        Assert.False(settings.EffectiveTensorRtRtx.EnableCudaGraph);
        Assert.Equal(BenchmarkCudaGraphMode.Auto, settings.EffectiveTensorRtRtx.CudaGraphMode);
        settings.Validate(3);
    }

    [Fact]
    public void CudaGraphAutoResolvesFromDeviceBoundEligibility()
    {
        var auto = new BenchmarkTensorRtRtxSettings(CudaGraphMode: BenchmarkCudaGraphMode.Auto);
        Assert.True(auto.ResolveCudaGraphEnable(deviceBoundEligible: true));
        Assert.False(auto.ResolveCudaGraphEnable(deviceBoundEligible: false));
        Assert.False(auto.EnableCudaGraph);

        var off = new BenchmarkTensorRtRtxSettings(CudaGraphMode: BenchmarkCudaGraphMode.Off);
        Assert.False(off.ResolveCudaGraphEnable(deviceBoundEligible: true));

        // Auto never requires device-bound I/O: it resolves off under managed timing.
        new BenchmarkExecutionSettings(
            IoMode: BenchmarkIoMode.Managed,
            TensorRtRtx: auto).Validate(3);
    }

    [Fact]
    public void ExecutionSettingsRejectInvalidBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkExecutionSettings.Default.Validate(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BenchmarkExecutionSettings(MinimumWarmupRuns: 0).Validate(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BenchmarkExecutionSettings(MinimumWarmupMilliseconds: -1).Validate(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BenchmarkExecutionSettings(MinimumMeasurementMilliseconds: -1).Validate(1));
    }

    [Fact]
    public void CudaGraphModesRequireDeviceBoundIo()
    {
        var deviceBoundSettings = new BenchmarkExecutionSettings(
            IoMode: BenchmarkIoMode.DeviceBound,
            TensorRtRtx: new BenchmarkTensorRtRtxSettings(CudaGraphMode: BenchmarkCudaGraphMode.Compare));
        deviceBoundSettings.Validate(3);

        foreach (var mode in new[] { BenchmarkCudaGraphMode.On, BenchmarkCudaGraphMode.Compare })
        {
            var managedSettings = new BenchmarkExecutionSettings(
                IoMode: BenchmarkIoMode.Managed,
                TensorRtRtx: new BenchmarkTensorRtRtxSettings(CudaGraphMode: mode));
            Assert.Throws<ArgumentException>(() => managedSettings.Validate(3));
        }

        var graphOffManaged = new BenchmarkExecutionSettings(
            IoMode: BenchmarkIoMode.Managed,
            TensorRtRtx: new BenchmarkTensorRtRtxSettings(CudaGraphMode: BenchmarkCudaGraphMode.Off));
        graphOffManaged.Validate(3);
    }

    [Fact]
    public void HistoryMappingRetainsNewMetricsAndCallerAssignedId()
    {
        var id = Guid.NewGuid();
        var metadata = new BenchmarkRunMetadata();
        var measurements = new BenchmarkMeasurements(null, 200, 25, 10, 40, 30, 25d / 30000d)
        {
            FirstRunMilliseconds = 50,
            WarmupRunCount = 8,
            WarmLatencyP50Milliseconds = 25,
            WarmLatencyP90Milliseconds = 37,
            WarmLatencyP95Milliseconds = 38.5,
            WarmLatencyP99Milliseconds = 39.7,
            SampleCount = 4,
            MeasuredWallTimeMilliseconds = 200,
            PassesPerSecond = 20,
            ThroughputUnitsPerSecond = 600,
            ThroughputUnit = BenchmarkThroughputUnit.AudioSecondsPerSecond,
            LatencySamplesMilliseconds = [10, 20, 30, 40]
        };
        var report = new BenchmarkReport(
            "onnx-model", "model.onnx", "report.json", BenchmarkStatus.Completed,
            "cpu", "cpu", 4, true, 10, measurements, null, [], DateTimeOffset.UtcNow,
            SchemaVersion: 2, RunId: id, Metadata: metadata);

        BenchmarkRunRecord record = BenchmarkRunRecord.FromReport(id, "sample-model", report);

        Assert.Equal(id, record.Id);
        Assert.Equal(38.5, record.WarmLatencyP95Milliseconds);
        Assert.Equal(4, record.SampleCount);
        Assert.Equal(8, record.WarmupRunCount);
        Assert.Equal(20, record.PassesPerSecond);
        Assert.Equal(600, record.ThroughputUnitsPerSecond);
        Assert.Equal(BenchmarkThroughputUnit.AudioSecondsPerSecond, record.ThroughputUnit);
        Assert.Equal(30, record.AudioDurationSeconds);
        Assert.Same(metadata, record.Metadata);
    }
}
