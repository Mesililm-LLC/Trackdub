using Trackdub.Domain;

namespace Trackdub.Contracts;

/// <summary>
/// Identity of a model/provider/environment tuple whose provider smoke test has been proven.
/// Any component change yields a different key, so a stale verdict can never be served after a
/// model re-download, GPU swap, driver update, or TRT RTX EP bump — the same triggers that make
/// compiled engine cache entries unusable.
/// </summary>
public sealed record SmokeVerdictKey(
    string ModelSha256,
    ExecutionProviderKind ExecutionProvider,
    NvidiaGpuArchitectureBucket GpuArchitecture,
    string? DriverVersion,
    string? TrtRtxEpVersion,
    string? OrtRuntimeVersion = null)
{
    /// <summary>GPU arch + driver + TRT RTX EP version + ORT runtime version; shared by every verdict on one machine state.</summary>
    public string EnvironmentFingerprint =>
        $"{GpuArchitecture}|{Normalize(DriverVersion)}|{Normalize(TrtRtxEpVersion)}|{Normalize(OrtRuntimeVersion)}";

    /// <summary>Model identity + execution provider.</summary>
    public string EntryId =>
        $"{Normalize(ModelSha256).ToLowerInvariant()}|{ExecutionProvider}";

    public string ToStableString() => $"{EntryId}|{EnvironmentFingerprint}";

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
}

/// <summary>
/// Persists provider smoke-test verdicts across processes so a model/provider pair proven on a
/// previous launch can skip the expensive compile-and-run smoke on the next one. Cheap file and
/// manifest checks still gate plan creation before any verdict is consulted.
/// </summary>
public interface ISmokeVerdictStore
{
    bool IsVerified(SmokeVerdictKey key);

    void RecordVerified(SmokeVerdictKey key);

    void Clear();
}

/// <summary>No-op store: every plan re-runs smoke. Default when no persistence is wired.</summary>
public sealed class NullSmokeVerdictStore : ISmokeVerdictStore
{
    public static NullSmokeVerdictStore Instance { get; } = new();

    public bool IsVerified(SmokeVerdictKey key) => false;

    public void RecordVerified(SmokeVerdictKey key)
    {
    }

    public void Clear()
    {
    }
}
