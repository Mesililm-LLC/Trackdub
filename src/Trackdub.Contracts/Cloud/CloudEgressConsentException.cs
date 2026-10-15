namespace Trackdub.Contracts.Cloud;

/// <summary>
/// Thrown when a cloud inference or dubbing engine is invoked without prior user consent
/// for the required <see cref="ConsentKey"/>.
/// </summary>
public sealed class CloudEgressConsentException : InvalidOperationException
{
    public string? ConsentKey { get; }

    public string? ProviderName { get; }

    public CloudEgressConsentException()
        : base("Cloud egress has not been consented. Grant consent in Settings (desktop app) or set TRACKDUB_CLOUD_EGRESS_CONSENT(S) (headless/CLI) before running this stage.")
    {
    }

    public CloudEgressConsentException(string message)
        : base(message)
    {
    }

    public CloudEgressConsentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CloudEgressConsentException(string consentKey, string providerName)
        : base($"Cloud egress to {providerName} ({consentKey}) has not been consented. Grant consent in Settings (desktop app) or set TRACKDUB_CLOUD_EGRESS_CONSENT(S) (headless/CLI) before running this stage.")
    {
        ConsentKey = consentKey;
        ProviderName = providerName;
    }
}
