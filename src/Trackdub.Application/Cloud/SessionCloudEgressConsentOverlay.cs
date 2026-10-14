using Trackdub.Contracts.Cloud;

namespace Trackdub.Application.Cloud;

/// <summary>
/// Run-scoped <see cref="ICloudEgressConsentService"/> that overlays the explicit
/// consents supplied for a single headless/CI run on top of the persisted consent
/// store. Explicit keys win for this run only — the shared, persisted consent state
/// is never mutated, so a later run that omits the option is still gated by what was
/// actually persisted.
/// </summary>
public sealed class SessionCloudEgressConsentOverlay(
    ICloudEgressConsentService inner,
    IReadOnlyDictionary<string, bool>? sessionConsents) : ICloudEgressConsentService
{
    private readonly ICloudEgressConsentService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IReadOnlyDictionary<string, bool>? _sessionConsents = sessionConsents;

    /// <inheritdoc />
    public async Task<bool> HasConsentAsync(string consentKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consentKey))
        {
            return false;
        }

        string normalizedKey = consentKey.Trim().ToLowerInvariant();
        if (_sessionConsents is not null && _sessionConsents.TryGetValue(normalizedKey, out bool consented))
        {
            return consented;
        }

        return await _inner.HasConsentAsync(consentKey, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias) =>
        _inner.GetRequiredConsentKeys(stageName, modelAlias);

    /// <inheritdoc />
    public Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct = default) =>
        _inner.SetConsentAsync(consentKey, consented, ct);
}
