namespace Trackdub.Domain.StageRuns;

/// <summary>
/// Canonical stage skip reason codes emitted by the dubbing pipeline and SDK.
/// </summary>
public static class StageSkipReasonCodes
{
    public const string ExistingArtifactsValid = "EXISTING_ARTIFACTS_VALID";

    public const string PrerequisiteFailed = "PREREQUISITE_FAILED";

    public const string NoTranscriptSegments = "NO_TRANSCRIPT_SEGMENTS";

    public const string NoSpeechRegions = "NO_SPEECH_REGIONS";

    /// <summary>Stage was explicitly disabled by a run option or host flag.</summary>
    public const string DisabledByOption = "DISABLED_BY_OPTION";

    /// <summary>User declined the stage's optional model during pre-flight provisioning.</summary>
    public const string OptionalModelDeclined = "OPTIONAL_MODEL_DECLINED";

    /// <summary>
    /// The stage's proving warmup (the single first-run smoke create) failed, so the
    /// stage was skipped before inference instead of attempting a doomed execution.
    /// Not benign: no verdict was recorded and the stage produced nothing.
    /// </summary>
    public const string WarmupFailed = "WARMUP_FAILED";

    private static readonly HashSet<string> BenignSkipReasonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ExistingArtifactsValid,
        PrerequisiteFailed,
        NoTranscriptSegments,
        NoSpeechRegions,
        DisabledByOption,
        OptionalModelDeclined,
    };

    /// <summary>
    /// Skip reason codes that represent intentional resume/prerequisite gating rather than
    /// a failed attempt to run a requested stage.
    /// </summary>
    public static bool IsBenignSkip(string? reasonCode) =>
        reasonCode is not null && BenignSkipReasonCodes.Contains(reasonCode);
}
