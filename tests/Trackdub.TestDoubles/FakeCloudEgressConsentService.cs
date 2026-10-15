using Trackdub.Application.Cloud;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;

namespace Trackdub.TestDoubles;

public sealed class FakeCloudEgressConsentService : ICloudEgressConsentService
{
    private readonly Dictionary<string, bool> _consents = new(StringComparer.OrdinalIgnoreCase);
    private readonly CloudEgressConsentService _innerCatalogHelper = new(new FakeStudioSettingsService());
    private readonly bool _defaultConsented;

    public FakeCloudEgressConsentService(bool defaultConsented = false, params string[] initialConsentedKeys)
    {
        _defaultConsented = defaultConsented;
        foreach (string key in initialConsentedKeys.Where(static key => !string.IsNullOrWhiteSpace(key)))
        {
            _consents[key.Trim().ToLowerInvariant()] = true;
        }
    }

    public Task<bool> HasConsentAsync(string consentKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consentKey))
        {
            return Task.FromResult(false);
        }

        if (_consents.TryGetValue(consentKey.Trim().ToLowerInvariant(), out bool consented))
        {
            return Task.FromResult(consented);
        }

        return Task.FromResult(_defaultConsented);
    }

    public IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias) =>
        _innerCatalogHelper.GetRequiredConsentKeys(stageName, modelAlias);

    public Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consentKey);
        _consents[consentKey.Trim().ToLowerInvariant()] = consented;
        return Task.CompletedTask;
    }

    public void Grant(params string[] keys)
    {
        foreach (string key in keys)
        {
            _consents[key.Trim().ToLowerInvariant()] = true;
        }
    }

    public void Revoke(params string[] keys)
    {
        foreach (string key in keys)
        {
            _consents[key.Trim().ToLowerInvariant()] = false;
        }
    }
}
