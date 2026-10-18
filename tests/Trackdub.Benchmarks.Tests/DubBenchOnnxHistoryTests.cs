using DubBench.Services;
using DubBench.ViewModels;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests;

public sealed class DubBenchOnnxHistoryTests
{
    [Fact]
    public async Task OnnxHistoryListsRecentRecordsWithMetrics()
    {
        var record = new BenchmarkRunRecord(
            Guid.NewGuid(), "silero-vad", "/models/silero.onnx", "/tmp/report.json",
            BenchmarkStatus.Completed, "auto", "nvtensorrtrtx", 10, true, 1234,
            500, 12.5, 10, 15, null, DateTimeOffset.UtcNow)
        {
            SampleCount = 10,
            WarmLatencyP95Milliseconds = 14.25,
            TimingMode = BenchmarkTimingMode.DeviceBoundSynchronized
        };
        var vm = new OnnxModelTabViewModel(new FakeRunner([record]), new FakeOlive());

        await vm.RefreshHistoryCommand.ExecuteAsync(null);

        OnnxBenchmarkHistoryEntry entry = Assert.Single(vm.RecentRuns);
        Assert.Equal(record.Id, entry.RunId);
        Assert.Equal("silero-vad", entry.Model);
        Assert.Equal("nvtensorrtrtx", entry.Provider);
        Assert.Equal("12.5 ms", entry.Average);
        Assert.Equal("14.2 ms", entry.P95);
        Assert.Equal("10", entry.Samples);
        Assert.Equal("DeviceBoundSynchronized", entry.TimingMode);
    }

    [Fact]
    public async Task OnnxHistoryShortensProfilerModelIds()
    {
        var record = new BenchmarkRunRecord(
            Guid.NewGuid(),
            "hardware-profiler:3f8a1c2d-0000-4000-8000-abcdef012345:sha256abc:asr-fast",
            "/models/whisper.onnx", "/tmp/report.json",
            BenchmarkStatus.Completed, "auto", "cpu", 5, true, 10,
            null, 30, 25, 40, null, DateTimeOffset.UtcNow);
        var vm = new OnnxModelTabViewModel(new FakeRunner([record]), new FakeOlive());

        await vm.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.Equal("profiler:asr-fast", Assert.Single(vm.RecentRuns).Model);
    }

    [Fact]
    public async Task ServiceWithoutRepositoryReturnsEmptyHistory()
    {
        var service = new BenchmarkRunnerService();

        Assert.Empty(await service.ListOnnxBenchmarkHistoryAsync(10));
    }

    private sealed class FakeRunner(IReadOnlyList<BenchmarkRunRecord> records) : IBenchmarkRunnerService
    {
        public Task<BenchmarkReport> RunOnnxModelBenchmarkAsync(BenchmarkRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BenchmarkRunRecord>> ListOnnxBenchmarkHistoryAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult(records);

        public Task<AudioPrepBenchmarkReport> RunAudioPrepBenchmarkAsync(AudioPrepBenchmarkOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DubbingBenchmarkReport> RunDubbingBenchmarkAsync(DubbingBenchmarkOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BenchmarkEvidenceReport> RunControlledDubbingBenchmarkAsync(ControlledDubbingBenchmarkOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOlive : IOliveOptimizationService
    {
        public bool IsOliveAvailable => false;
        public Task<string?> OptimizeAsync(string modelPath, string outputDir, string? provider = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
