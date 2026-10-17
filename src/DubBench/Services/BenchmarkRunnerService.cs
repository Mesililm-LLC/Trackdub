using Microsoft.Extensions.DependencyInjection;
using Trackdub.Benchmarks;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
#if WINDOWS
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
#endif

namespace DubBench.Services;

/// <summary>
/// Thin delegation wrapper around Trackdub's existing benchmark runners.
/// VMs instantiate this directly (no DI container for MVP).
/// </summary>
public sealed class BenchmarkRunnerService : IBenchmarkRunnerService
{
#if WINDOWS
    private readonly IModelBenchmarkRunner? _onnxRunner;
#endif
    private readonly AudioPrepBenchmarkRunner _audioRunner;
    private readonly DubbingBenchmarkRunner _dubbingRunner;
    private readonly ControlledDubbingBenchmarkRunner _controlledRunner;
    private readonly IUserBenchmarkRepository? _onnxHistory;

    public BenchmarkRunnerService(
        IBenchmarkEvidenceRepository? history = null,
        Action<IServiceCollection>? serviceConfigurator = null,
        IUserBenchmarkRepository? onnxHistory = null)
    {
        _onnxHistory = onnxHistory;
#if WINDOWS
        _onnxRunner = BenchmarkOnnxExecutionBootstrap.CreateOnnxRunner();
#endif
        _audioRunner = new AudioPrepBenchmarkRunner();
        _dubbingRunner = new DubbingBenchmarkRunner();
        _controlledRunner = new ControlledDubbingBenchmarkRunner(history, serviceConfigurator);
    }

    /// <inheritdoc />
    public async Task<BenchmarkReport> RunOnnxModelBenchmarkAsync(
        BenchmarkRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
#if WINDOWS
        BenchmarkOnnxExecutionBootstrap.ConfigureExecution(request.WindowsMlDevicePolicyKey);
        if (_onnxRunner is null)
        {
            return new BenchmarkReport(
                Scenario: "onnx-model",
                ModelPath: request.ModelPath ?? string.Empty,
                ReportPath: request.ReportPath ?? string.Empty,
                Status: BenchmarkStatus.Failed,
                RequestedProvider: request.ProviderPreference.ToString(),
                SelectedProvider: "None",
                RunCount: request.RunCount,
                SupportsExecution: false,
                ModelSizeBytes: 0,
                Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
                FailureReason: "ONNX model benchmark runner is unavailable on this Windows host.",
                Notes: [],
                GeneratedAtUtc: DateTimeOffset.UtcNow);
        }

        BenchmarkReport report = await _onnxRunner.RunAsync(request, ct).ConfigureAwait(false);
        return await PersistOnnxHistoryAsync(report, ct).ConfigureAwait(false);
#else
        await Task.CompletedTask.ConfigureAwait(false);
        return new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: request.ModelPath ?? string.Empty,
            ReportPath: request.ReportPath ?? string.Empty,
            Status: BenchmarkStatus.Failed,
            RequestedProvider: request.ProviderPreference.ToString(),
            SelectedProvider: "None",
            RunCount: request.RunCount,
            SupportsExecution: false,
            ModelSizeBytes: 0,
            Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
            FailureReason: "ONNX model benchmarks require Windows (Windows ML / ONNX Runtime EP support).",
            Notes: [],
            GeneratedAtUtc: DateTimeOffset.UtcNow);
#endif
    }

    /// <inheritdoc />
    public async Task<AudioPrepBenchmarkReport> RunAudioPrepBenchmarkAsync(
        AudioPrepBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await _audioRunner.RunAsync(options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DubbingBenchmarkReport> RunDubbingBenchmarkAsync(
        DubbingBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await _dubbingRunner.RunAsync(options, ct).ConfigureAwait(false);
    }

    public Task<BenchmarkEvidenceReport> RunControlledDubbingBenchmarkAsync(
        ControlledDubbingBenchmarkOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _controlledRunner.RunAsync(options, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListOnnxBenchmarkHistoryAsync(
        int limit, CancellationToken ct = default)
    {
        if (_onnxHistory is null || limit <= 0)
        {
            return [];
        }

        return await _onnxHistory.ListRecentAsync(limit, ct).ConfigureAwait(false);
    }

#if WINDOWS
    private async Task<BenchmarkReport> PersistOnnxHistoryAsync(BenchmarkReport report, CancellationToken ct)
    {
        if (_onnxHistory is null)
        {
            return report with
            {
                HistoryPersistence = new BenchmarkHistoryPersistenceResult(
                    BenchmarkHistoryPersistenceStatus.NotRequested)
            };
        }

        try
        {
            string modelId = Path.GetFileNameWithoutExtension(report.ModelPath) is { Length: > 0 } name
                ? name
                : report.ModelPath;
            BenchmarkRunRecord record = BenchmarkRunRecord.FromReport(
                report.RunId ?? Guid.NewGuid(), modelId, report);
            await _onnxHistory.AddAsync(record, ct).ConfigureAwait(false);
            return report with
            {
                RunId = record.Id,
                HistoryPersistence = new BenchmarkHistoryPersistenceResult(
                    BenchmarkHistoryPersistenceStatus.Saved)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return report with
            {
                HistoryPersistence = new BenchmarkHistoryPersistenceResult(
                    BenchmarkHistoryPersistenceStatus.Failed, ex.Message)
            };
        }
    }
#endif
}
