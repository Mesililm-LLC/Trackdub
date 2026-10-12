using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>Prepare and append one plugin EP from the LinML catalog.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinMlPluginCatalog
{
    // Same bounds as the Windows ML catalog bootstrap: registration of an installed plugin is
    // quick; a first-time download of the plugin archive is not.
    internal static readonly TimeSpan RegisterOnlyTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private readonly IExecutionProviderCatalog _catalog;
    private readonly ProviderId _providerId;
    private readonly string _label;

    public LinMlPluginCatalog(IExecutionProviderCatalog catalog, ProviderId providerId, string label)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _providerId = providerId;
        _label = label;
    }

    public async Task<LinuxPluginCatalogResult> EnsureReadyAsync(
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        IExecutionProvider? provider = _catalog.Find(_providerId);
        if (provider is null)
        {
            return new(LinuxPluginCatalogStatus.Unsupported, $"The LinML catalog has no {_label} provider.");
        }

        TimeSpan timeout = allowDownloads ? DownloadTimeout : RegisterOnlyTimeout;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            ProviderEnsureResult ensured = await provider
                .EnsureReadyAsync(new EnsureOptions(allowDownloads), bounded.Token)
                .ConfigureAwait(false);
            return new(MapStatus(ensured.Status), $"{_label} plugin EP ({provider.PinnedVersion}): {ensured.Status}. {ensured.Detail}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LinuxPluginCatalogStatus.Failed, $"{_label} plugin EP preparation timed out after {timeout.TotalSeconds:0} s.");
        }
    }

    internal static LinuxPluginCatalogStatus MapStatus(ProviderEnsureStatus status) =>
        status switch
        {
            ProviderEnsureStatus.Ready or ProviderEnsureStatus.Downloaded => LinuxPluginCatalogStatus.Ready,
            ProviderEnsureStatus.DownloadDisallowed => LinuxPluginCatalogStatus.NotInstalled,
            ProviderEnsureStatus.NoDevices => LinuxPluginCatalogStatus.NoDevices,
            ProviderEnsureStatus.AbiIncompatible or ProviderEnsureStatus.NotAvailable => LinuxPluginCatalogStatus.Unsupported,
            _ => LinuxPluginCatalogStatus.Failed,
        };

    public bool TryAppend(
        SessionOptions options,
        IReadOnlyList<OrtHardwareDeviceType> preferredDeviceTypes,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(options);

        IExecutionProvider? provider = _catalog.Find(_providerId);
        if (provider is null)
        {
            failureReason = $"The LinML catalog has no {_label} provider.";
            return false;
        }

        // AppendFromCatalog maps append failures to a result, but reads the plugin's devices first;
        // catch the same native failures as WinMlCatalogSessionOptionsExtensions so the CPU
        // fallback still applies.
        try
        {
            ProviderAppendResult appended = options.AppendFromCatalog(provider, preferredDeviceTypes);
            failureReason = appended.FailureReason;
            return appended.Appended;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }
}
