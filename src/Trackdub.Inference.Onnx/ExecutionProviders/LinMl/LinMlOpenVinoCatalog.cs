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
    private static readonly OrtHardwareDeviceType[] AcceleratorFirst =
        [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU, OrtHardwareDeviceType.CPU];

    private static readonly OrtHardwareDeviceType[] CpuOnly = [OrtHardwareDeviceType.CPU];

    internal static IReadOnlyList<OrtHardwareDeviceType> PreferredDeviceTypes(bool cpuOnly) =>
        cpuOnly ? CpuOnly : AcceleratorFirst;

    public static LinMlOpenVinoCatalog Default { get; } = new(ExecutionProviderCatalog.GetDefault());

    private readonly IExecutionProviderCatalog _catalog;

    public LinMlOpenVinoCatalog(IExecutionProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        IExecutionProvider? provider = _catalog.Find(ProviderId.OpenVino);
        if (provider is null)
        {
            return new(LinuxOpenVinoCatalogStatus.Unsupported, "The LinML catalog has no OpenVINO provider.");
        }

        TimeSpan timeout = allowDownloads ? DownloadTimeout : RegisterOnlyTimeout;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            ProviderEnsureResult ensured = await provider
                .EnsureReadyAsync(new EnsureOptions(allowDownloads), bounded.Token)
                .ConfigureAwait(false);
            return new(MapStatus(ensured.Status), $"OpenVINO plugin EP ({provider.PinnedVersion}): {ensured.Status}. {ensured.Detail}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LinuxOpenVinoCatalogStatus.Failed, $"OpenVINO plugin EP preparation timed out after {timeout.TotalSeconds:0} s.");
        }
    }

    internal static LinuxOpenVinoCatalogStatus MapStatus(ProviderEnsureStatus status) =>
        status switch
        {
            ProviderEnsureStatus.Ready or ProviderEnsureStatus.Downloaded => LinuxOpenVinoCatalogStatus.Ready,
            ProviderEnsureStatus.DownloadDisallowed => LinuxOpenVinoCatalogStatus.NotInstalled,
            ProviderEnsureStatus.NoDevices => LinuxOpenVinoCatalogStatus.NoDevices,
            ProviderEnsureStatus.AbiIncompatible or ProviderEnsureStatus.NotAvailable => LinuxOpenVinoCatalogStatus.Unsupported,
            _ => LinuxOpenVinoCatalogStatus.Failed,
        };

    public bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(options);

        IExecutionProvider? provider = _catalog.Find(ProviderId.OpenVino);
        if (provider is null)
        {
            failureReason = "The LinML catalog has no OpenVINO provider.";
            return false;
        }

        // AppendFromCatalog maps append failures to a result, but reads the plugin's devices first;
        // catch the same native failures as WinMlCatalogSessionOptionsExtensions so the CPU
        // fallback still applies.
        try
        {
            ProviderAppendResult appended = options.AppendFromCatalog(provider, PreferredDeviceTypes(cpuOnly));
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
