namespace Trackdub.Domain;

public sealed record BenchmarkRequest(
    string ModelPath,
    string ReportPath,
    BenchmarkProviderPreference ProviderPreference,
    int RunCount,
    string? WindowsMlDevicePolicyKey = null);

public sealed record BenchmarkMeasurements(
    double? ColdLoadMilliseconds,
    double? WarmupMilliseconds,
    double? WarmLatencyAverageMilliseconds,
    double? WarmLatencyMinimumMilliseconds,
    double? WarmLatencyMaximumMilliseconds,
    double? AudioDurationSeconds,
    double? RealTimeFactorAverage,
    double? OptionsCreateMilliseconds = null,
    string? EngineCacheOutcome = null,
    long? EngineCacheBytesAdded = null,
    int? EngineCacheFilesAdded = null,
    string? ColdLoadDominantPhase = null);

public sealed record BenchmarkReport(
    string Scenario,
    string ModelPath,
    string ReportPath,
    BenchmarkStatus Status,
    string RequestedProvider,
    string SelectedProvider,
    int RunCount,
    bool SupportsExecution,
    long ModelSizeBytes,
    BenchmarkMeasurements Measurements,
    string? FailureReason,
    IReadOnlyList<string> Notes,
    DateTimeOffset GeneratedAtUtc);

public enum BenchmarkProviderPreference
{
    Auto = 0,
    Cpu = 1,
    Dml = 2,
    TensorRtRtx = 3,
    Migraphx = 4,
    Cuda = 5,
    TensorRt = 6,
    Qnn = 7
}

public enum BenchmarkStatus
{
    Planned,
    Completed,
    Failed
}

public sealed record BenchmarkRunRecord
{
    public BenchmarkRunRecord(
        Guid id,
        string modelId,
        string modelPath,
        string reportPath,
        BenchmarkStatus status,
        string requestedProvider,
        string selectedProvider,
        int runCount,
        bool supportsExecution,
        long modelSizeBytes,
        double? coldLoadMilliseconds,
        double? warmLatencyAverageMilliseconds,
        double? warmLatencyMinimumMilliseconds,
        double? warmLatencyMaximumMilliseconds,
        string? failureReason,
        DateTimeOffset generatedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Benchmark run id is required.", nameof(id));
        }

        Id = id;
        ModelId = NormalizeRequired(modelId, nameof(modelId), "Model id is required.");
        ModelPath = NormalizeRequired(modelPath, nameof(modelPath), "Model path is required.");
        ReportPath = NormalizeRequired(reportPath, nameof(reportPath), "Report path is required.");
        Status = status;
        RequestedProvider = NormalizeRequired(requestedProvider, nameof(requestedProvider), "Requested provider is required.");
        SelectedProvider = NormalizeRequired(selectedProvider, nameof(selectedProvider), "Selected provider is required.");
        RunCount = runCount;
        SupportsExecution = supportsExecution;
        ModelSizeBytes = modelSizeBytes;
        ColdLoadMilliseconds = coldLoadMilliseconds;
        WarmLatencyAverageMilliseconds = warmLatencyAverageMilliseconds;
        WarmLatencyMinimumMilliseconds = warmLatencyMinimumMilliseconds;
        WarmLatencyMaximumMilliseconds = warmLatencyMaximumMilliseconds;
        FailureReason = NormalizeOptional(failureReason);
        GeneratedAtUtc = generatedAtUtc;
    }

    public Guid Id { get; init; }

    public string ModelId { get; init; }

    public string ModelPath { get; init; }

    public string ReportPath { get; init; }

    public BenchmarkStatus Status { get; init; }

    public string RequestedProvider { get; init; }

    public string SelectedProvider { get; init; }

    public int RunCount { get; init; }

    public bool SupportsExecution { get; init; }

    public long ModelSizeBytes { get; init; }

    public double? ColdLoadMilliseconds { get; init; }

    public double? WarmLatencyAverageMilliseconds { get; init; }

    public double? WarmLatencyMinimumMilliseconds { get; init; }

    public double? WarmLatencyMaximumMilliseconds { get; init; }

    public string? FailureReason { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public static BenchmarkRunRecord Create(
        string modelId,
        string modelPath,
        string reportPath,
        BenchmarkStatus status,
        string requestedProvider,
        string selectedProvider,
        int runCount,
        bool supportsExecution,
        long modelSizeBytes,
        double? coldLoadMilliseconds,
        double? warmLatencyAverageMilliseconds,
        double? warmLatencyMinimumMilliseconds,
        double? warmLatencyMaximumMilliseconds,
        string? failureReason,
        DateTimeOffset generatedAtUtc)
    {
        return new BenchmarkRunRecord(
            Guid.NewGuid(),
            modelId,
            modelPath,
            reportPath,
            status,
            requestedProvider,
            selectedProvider,
            runCount,
            supportsExecution,
            modelSizeBytes,
            coldLoadMilliseconds,
            warmLatencyAverageMilliseconds,
            warmLatencyMinimumMilliseconds,
            warmLatencyMaximumMilliseconds,
            failureReason,
            generatedAtUtc);
    }

    private static string NormalizeRequired(string value, string paramName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, paramName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
