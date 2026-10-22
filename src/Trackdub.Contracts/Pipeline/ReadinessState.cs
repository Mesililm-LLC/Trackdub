namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Distinct readiness states for a single pipeline stage.
/// Maps 1:1 to the never-fake-readiness invariant states.
/// Blocking states prevent Run; non-blocking states allow it.
/// </summary>
public enum ReadinessState
{
    // ── Blocking states ────────────────────────────────────────────────────────

    /// <summary>No registered provider can serve this stage with the current selections.</summary>
    ProviderMissing = 0,

    /// <summary>Provider is registered but required runtime/EP package is not installed.</summary>
    RuntimeMissing = 1,

    /// <summary>Model files are absent; provider can auto-download from HF Hub.</summary>
    DownloadRequired = 2,

    /// <summary>Model files are absent and cannot be auto-downloaded; user must import manually.</summary>
    ImportRequired = 3,

    /// <summary>Model files are present but checksum verification failed.</summary>
    IntegrityFailed = 4,

    /// <summary>Model requires license review before use.</summary>
    LicenseReviewRequired = 5,

    /// <summary>Selected model is non-commercial-only and commercial mode is active.</summary>
    CommercialBlocked = 6,

    /// <summary>A cloud engine alias is selected but the required API key is not configured.</summary>
    CloudKeyMissing = 7,

    /// <summary>Voice-clone TTS requires session consent that has not yet been granted.</summary>
    ConsentRequired = 8,

    /// <summary>A cloud engine would egress data to an external provider without recorded user consent.</summary>
    CloudEgressConsentRequired = 9,

    /// <summary>The selected speech model does not support the requested dubbed language.</summary>
    UnsupportedLanguage = 10,

    // ── Non-blocking states ────────────────────────────────────────────────────

    /// <summary>Stage is fully ready to execute.</summary>
    Ready = 100,

    /// <summary>Stage has valid existing artifacts from a prior run and will be skipped (resume).</summary>
    Satisfied = 101,

    /// <summary>Stage is optional (e.g. Separation) and the user has opted to skip it.</summary>
    SkippableOptional = 102,

    /// <summary>
    /// Model metadata checks passed but the runtime/provider path was not verified
    /// (the evaluation skipped provider smoke testing). Non-blocking: the stage may
    /// still fail at execution time. Stages in this state are included in
    /// <see cref="PipelineReadinessReport"/>.
    /// </summary>
    Unverified = 103,

    /// <summary>Translation pair selection and provisioning will be checked after source resolution.</summary>
    PendingTranslationResolution = 104,
}

public static class ReadinessStateExtensions
{
    /// <summary>Returns true when the state blocks a pipeline run from starting.</summary>
    public static bool IsBlocking(this ReadinessState state) =>
        state is ReadinessState.ProviderMissing
            or ReadinessState.RuntimeMissing
            or ReadinessState.DownloadRequired
            or ReadinessState.ImportRequired
            or ReadinessState.IntegrityFailed
            or ReadinessState.LicenseReviewRequired
            or ReadinessState.CommercialBlocked
            or ReadinessState.CloudKeyMissing
            or ReadinessState.ConsentRequired
            or ReadinessState.CloudEgressConsentRequired
            or ReadinessState.UnsupportedLanguage;
}
