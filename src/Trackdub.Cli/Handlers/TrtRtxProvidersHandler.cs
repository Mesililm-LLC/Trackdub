using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Application.Runtime;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Headless TensorRT RTX EP ABI plugin status and install for operators.
/// </summary>
internal static class TrtRtxProvidersHandler
{
    private const string LicenseReference =
        "NVIDIA TensorRT-RTX: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html; CUDA EULA: https://docs.nvidia.com/cuda/eula/index.html";

    public static async Task<int> StatusAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        string? environmentDirectory = Environment.GetEnvironmentVariable(
            TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        string defaultInstallDirectory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
            storagePaths.UserDataRoot,
            ResolveNativeRuntimeIdentifier());

        var payload = new TrtRtxStatusOutput
        {
            Ready = snapshot.IsReady,
            SupportedPlatform = snapshot.IsSupportedPlatform,
            ProviderId = snapshot.ProviderId,
            Route = snapshot.RouteDisplay,
            StatusLabel = snapshot.StatusLabel,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsHardwareEligible = snapshot.IsHardwareEligible,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            IsRegisteredWithOrt = snapshot.IsRegisteredWithOrt,
            LicenseAccepted = settings.NvidiaTensorRtRtxLicenseAccepted,
            PluginDirectory = ResolveEffectivePluginDirectory(
                settings.TensorRtRtxPluginDirectory,
                environmentDirectory,
                defaultInstallDirectory),
            StudioPluginDirectory = settings.TensorRtRtxPluginDirectory,
            EnvironmentPluginDirectory = environmentDirectory,
            DefaultInstallDirectory = defaultInstallDirectory,
            InstallHint = snapshot.InstallHint,
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return snapshot.IsReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    public static async Task<int> InstallAsync(
        TrackdubSessionFactory factory,
        bool acceptLicense,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        ITrtRtxEpInstaller installer = factory.GetRequiredService<ITrtRtxEpInstaller>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (acceptLicense)
        {
            if (!settings.NvidiaTensorRtRtxLicenseAccepted)
            {
                settings = settings with { NvidiaTensorRtRtxLicenseAccepted = true };
                await settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            await factory
                .PersistNvidiaTensorRtRtxLicenseAsync(cancellationToken)
                .ConfigureAwait(false);
            await progressOutput.WriteLineAsync(
                $"Accepted NVIDIA TensorRT RTX license flag in studio settings. Reference: {LicenseReference}")
                .ConfigureAwait(false);
        }

        if (!settings.NvidiaTensorRtRtxLicenseAccepted)
        {
            var blocked = new TrtRtxInstallOutput
            {
                Succeeded = false,
                FailureDetail =
                    "NVIDIA TensorRT RTX license not accepted. Pass --accept-license after reviewing the license terms, or accept in Model Manager.",
                LicenseReference = LicenseReference,
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, CliJsonOptions.Default))
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        var progress = new Progress<string>(message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                progressOutput.WriteLine(message);
            }
        });

        TrtRtxEpInstallResult result = await installer
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        // EnsureInstalledAsync may install/register the EP, changing process state. Invalidate the
        // shared (process-wide singleton) readiness cache so the verification re-probe below sees
        // the freshly installed state instead of a stale pre-install snapshot. This forces a real
        // re-probe; it does not fabricate a ready result.
        if (factory.GetRequiredService<ITensorRtRtxReadinessProbe>() is IReadinessProbeCache probeCache)
        {
            probeCache.Invalidate();
        }

        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();
        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        var payload = new TrtRtxInstallOutput
        {
            Succeeded = result.Succeeded,
            FailureDetail = result.FailureDetail,
            Ready = snapshot.IsReady,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            LicenseReference = LicenseReference,
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonOptions.Default))
            .ConfigureAwait(false);

        return result.Succeeded && snapshot.IsReady
            ? Program.ExitSuccess
            : Program.ExitPipelineFailure;
    }

    public static async Task<int> SmokeAsync(
        TrackdubSessionFactory factory,
        IReadOnlyList<string>? modelFilter,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();

        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = FilterTargets(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            modelFilter);

        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        if (!snapshot.IsReady)
        {
            var blocked = new TrtRtxSmokeOutput
            {
                Ready = false,
                Blocker = snapshot.Blocker.ToString(),
                Detail = snapshot.Detail,
                Attempted = 0,
                Passed = 0,
                Failed = 0,
                Skipped = targets.Count,
                Targets = [],
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, SmokeJsonOptions)).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(
                    snapshot.Detail ?? "TensorRT RTX EP ABI plugin is not ready. Run trackdub providers trt-rtx status.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        if (targets.Count == 0)
        {
            await progressOutput.WriteLineAsync(
                    $"No TRT RTX smoke targets matched --model filter: {string.Join(", ", modelFilter ?? [])}")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        // Emit each target result as it completes so a fatal native crash mid-catalog
        // does not lose the per-model evidence gathered up to that point.
        var progress = new ImmediateTargetProgress(progressOutput);

        TrtRtxStarterPackSmokeReport report;
        try
        {
            report = await TrtRtxStarterPackSmokeRunner
                .RunAsync(
                    storagePaths.ModelCacheDirectory,
                    targets,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new TrtRtxSmokeOutput
            {
                Ready = true,
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

        var payload = new TrtRtxSmokeOutput
        {
            Ready = true,
            Scope = TrtRtxSmokeCatalog.ScopeName,
            Attempted = report.Attempted,
            Passed = report.Passed,
            Failed = report.Failed,
            Skipped = report.Skipped,
            Targets = report.Targets
                .Select(target => new TrtRtxSmokeTargetOutput
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
                $"TRT RTX smoke summary: passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}.")
            .ConfigureAwait(false);

        if (!report.HasAttempts)
        {
            await progressOutput.WriteLineAsync(
                    "No TRT RTX smoke targets were cached locally. Download starter-pack models first.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        return report.HasFailures ? Program.ExitPipelineFailure : Program.ExitSuccess;
    }

    /// <summary>
    /// Runs the real TRT RTX smoke path against an explicit ONNX entry path — e.g. an
    /// Olive-recipe-staged output directory — instead of the model cache. Used by
    /// Olive validator scripts to confirm a staged model actually loads and the effective
    /// provider is TensorRT RTX (not a silent CPU/DirectML fallback) before trusting it.
    /// </summary>
    public static async Task<int> VerifyAsync(
        TrackdubSessionFactory factory,
        string modelId,
        string entryPath,
        string? variant,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();

        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        if (!snapshot.IsReady)
        {
            var blocked = new TrtRtxVerifyOutput
            {
                Ready = false,
                Passed = false,
                ModelId = modelId,
                EntryPath = entryPath,
                Blocker = snapshot.Blocker.ToString(),
                Detail = snapshot.Detail,
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, SmokeJsonOptions)).ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        TrtRtxStarterPackSmokeTargetResult result;
        try
        {
            result = await TrtRtxStarterPackSmokeRunner
                .VerifyEntryPathAsync(modelId, entryPath, variant, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new TrtRtxVerifyOutput
            {
                Ready = true,
                Passed = false,
                ModelId = modelId,
                EntryPath = entryPath,
                Detail = ex.Message,
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(failed, SmokeJsonOptions)).ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        finally
        {
            await EvictIdleSessionsAsync(factory).ConfigureAwait(false);
        }

        bool passed = result.Status == TrtRtxStarterPackSmokeTargetStatus.Passed;
        var payload = new TrtRtxVerifyOutput
        {
            Ready = true,
            Passed = passed,
            ModelId = modelId,
            EntryPath = entryPath,
            Detail = result.Detail,
        };
        await output.WriteLineAsync(JsonSerializer.Serialize(payload, SmokeJsonOptions)).ConfigureAwait(false);

        return passed ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    private static Task EvictIdleSessionsAsync(TrackdubSessionFactory factory) =>
        factory.GetRequiredService<IInferenceSessionPoolEvictor>().EvictAllIdleAsync(CancellationToken.None);

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

    private static string? ResolveEffectivePluginDirectory(
        string? studioDirectory,
        string? environmentDirectory,
        string defaultInstallDirectory)
    {
        // Mirrors TensorRtRtxPluginLocator: a previous managed bundle left in settings/env is skipped.
        if (!string.IsNullOrWhiteSpace(studioDirectory) &&
            !TensorRtRtxProviderConstants.IsSupersededManagedInstallDirectory(studioDirectory, defaultInstallDirectory))
        {
            return studioDirectory;
        }

        if (!string.IsNullOrWhiteSpace(environmentDirectory) &&
            !TensorRtRtxProviderConstants.IsSupersededManagedInstallDirectory(environmentDirectory, defaultInstallDirectory))
        {
            return environmentDirectory;
        }

        return Directory.Exists(defaultInstallDirectory) ? defaultInstallDirectory : null;
    }

    private static string ResolveNativeRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                _ => "osx-x64",
            };
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            _ => "linux-x64",
        };
    }

    private sealed class TrtRtxStatusOutput
    {
        public bool Ready { get; init; }
        public bool SupportedPlatform { get; init; }
        public string? ProviderId { get; init; }
        public string? Route { get; init; }
        public string? StatusLabel { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsHardwareEligible { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public bool IsRegisteredWithOrt { get; init; }
        public bool LicenseAccepted { get; init; }
        public string? PluginDirectory { get; init; }
        public string? StudioPluginDirectory { get; init; }
        public string? EnvironmentPluginDirectory { get; init; }
        public string? DefaultInstallDirectory { get; init; }
        public string? InstallHint { get; init; }
    }

    private sealed class TrtRtxInstallOutput
    {
        public bool Succeeded { get; init; }
        public bool Ready { get; init; }
        public string? FailureDetail { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public string? LicenseReference { get; init; }
    }

    private sealed class TrtRtxSmokeOutput
    {
        public bool Ready { get; init; }
        public string? Scope { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public int Attempted { get; init; }
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public IReadOnlyList<TrtRtxSmokeTargetOutput> Targets { get; init; } = [];
    }

    private sealed class TrtRtxSmokeTargetOutput
    {
        public string Label { get; init; } = string.Empty;
        public string ModelReference { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? Detail { get; init; }
    }

    private sealed class TrtRtxVerifyOutput
    {
        public bool Ready { get; init; }
        public bool Passed { get; init; }
        public string? ModelId { get; init; }
        public string? EntryPath { get; init; }
        public string? Blocker { get; init; }
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
