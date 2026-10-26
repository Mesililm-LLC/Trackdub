using System.Diagnostics;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Cached machine fingerprint + optional valid EP-context load path for session creation.
/// The hardware identity is populated asynchronously during host startup / EP-context warmup
/// (<see cref="EpContextWarmupService.InitializeHardwareIdentityAsync"/>); this type never probes
/// or blocks on the session-creation path. Until the identity is known, lookups are honest
/// cache misses and no stamp may be written from the placeholder identity.
/// </summary>
internal static class EpContextLoadPathResolver
{
    private sealed record HardwareIdentity(string GpuArchitecture, string? DriverVersion);

    private static HardwareIdentity hardware = new("unknown", null);

    /// <summary>
    /// True only once a real GPU architecture, driver, and host ORT version are known.
    /// The placeholder ("unknown") identity must never resolve or stamp an artifact.
    /// </summary>
    internal static bool HasIdentifiedHardware
    {
        get
        {
            (string gpuArchitecture, string? driverVersion) = CurrentHardware;
            return !IsUnknown(gpuArchitecture) && !IsUnknown(driverVersion) &&
                !IsUnknown(EpContextArtifact.HostOrtRuntimeVersion);
        }
    }

    private static bool IsUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase);

    public static string CurrentEnvironmentFingerprint =>
        $"{CurrentHardware.GpuArchitecture}|{(string.IsNullOrWhiteSpace(CurrentHardware.DriverVersion) ? "unknown" : CurrentHardware.DriverVersion)}|{TensorRtRtxProviderConstants.BundledFingerprintVersion}|{EpContextArtifact.HostOrtRuntimeVersion}";

    /// <summary>GPU architecture and driver the fingerprint (and new stamps) are built from.</summary>
    public static (string GpuArchitecture, string? DriverVersion) CurrentHardware
    {
        get
        {
            HardwareIdentity current = Volatile.Read(ref hardware);
            return (current.GpuArchitecture, current.DriverVersion);
        }
    }

    internal static void SetCurrentHardware(HardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Volatile.Write(ref hardware, new HardwareIdentity(profile.NvidiaGpuArchitecture.ToString(), profile.GpuDriverVersion));
    }

    internal static void ResetCurrentHardwareForTesting() =>
        Volatile.Write(ref hardware, new HardwareIdentity("unknown", null));

    /// <summary>
    /// Valid EP-context sibling for <paramref name="sourceModelPath"/>, or <see langword="null"/>.
    /// Stamp identity (including the TensorRT RTX options the session appends) and
    /// selected-device compatibility both have to pass.
    /// </summary>
    public static string? TryResolveLoadPath(
        string sourceModelPath,
        IReadOnlyDictionary<string, string> effectiveTrtOptions)
    {
        // Pre-warmup (hardware identity not yet loaded): an honest miss. Comparing against
        // the placeholder "unknown" fingerprint could never match a valid stamp — stamps
        // require identified hardware — and must never be treated as a real environment.
        if (!HasIdentifiedHardware)
        {
            return null;
        }

        string compileOptionsIdentity = BuildCompileOptionsIdentity(sourceModelPath, effectiveTrtOptions);
        string? compiledPath = EpContextArtifact.TryResolveValidLoadPath(
            sourceModelPath,
            CurrentEnvironmentFingerprint,
            compileOptionsIdentity);
        if (compiledPath is null)
        {
            return null;
        }

        return EpContextCompatibility.AllowsTensorRtRtxArtifact(compiledPath) ? compiledPath : null;
    }

    /// <summary>Identity of the options an engine for <paramref name="sourceModelPath"/> is built with.</summary>
    public static string BuildCompileOptionsIdentity(
        string sourceModelPath,
        IReadOnlyDictionary<string, string> effectiveTrtOptions) =>
        EpContextArtifact.BuildCompileOptionsIdentity(
            effectiveTrtOptions,
            EpContextArtifact.ShouldEmbedEpContext(sourceModelPath));
}
