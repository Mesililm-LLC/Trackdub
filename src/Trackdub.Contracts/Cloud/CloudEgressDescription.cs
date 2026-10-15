namespace Trackdub.Contracts.Cloud;

/// <summary>
/// Describes a specific cloud egress route for disclosure in consent prompts and settings.
/// </summary>
public sealed record CloudEgressDescription(
    string ConsentKey,
    EgressType EgressType,
    string ProviderDisplayName,
    string StageDisplayName,
    string DataDescription,
    string SentTo,
    string? PrivacyPolicyUrl)
{
    public CloudEgressDescription(
        string consentKey,
        EgressType egressType,
        string providerName,
        string dataDescription,
        string endpoint,
        string? privacyPolicyUrl)
        : this(
            consentKey,
            egressType,
            providerName,
            StageDisplayName: string.Empty,
            dataDescription,
            endpoint,
            privacyPolicyUrl)
    {
    }

    /// <summary>Alias for <see cref="ProviderDisplayName"/>.</summary>
    public string ProviderName => ProviderDisplayName;

    /// <summary>Alias for <see cref="SentTo"/>.</summary>
    public string Endpoint => SentTo;
}
