namespace Trackdub.Contracts.Cloud;

/// <summary>
/// Queries and persists per-(egressType, provider) user consent for cloud data egress.
/// </summary>
public interface ICloudEgressConsentService
{
    /// <summary>
    /// Returns true if the user has previously consented to this egress key.
    /// Pure read — no network calls.
    /// </summary>
    Task<bool> HasConsentAsync(string consentKey, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of consent keys required for the given stage and model alias selection.
    /// </summary>
    IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias);

    /// <summary>
    /// Persists consent (or revocation) for a single key.
    /// </summary>
    Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct = default);
}
