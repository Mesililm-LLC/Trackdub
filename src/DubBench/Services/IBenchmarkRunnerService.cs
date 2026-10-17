using Trackdub.Benchmarks;
using Trackdub.Domain;
using Trackdub.Contracts.Benchmarking;

namespace DubBench.Services;

/// <summary>
/// Thin service that delegates to Trackdub's existing benchmark runners.
/// </summary>
public interface IBenchmarkRunnerService
{
    /// <summary>
    /// Runs ONNX model inference benchmark using <see cref="Trackdub.Inference.Onnx.OnnxModelBenchmarkRunner"/>.
    /// The report is persisted to ONNX benchmark history when a repository is configured.
    /// </summary>
    Task<BenchmarkReport> RunOnnxModelBenchmarkAsync(BenchmarkRequest request, CancellationToken ct = default);

    /// <summary>Lists recent persisted ONNX model benchmark runs, newest first.</summary>
    Task<IReadOnlyList<BenchmarkRunRecord>> ListOnnxBenchmarkHistoryAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Runs audio preparation benchmark using <see cref="AudioPrepBenchmarkRunner"/>.
    /// </summary>
    Task<AudioPrepBenchmarkReport> RunAudioPrepBenchmarkAsync(AudioPrepBenchmarkOptions options, CancellationToken ct = default);

    /// <summary>
    /// Runs the dubbing pipeline using <see cref="DubbingBenchmarkRunner"/>.
    /// </summary>
    Task<DubbingBenchmarkReport> RunDubbingBenchmarkAsync(DubbingBenchmarkOptions options, CancellationToken ct = default);

    /// <summary>Runs an isolated fixture and persists controlled benchmark evidence.</summary>
    Task<BenchmarkEvidenceReport> RunControlledDubbingBenchmarkAsync(
        ControlledDubbingBenchmarkOptions options, CancellationToken ct = default);
}
