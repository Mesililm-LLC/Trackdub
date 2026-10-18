using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Benchmarking;

/// <summary>
/// Builds the typed evidence record (<see cref="BenchmarkRunMetadata"/>) that accompanies a
/// benchmark report. Collection runs outside every timed region; anything that cannot be
/// observed is reported through <see cref="BenchmarkEnvironmentInfo.UnavailableReasons"/> or
/// run-level warnings rather than fabricated.
/// </summary>
internal static class BenchmarkMetadataBuilder
{
    internal const string SyntheticInputProfileId = "synthetic-v1";
    internal const string ZerosInputProfileId = "zeros-v1";
    internal const string ProfileInputProfileId = "trt-profile-opt-v1";
    private const int ComparisonKeyVersion = 2;

    internal static BenchmarkEnvironmentInfo CaptureEnvironment()
    {
        var unavailable = new List<string>();

        string? cpuName = null;
        string? machineGpuName = null;
        string? gpuArchitecture = null;
        string? driverVersion = null;
        long? gcAvailableMemoryBytes = null;
        try
        {
            HardwareProfile hardware = new MachineHardwareProfileProvider()
                .GetCurrentAsync().GetAwaiter().GetResult();
            cpuName = hardware.CpuName;
            machineGpuName = hardware.GpuDescription;
            driverVersion = hardware.GpuDriverVersion;
            if (hardware.NvidiaGpuArchitecture is not NvidiaGpuArchitectureBucket.Unknown)
            {
                gpuArchitecture = hardware.NvidiaGpuArchitecture.ToString();
            }

            if (hardware.TotalRamMb > 0)
            {
                // MachineHardwareProfileProvider derives this from GC.GetGCMemoryInfo(); it is the
                // runtime's memory limit, not physical RAM.
                gcAvailableMemoryBytes = hardware.TotalRamMb * 1024 * 1024;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            unavailable.Add($"hardware-profile:{ex.GetType().Name}");
        }

        string? hostOrtVersion = null;
        try
        {
            hostOrtVersion = EpContextArtifact.HostOrtRuntimeVersion;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            unavailable.Add($"host-ort-version:{ex.GetType().Name}");
        }

        string? epContextFingerprint = null;
        try
        {
            epContextFingerprint = EpContextLoadPathResolver.CurrentEnvironmentFingerprint;
            gpuArchitecture ??= EpContextLoadPathResolver.CurrentHardware.GpuArchitecture;
            driverVersion ??= EpContextLoadPathResolver.CurrentHardware.DriverVersion;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            unavailable.Add($"ep-context-fingerprint:{ex.GetType().Name}");
        }

        string? ortManagedVersion = null;
        try
        {
            ortManagedVersion = typeof(OrtEnv).Assembly.GetName().Version?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            unavailable.Add($"ort-managed-version:{ex.GetType().Name}");
        }

        return new BenchmarkEnvironmentInfo
        {
            CpuName = cpuName,
            PhysicalRamBytes = null,
            GcAvailableMemoryBytes = gcAvailableMemoryBytes,
            OsDescription = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            OrtManagedAssemblyVersion = ortManagedVersion,
            HostOrtRuntimeVersion = hostOrtVersion,
            MachineGpuName = machineGpuName,
            GpuArchitecture = gpuArchitecture,
            DriverVersion = driverVersion,
            ConfiguredPluginFingerprint = TensorRtRtxProviderConstants.BundledFingerprintVersion,
            ObservedPluginIdentity = ProbeObservedPluginIdentity(),
            ExpectedPluginVersion = TensorRtRtxProviderConstants.BundledVersion,
            ExpectedTensorRtRtxVersion = TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersion,
            EpContextEnvironmentFingerprint = epContextFingerprint,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            UnavailableReasons = unavailable
        };
    }

    /// <summary>
    /// File-version evidence for the loaded TensorRT-RTX plugin binaries when a candidate
    /// directory is visible; null when nothing is installed where we can look. This records
    /// what is on disk; it is not proof the provider executed graph nodes.
    /// </summary>
    private static string? ProbeObservedPluginIdentity()
    {
        var candidates = new List<string>();
        string? overrideDirectory = Environment.GetEnvironmentVariable(
            TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            candidates.Add(overrideDirectory);
        }

        candidates.Add(AppContext.BaseDirectory);

        foreach (string directory in candidates)
        {
            try
            {
                string pluginPath = Path.Join(directory, TensorRtRtxProviderConstants.PluginLibraryFileName);
                if (!File.Exists(pluginPath))
                {
                    continue;
                }

                FileVersionInfo pluginVersion = FileVersionInfo.GetVersionInfo(pluginPath);
                var parts = new List<string>
                {
                    $"{TensorRtRtxProviderConstants.PluginLibraryFileName}@{pluginVersion.FileVersion ?? "unknown"}"
                };

                string runtimePath = Path.Join(directory, TensorRtRtxProviderConstants.TensorRtRuntimeFileName);
                if (File.Exists(runtimePath))
                {
                    FileVersionInfo runtimeVersion = FileVersionInfo.GetVersionInfo(runtimePath);
                    parts.Add($"{TensorRtRtxProviderConstants.TensorRtRuntimeFileName}@{runtimeVersion.FileVersion ?? "unknown"}");
                }

                return string.Join(";", parts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Trace.TraceWarning(
                    $"Could not inspect TensorRT-RTX plugin identity in '{directory}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Effective EP devices a session reports for its inputs, e.g.
    /// <c>NvTensorRTRTXExecutionProvider|GPU|10DE:2F0C</c>. Empty when ORT cannot report them.
    /// </summary>
    internal static IReadOnlyList<string> DescribeSessionDevices(InferenceSession session)
    {
        try
        {
            var devices = new List<string>();
            foreach (OrtEpDevice device in session.GetEpDeviceForInputs())
            {
                string hardwareIdentity = device.HardwareDevice.VendorId is { } vendorId
                    ? $"{vendorId:X4}:{device.HardwareDevice.DeviceId:X4}"
                    : "unknown";
                devices.Add($"{device.EpName}|{device.HardwareDevice.Type}|{hardwareIdentity}");
            }

            return devices;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<BenchmarkTensorDescription> DescribeTensors(
        IReadOnlyDictionary<string, NodeMetadata> tensorMetadata) =>
        tensorMetadata
            .Select(pair => new BenchmarkTensorDescription(
                pair.Key,
                pair.Value.ElementDataType.ToString(),
                pair.Value.Dimensions.ToArray(),
                pair.Value.SymbolicDimensions?.ToArray()))
            .ToArray();

    internal static string? ComputeModelSha256(string modelPath)
    {
        try
        {
            return File.Exists(modelPath) ? EpContextArtifact.ComputeSha256(modelPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the model has no external-weights sibling, or the sibling exists and was
    /// hashed into the graph identity. Only then is the model content fully identified.
    /// </summary>
    internal static bool TryVerifyExternalDataIdentity(
        string sourceModelPath,
        out string? externalDataSha256,
        out string? unavailableReason)
    {
        externalDataSha256 = null;
        unavailableReason = null;
        string externalDataPath = EpContextArtifact.GetSourceExternalDataPath(sourceModelPath);
        if (!File.Exists(externalDataPath))
        {
            return true;
        }

        try
        {
            externalDataSha256 = EpContextArtifact.ComputeSha256(externalDataPath);
            return !string.IsNullOrWhiteSpace(externalDataSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unavailableReason = $"external-weights-hash:{ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// Versioned SHA-256 comparison key over the identities that make two runs comparable.
    /// Null when any required identity is missing; callers surface the reason as a warning
    /// instead of silently emitting a weak key.
    /// </summary>
    internal static string? BuildComparisonKey(
        BenchmarkConfigurationInfo configuration,
        IReadOnlyList<BenchmarkGraphMetadata> graphs,
        BenchmarkEnvironmentInfo environment,
        ICollection<string> incompleteReasons)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration.SelectedProvider))
        {
            missing.Add("selected-provider");
        }

        if (graphs.Count == 0 || graphs.Any(graph => string.IsNullOrWhiteSpace(graph.SourceModelSha256)))
        {
            missing.Add("model-sha256");
        }

        if (graphs.Any(graph => !graph.ExternalDataIdentityVerified))
        {
            missing.Add("external-weights-identity");
        }

        bool gpuBound = graphs.Any(graph => graph.SelectedDeviceIdentity is not null
            && graph.SelectedDeviceIdentity.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        if (gpuBound)
        {
            if (string.IsNullOrWhiteSpace(environment.GpuArchitecture)
                || environment.GpuArchitecture.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                missing.Add("gpu-architecture");
            }

            if (string.IsNullOrWhiteSpace(environment.DriverVersion))
            {
                missing.Add("gpu-driver");
            }

            if (graphs.Any(graph => graph.SelectedDeviceIdentity is null))
            {
                missing.Add("selected-device-identity");
            }
        }

        if (missing.Count > 0)
        {
            incompleteReasons.Add(
                $"comparison qualification incomplete: missing {string.Join(", ", missing.Distinct())}.");
            return null;
        }

        var payload = new StringBuilder();
        payload.Append('v').Append(ComparisonKeyVersion).Append('|')
            .Append(configuration.SelectedProvider).Append('|')
            .Append(configuration.TimingMode).Append('|')
            .Append(configuration.InputProfileId).Append('|')
            .Append(configuration.Execution.InputSeed).Append('|')
            .Append(environment.GpuArchitecture).Append('|')
            .Append(environment.DriverVersion).Append('|')
            .Append(environment.ConfiguredPluginFingerprint).Append('|')
            .Append(environment.HostOrtRuntimeVersion);

        if (configuration.Execution.InputShapeOverrides is { Count: > 0 } shapeOverrides)
        {
            foreach ((string name, int[] dimensions) in shapeOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                payload.Append("|shape:").Append(name).Append('=').Append(string.Join('x', dimensions));
            }
        }

        foreach (BenchmarkGraphMetadata graph in graphs)
        {
            payload.Append("|graph:")
                .Append(graph.Role).Append(',')
                .Append(graph.SourceModelSha256).Append(',')
                .Append(graph.ExternalDataSha256 ?? "none").Append(',')
                .Append(graph.LoadKind).Append(',')
                .Append(graph.CompileOptionsIdentity).Append(',')
                .Append(graph.EnableCudaGraph);
            foreach ((string key, string value) in graph.EffectiveProviderOptions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                payload.Append(';').Append(key).Append('=').Append(value);
            }
        }

        return $"sha256-v{ComparisonKeyVersion}:{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()))).ToLowerInvariant()}";
    }
}
