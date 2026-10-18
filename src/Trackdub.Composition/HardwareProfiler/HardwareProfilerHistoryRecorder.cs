using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;

namespace Trackdub.Composition.HardwareProfiler;

public sealed class HardwareProfilerHistoryRecorder(
    IUserBenchmarkRepository benchmarkRepository,
    IAppStoragePaths storagePaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private int legacyImportCompleted;
    private readonly SemaphoreSlim legacyImportGate = new(1, 1);

    public async Task RecordSnapshotAsync(
        HardwareProfilerSnapshot snapshot,
        string reportsRoot,
        int runCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportsRoot);

        foreach (StageBenchmarkScenarioResult scenario in snapshot.Scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BenchmarkRunRecord record = MapScenarioToRecord(snapshot, scenario, reportsRoot, runCount);
            await benchmarkRepository.AddAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnsureLegacyImportAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref legacyImportCompleted) == 1)
        {
            return;
        }

        await legacyImportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref legacyImportCompleted) == 1)
            {
                return;
            }

            string runsRoot = Path.Join(storagePaths.UserDataRoot, "hardware-profiler", "runs");
            if (!Directory.Exists(runsRoot))
            {
                Volatile.Write(ref legacyImportCompleted, 1);
                return;
            }

            foreach (string runDirectory in Directory.EnumerateDirectories(runsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string snapshotPath = Path.Join(runDirectory, "snapshot.json");
                if (!File.Exists(snapshotPath))
                {
                    continue;
                }

                HardwareProfilerSnapshot? snapshot;
                try
                {
                    await using FileStream stream = File.OpenRead(snapshotPath);
                    LegacyStoredSnapshot? stored = await JsonSerializer
                        .DeserializeAsync<LegacyStoredSnapshot>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    snapshot = stored?.ToDomain();
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (snapshot is null)
                {
                    continue;
                }

                if (await benchmarkRepository.ContainsEvidenceAsync(snapshot.EvidenceId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string reportsRoot = Path.Join(
                    storagePaths.UserDataRoot,
                    "hardware-profiler",
                    "reports",
                    snapshot.Fingerprint.Hash[..Math.Min(12, snapshot.Fingerprint.Hash.Length)]);

                try
                {
                    await RecordSnapshotAsync(snapshot, reportsRoot, runCount: 3, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    continue;
                }
            }

            Volatile.Write(ref legacyImportCompleted, 1);
        }
        finally
        {
            legacyImportGate.Release();
        }
    }

    internal static BenchmarkRunRecord MapScenarioToRecord(
        HardwareProfilerSnapshot snapshot,
        StageBenchmarkScenarioResult scenario,
        string reportsRoot,
        int runCount)
    {
        string scenarioName = ResolveScenarioName(scenario.Scenario);
        string reportPath = ResolveReportPath(reportsRoot, scenarioName) ??
                            Path.Join(reportsRoot, $"{scenarioName}-missing-report.json");
        BenchmarkReport? report = TryReadBenchmarkReport(reportPath);

        string modelId = BuildProfilerModelId(
            snapshot.EvidenceId,
            snapshot.Fingerprint.Hash,
            scenarioName);

        return new BenchmarkRunRecord(
            CreateDeterministicRunId(snapshot.EvidenceId, scenario.Scenario),
            modelId,
            report?.ModelPath ?? $"bundled:{scenarioName}",
            reportPath,
            scenario.Status,
            scenario.RequestedProvider,
            scenario.SelectedProvider,
            report?.RunCount ?? runCount,
            report?.SupportsExecution ?? scenario.Status == BenchmarkStatus.Completed,
            report?.ModelSizeBytes ?? 0,
            report?.Measurements.ColdLoadMilliseconds,
            scenario.WarmLatencyAverageMilliseconds ?? report?.Measurements.WarmLatencyAverageMilliseconds,
            report?.Measurements.WarmLatencyMinimumMilliseconds,
            report?.Measurements.WarmLatencyMaximumMilliseconds,
            scenario.FailureReason ?? report?.FailureReason,
            snapshot.CompletedAtUtc)
        {
            FirstRunMilliseconds = report?.Measurements.FirstRunMilliseconds,
            WarmupMilliseconds = report?.Measurements.WarmupMilliseconds,
            WarmupRunCount = report?.Measurements.WarmupRunCount,
            SampleCount = report?.Measurements.SampleCount,
            WarmLatencyP50Milliseconds = report?.Measurements.WarmLatencyP50Milliseconds,
            WarmLatencyP90Milliseconds = report?.Measurements.WarmLatencyP90Milliseconds,
            WarmLatencyP95Milliseconds = report?.Measurements.WarmLatencyP95Milliseconds,
            WarmLatencyP99Milliseconds = report?.Measurements.WarmLatencyP99Milliseconds,
            MeasuredWallTimeMilliseconds = report?.Measurements.MeasuredWallTimeMilliseconds,
            PassesPerSecond = report?.Measurements.PassesPerSecond,
            ThroughputUnitsPerSecond = report?.Measurements.ThroughputUnitsPerSecond,
            ThroughputUnit = report?.Measurements.ThroughputUnit,
            AudioDurationSeconds = report?.Measurements.AudioDurationSeconds,
            RealTimeFactorAverage = scenario.RealTimeFactorAverage ?? report?.Measurements.RealTimeFactorAverage,
            TimingMode = report?.Metadata?.Configuration?.TimingMode,
            InputProfileId = report?.Metadata?.Configuration?.InputProfileId,
            ComparisonKey = report?.Metadata?.ComparisonKey,
            Metadata = report?.Metadata
        };
    }

    internal static string BuildProfilerModelId(Guid evidenceId, string fingerprintHash, string scenarioName) =>
        $"hardware-profiler:{evidenceId:D}:{fingerprintHash}:{scenarioName}";

    private static Guid CreateDeterministicRunId(Guid evidenceId, StageBenchmarkScenario scenario)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{evidenceId:D}:{scenario}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static string ResolveScenarioName(StageBenchmarkScenario scenario)
    {
        StageBenchmarkModelCatalog.ScenarioDefinition? definition =
            StageBenchmarkModelCatalog.All.FirstOrDefault(item => item.Scenario == scenario);
        return definition?.ScenarioName ?? scenario.ToString().ToLowerInvariant();
    }

    private static string? ResolveReportPath(string reportsRoot, string scenarioName)
    {
        if (!Directory.Exists(reportsRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(reportsRoot, $"{scenarioName}-*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static BenchmarkReport? TryReadBenchmarkReport(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(reportPath);
            return JsonSerializer.Deserialize<BenchmarkReport>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record LegacyStoredSnapshot(
        Guid EvidenceId,
        LegacyFingerprint Fingerprint,
        IReadOnlyList<LegacyScenarioResult> Scenarios,
        LegacyRecommendation Recommendation,
        DateTimeOffset CompletedAtUtc)
    {
        public HardwareProfilerSnapshot ToDomain() =>
            new(
                EvidenceId,
                Fingerprint.ToDomain(),
                Scenarios.Select(static scenario => scenario.ToDomain()).ToArray(),
                Recommendation.ToDomain(),
                CompletedAtUtc);
    }

    private sealed record LegacyFingerprint(
        string Hash,
        string OperatingSystem,
        string Architecture,
        string? GpuDescription,
        long? TotalRamBytes,
        long? GpuDedicatedMemoryBytes,
        DateTimeOffset CapturedAtUtc)
    {
        public HardwareFingerprint ToDomain() =>
            new(
                Hash,
                OperatingSystem,
                Architecture,
                GpuDescription,
                TotalRamBytes,
                GpuDedicatedMemoryBytes,
                CapturedAtUtc);
    }

    private sealed record LegacyScenarioResult(
        StageBenchmarkScenario Scenario,
        BenchmarkStatus Status,
        string RequestedProvider,
        string SelectedProvider,
        double? WarmLatencyAverageMilliseconds,
        double? RealTimeFactorAverage,
        string? FailureReason)
    {
        public StageBenchmarkScenarioResult ToDomain() =>
            new(
                Scenario,
                Status,
                RequestedProvider,
                SelectedProvider,
                WarmLatencyAverageMilliseconds,
                RealTimeFactorAverage,
                FailureReason);
    }

    private sealed record LegacyRecommendation(
        HardwareQualityPreset Preset,
        string Summary,
        IReadOnlyList<string> RationaleLines,
        string SuggestedModelTierPreference)
    {
        public HardwarePresetRecommendation ToDomain() =>
            new(Preset, Summary, RationaleLines, SuggestedModelTierPreference);
    }
}
