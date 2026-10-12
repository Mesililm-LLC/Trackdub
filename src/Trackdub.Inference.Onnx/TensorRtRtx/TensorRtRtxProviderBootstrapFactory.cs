using Trackdub.Contracts.ApplicationContracts;
#if LINML
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
#endif
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Builds <see cref="ITensorRtRtxProviderBootstrap"/> instances with the same wiring shape as the app composition root.
/// </summary>
public static class TensorRtRtxProviderBootstrapFactory
{
    /// <summary>
    /// Process-wide default for callers that have no settings service. On Linux this is the LinML
    /// catalog bootstrap; elsewhere it is <see cref="TensorRtRtxPluginService.Shared"/>.
    /// </summary>
    internal static ITensorRtRtxProviderBootstrap Shared { get; } = CreateShared();

    private static ITensorRtRtxProviderBootstrap CreateShared()
    {
#if LINML
        if (OperatingSystem.IsLinux())
        {
            return LinMlTensorRtRtxBootstrap.Create(
                static _ => ValueTask.FromResult<string?>(null),
                static _ => ValueTask.FromResult(false));
        }
#endif
        return TensorRtRtxPluginService.Shared;
    }

    /// <param name="isLicenseAccepted">
    /// Linux only: whether the NVIDIA TensorRT RTX license has been accepted, which gates the LinML
    /// catalog's plugin download. Omitted means not accepted, so the bootstrap never downloads.
    /// </param>
    public static ITensorRtRtxProviderBootstrap Create(
        Func<CancellationToken, ValueTask<string?>> explicitPluginDirectoryProvider,
        Func<CancellationToken, ValueTask<string?>> defaultInstallDirectoryProvider,
        Func<bool, CancellationToken, ValueTask<TensorRtRtxBundleEnsureResult>>? bundleEnsureAsync = null,
        Func<CancellationToken, ValueTask<bool>>? isLicenseAccepted = null)
    {
        ArgumentNullException.ThrowIfNull(explicitPluginDirectoryProvider);
        ArgumentNullException.ThrowIfNull(defaultInstallDirectoryProvider);

#if LINML
        if (OperatingSystem.IsLinux())
        {
            return LinMlTensorRtRtxBootstrap.Create(
                explicitPluginDirectoryProvider,
                isLicenseAccepted ?? (static _ => ValueTask.FromResult(false)));
        }
#endif

        return new TensorRtRtxPluginService(
            explicitPluginDirectoryProvider,
            defaultInstallDirectoryProvider,
            bundleEnsureAsync);
    }

    /// <summary>
    /// Creates a bootstrap instance that resolves the plugin from the default installed-bundle
    /// path only. The explicit <c>StudioSettings.TensorRtRtxPluginDirectory</c> setting is not
    /// available in this layer (no Infrastructure dependency); use the full
    /// <see cref="Create"/> overload when a settings service is available (e.g. CompositionRoot).
    /// Bundle downloads are never triggered; pass <c>userDataRoot</c> computed from
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> + "Trackdub".
    /// </summary>
    public static ITensorRtRtxProviderBootstrap CreateWithDefaultInstallPath(string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);

        return Create(
            static _ => ValueTask.FromResult<string?>(null),
            cancellationToken =>
            {
                _ = cancellationToken;
                string? rid = OperatingSystem.IsWindows() ? "win-x64"
                    : OperatingSystem.IsLinux() ? "linux-x64"
                    : null;
                if (rid is null)
                {
                    return ValueTask.FromResult<string?>(null);
                }

                string installDirectory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
                    userDataRoot, rid);
                return ValueTask.FromResult<string?>(installDirectory);
            },
            bundleEnsureAsync: null);
    }
}
