namespace Trackdub.Contracts.Serving;

/// <summary>
/// Process-level efficiency controls for host processes (serve / inference worker). The
/// implementation lives in the inference runtime; callers that cannot reference the runtime
/// directly (the CLI) resolve it through the session-factory service provider.
/// </summary>
public interface IProcessEfficiencyControl
{
    /// <summary>
    /// Opts the current process into Windows EcoQoS power throttling (efficiency-oriented CPU
    /// scheduling). Returns <c>(false, reason)</c> when the platform/runtime does not support it
    /// or the call fails; a success is backed by a verified read-back on Windows.
    /// </summary>
    (bool Applied, string? Error) TryEnableEcoQos();

    /// <summary>
    /// Verified current state: execution-speed throttling is active for this process. Never
    /// assumed — read back from the OS where supported; <see langword="false"/> otherwise.
    /// </summary>
    bool IsEcoQosEnabled { get; }
}
