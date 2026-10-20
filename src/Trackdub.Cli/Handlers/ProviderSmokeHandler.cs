using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Headless per-model smoke tests for the native CUDA and TensorRT execution providers.
/// </summary>
internal static class ProviderSmokeHandler
{
    public static async Task<int> SmokeAsync(
        TrackdubSessionFactory factory,
        ExecutionProviderKind provider,
        IReadOnlyList<string>? modelFilter,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        IHardwareProfileProvider hardwareProfileProvider =
            factory.GetRequiredService<IHardwareProfileProvider>();
        IExecutionProviderDiscovery discovery =
            factory.GetRequiredService<IExecutionProviderDiscovery>();

        string scope = ScopeForProvider(provider);
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = FilterTargets(
            TrtRtxSmokeCatalog.TargetsForProvider(provider),
            modelFilter);

        HardwareProfile hardwareProfile = await hardwareProfileProvider
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery
            .DiscoverAsync(hardwareProfile, cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviderAvailability? availability = availabilities
            .FirstOrDefault(candidate => candidate.Provider == provider);
        bool isAvailable = provider is ExecutionProviderKind.Cpu
            || (availability?.IsAvailable ?? false);

        if (!isAvailable)
        {
            var blocked = new ProviderSmokeOutput
            {
                Ready = false,
                Scope = scope,
                Detail = availability?.Detail,
                Attempted = 0,
                Passed = 0,
                Failed = 0,
                Skipped = targets.Count,
                Targets = [],
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, SmokeJsonOptions)).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(
                    availability?.Detail ?? $"Provider {provider} is not available on this machine.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        if (targets.Count == 0)
        {
            await progressOutput.WriteLineAsync(
                    $"No {scope} smoke targets matched --model filter: {string.Join(", ", modelFilter ?? [])}")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        // Emit each target result as it completes so a fatal native crash mid-catalog
        // does not lose the per-model evidence gathered up to that point.
        var progress = new ImmediateTargetProgress(progressOutput);

        TrtRtxStarterPackSmokeReport report;
        try
        {
            IExecutionProviderSmokeTester smokeTester =
                factory.GetRequiredService<IExecutionProviderSmokeTester>();
            report = await TrtRtxStarterPackSmokeRunner
                .RunAsync(
                    storagePaths.ModelCacheDirectory,
                    smokeTester,
                    targets,
                    progress,
                    cancellationToken,
                    provider)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or NotSupportedException)
        {
            var failed = new ProviderSmokeOutput
            {
                Ready = true,
                Scope = scope,
                Detail = ex.Message,
                Attempted = 0,
                Passed = 0,
                Failed = 1,
                Skipped = 0,
                Targets = [],
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(failed, SmokeJsonOptions)).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        finally
        {
            await EvictIdleSessionsAsync(factory).ConfigureAwait(false);
        }

        var payload = new ProviderSmokeOutput
        {
            Ready = true,
            Scope = scope,
            Attempted = report.Attempted,
            Passed = report.Passed,
            Failed = report.Failed,
            Skipped = report.Skipped,
            Targets = report.Targets
                .Select(target => new ProviderSmokeTargetOutput
                {
                    Label = target.Label,
                    ModelReference = target.ModelReference,
                    Status = target.Status.ToString().ToLowerInvariant(),
                    Detail = target.Detail,
                })
                .ToArray(),
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, SmokeJsonOptions)).ConfigureAwait(false);

        await progressOutput.WriteLineAsync(
                $"{scope} smoke summary: passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}.")
            .ConfigureAwait(false);

        if (!report.HasAttempts)
        {
            await progressOutput.WriteLineAsync(
                    $"No {scope} smoke targets were cached locally. Download starter-pack models first.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        return report.HasFailures ? Program.ExitPipelineFailure : Program.ExitSuccess;
    }

    private static Task EvictIdleSessionsAsync(TrackdubSessionFactory factory) =>
        factory.GetRequiredService<IInferenceSessionPoolEvictor>().EvictAllIdleAsync(CancellationToken.None);

    private static string ScopeForProvider(ExecutionProviderKind provider) => provider switch
    {
        ExecutionProviderKind.Cuda => "cuda-smoke",
        ExecutionProviderKind.TensorRt => "tensorrt-smoke",
        ExecutionProviderKind.TensorRTRtx => "trt-rtx-smoke",
        _ => "provider-smoke",
    };

    private static IReadOnlyList<TrtRtxSmokeCatalog.Target> FilterTargets(
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        IReadOnlyList<string>? modelFilter)
    {
        if (modelFilter is null || modelFilter.Count == 0)
        {
            return targets;
        }

        return targets
            .Where(target => modelFilter.Any(filter =>
                !string.IsNullOrWhiteSpace(filter)
                && (target.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || target.ModelReference.Contains(filter, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    private static readonly JsonSerializerOptions SmokeJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ProviderSmokeOutput
    {
        public bool Ready { get; init; }
        public string? Scope { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public int Attempted { get; init; }
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public IReadOnlyList<ProviderSmokeTargetOutput> Targets { get; init; } = [];
    }

    private sealed class ProviderSmokeTargetOutput
    {
        public string Label { get; init; } = string.Empty;
        public string ModelReference { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? Detail { get; init; }
    }

    // Writes synchronously rather than via Progress<T> (which queues to the thread pool) so
    // the line is flushed even if the next target crashes the process in native code.
    private sealed class ImmediateTargetProgress(TextWriter progressOutput)
        : IProgress<TrtRtxStarterPackSmokeTargetResult>
    {
        public void Report(TrtRtxStarterPackSmokeTargetResult target)
        {
            string line = target.Status switch
            {
                TrtRtxStarterPackSmokeTargetStatus.Passed =>
                    $"PASS {target.Label} ({target.ModelReference})",
                TrtRtxStarterPackSmokeTargetStatus.Skipped =>
                    $"SKIP {target.Label}: {target.Detail ?? "model not cached locally"}",
                TrtRtxStarterPackSmokeTargetStatus.Failed =>
                    $"FAIL {target.Label}: {target.Detail ?? "smoke test failed"}",
                _ => $"{target.Label}: {target.Status}",
            };

            progressOutput.WriteLine(line);
            progressOutput.Flush();
        }
    }
}
