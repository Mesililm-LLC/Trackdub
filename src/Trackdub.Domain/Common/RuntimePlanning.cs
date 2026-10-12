namespace Trackdub.Domain;

public enum RuntimeStage
{
    Vad = 1,
    Asr = 2,
    Translation = 3,
    Tts = 4,
    Diarization = 5,
    Separation = 6,
    SpeechEnhancement = 7,
    LipSync = 8,
    TextRefinement = 9,
    OverlapRescue = 10,
    LipSynthesis = 11
}

public enum ExecutionProviderKind
{
    Cpu = 1,
    DirectMl = 2,
    TensorRTRtx = 3,    // Windows TensorRT RTX standalone EP ABI plugin
    OpenVino = 4,
    CoreMl = 5,          // macOS CoreML + ANE — no download required
    Cuda = 6,            // Linux NVIDIA CUDA (native); Windows: pin remaps to TensorRTRtx (native CUDA is advanced-only)
    TensorRt = 7,        // Linux native TensorRT (requires libnvinfer); Windows: promoted to TensorRTRtx
    Migraphx = 8,        // AMD MIGraphX: Windows via WinML catalog EP; Linux via LinML catalog plugin EP
    /// <summary>
    /// Qualcomm QNN via Windows ML catalog EP (Windows).
    /// </summary>
    Qnn = 9,
    /// <summary>
    /// Intel OpenVINO via Windows ML catalog EP (Windows). Distinct from standalone <see cref="OpenVino"/>.
    /// </summary>
    OpenVinoCatalog = 10,
    /// <summary>
    /// AMD VitisAI / Ryzen AI NPU via Windows ML catalog EP (Windows).
    /// </summary>
    VitisAi = 11,
    /// <summary>
    /// Intel oneDNN / DNNL ONNX Runtime CPU execution provider.
    /// </summary>
    Dnnl = 12
}

public enum StageRuntimePlanStatus
{
    /// <summary>
    /// All required model files are present and pass file-existence/integrity checks.
    /// CPU-only providers stop here because no smoke test is performed for CPU.
    /// </summary>
    Ready = 1,
    DownloadRequired = 2,
    Blocked = 3,
    /// <summary>
    /// Stronger than <see cref="Ready"/>: model files are present AND a runtime smoke test
    /// passed for the selected non-CPU execution provider. Consumers that ran inference
    /// during planning should report Verified so the UI can distinguish "loadable" from
    /// "actually executed."
    /// </summary>
    Verified = 4
}

public enum RuntimePlanFallbackCode
{
    ProviderUnavailable = 1,
    ProviderSmokeTestFailed = 2,
    ModelNotCached = 3,
    CommercialSafeExcluded = 4,
    NoCompatibleVariant = 5,
    UnsupportedLanguagePair = 6,
    ModelIntegrityMismatch = 7,
    NoDeviceAvailable = 8
}

public enum RuntimePlanWarningCode
{
    CpuFallback = 1,
    AttributionRequired = 2,
    UserConsentRequired = 3,
    CommercialSafeModeActive = 4,
    ModelIntegrityNotVerified = 5,
    DeviceFallback = 6,
    PreferredOptimizedVariantUnavailable = 7,
    ExpectedRuntimeMismatch = 8,
    /// <summary>
    /// Preferred execution provider was skipped because the selected engine family does not allow it
    /// (for example Kokoro TTS is CPU-only).
    /// </summary>
    PreferredExecutionProviderNotAllowedForEngine = 9
}

public enum RuntimeModelIntegrityStatus
{
    Unknown = 0,
    Verified = 1,
    Skipped = 2
}

public enum RuntimeRouteReadiness
{
    NotReady = 0,
    Ready = 1,
    Fallback = 2
}

public sealed record ProviderCapability
{
    public ExecutionProviderKind Provider { get; init; }
    public bool DeviceDetected { get; init; }
    public bool RuntimePackageInstalled { get; init; }
    public bool ProviderLoadable { get; init; }
    public bool ModelVariantCompatible { get; init; }
    public bool SmokeTestPassed { get; init; }
    public bool BenchmarkAvailable { get; init; }
    public string? BlockedReason { get; init; }
}

public sealed record RuntimeRoute
{
    public RuntimeStage Stage { get; init; }
    public ExecutionProviderKind SelectedProvider { get; init; }
    public int? DeviceId { get; init; }
    public string? DeviceTarget { get; init; }
    public string? ModelId { get; init; }
    public string? Variant { get; init; }
    public RuntimeRouteReadiness Readiness { get; init; }
    public string? FallbackReason { get; init; }
    public string? SmokeEvidenceId { get; init; }
    public string? BenchmarkEvidenceId { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
