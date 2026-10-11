using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary><see cref="ILinuxOpenVinoCatalog"/> backed by <see cref="ExecutionProviderCatalog"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinMlOpenVinoCatalog : ILinuxOpenVinoCatalog
{
    // Same bounds as the Windows ML catalog bootstrap: registration of an installed plugin is
    // quick; a first-time download of the plugin archive is not.
    internal static readonly TimeSpan RegisterOnlyTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    // NPU first, matching WinMlCatalogSessionOptionsExtensions.TryAppendOpenVinoCatalogProvider.
    private static readonly OrtHardwareDeviceType[] PreferredDeviceTypes =
        [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU, OrtHardwareDeviceType.CPU];

    public static LinMlOpenVinoCatalog Default { get; } = new(ExecutionProviderCatalog.GetDefault());

    private readonly IExecutionProviderCatalog _catalog;

    public LinMlOpenVinoCatalog(IExecutionProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public bool IsInstalled =>
        _catalog.Find(ProviderId.OpenVino)?.ReadyState is ProviderReadyState.Ready or ProviderReadyState.NotReady;

    public async Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        IExecutionProvider? provider = _catalog.Find(ProviderId.OpenVino);
        if (provider is null)
        {
            return new(false, "The LinML catalog has no OpenVINO provider.");
        }

        TimeSpan timeout = allowDownloads ? DownloadTimeout : RegisterOnlyTimeout;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            ProviderEnsureResult ensured = await provider
                .EnsureReadyAsync(new EnsureOptions(allowDownloads), bounded.Token)
                .ConfigureAwait(false);
            return new(ensured.IsReady, $"OpenVINO plugin EP ({provider.PinnedVersion}): {ensured.Status}. {ensured.Detail}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, $"OpenVINO plugin EP preparation timed out after {timeout.TotalSeconds:0} s.");
        }
    }

    public bool TryAppend(SessionOptions options, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(options);

        IExecutionProvider? provider = _catalog.Find(ProviderId.OpenVino);
        if (provider is null)
        {
            failureReason = "The LinML catalog has no OpenVINO provider.";
            return false;
        }

        ProviderAppendResult appended = options.AppendFromCatalog(provider, PreferredDeviceTypes);
        failureReason = appended.FailureReason;
        return appended.Appended;
    }
}
