using Trackdub.Application.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Downloads (when needed), persists, and registers the TensorRT RTX standalone ORT EP ABI plugin.
/// On Linux the plugin comes from the LinML catalog, so there is no separate bundle step.
/// </summary>
internal sealed class TrtRtxEpInstaller(
    ITrtRtxEpBundleInstaller bundleInstaller,
    ITensorRtRtxProviderBootstrap pluginBootstrap) : ITrtRtxEpInstaller
{
    private readonly ITrtRtxEpBundleInstaller bundleInstaller =
        bundleInstaller ?? throw new ArgumentNullException(nameof(bundleInstaller));
    private readonly ITensorRtRtxProviderBootstrap pluginBootstrap =
        pluginBootstrap ?? throw new ArgumentNullException(nameof(pluginBootstrap));
    private readonly bool useCatalogRoute = OperatingSystem.IsLinux();

    internal TrtRtxEpInstaller(
        ITrtRtxEpBundleInstaller bundleInstaller,
        ITensorRtRtxProviderBootstrap pluginBootstrap,
        bool useCatalogRoute)
        : this(bundleInstaller, pluginBootstrap)
    {
        this.useCatalogRoute = useCatalogRoute;
    }

    public async Task<TrtRtxEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!useCatalogRoute)
        {
            progress.Report("Checking TensorRT RTX EP ABI plugin bundle...");
            TrtRtxEpBundleInstallResult bundleResult = await bundleInstaller
                .EnsureBundleAsync(progress, cancellationToken)
                .ConfigureAwait(false);

            if (!bundleResult.Succeeded)
            {
                string failureDetail = string.IsNullOrWhiteSpace(bundleResult.FailureDetail)
                    ? "TensorRT RTX EP ABI bundle install did not complete."
                    : bundleResult.FailureDetail;
                progress.Report(failureDetail);
                return new TrtRtxEpInstallResult(Succeeded: false, FailureDetail: failureDetail);
            }
        }

        progress.Report(useCatalogRoute
            ? "Preparing TensorRT RTX EP ABI plugin through the LinML catalog..."
            : "Registering TensorRT RTX EP ABI plugin...");
        TensorRtRtxBootstrapResult bootstrap = await pluginBootstrap
            .EnsureRegisteredAsync(allowProviderDownloads: useCatalogRoute, cancellationToken)
            .ConfigureAwait(false);

        if (bootstrap.Succeeded)
        {
            progress.Report("TensorRT RTX EP ABI plugin registered successfully.");
            return new TrtRtxEpInstallResult(Succeeded: true);
        }

        string registerFailure = string.IsNullOrWhiteSpace(bootstrap.Detail)
            ? "TensorRT RTX EP ABI plugin registration did not complete. TRT RTX will not be available this session."
            : bootstrap.Detail;

        progress.Report($"TensorRT RTX EP ABI plugin registration failed: {registerFailure}");
        return new TrtRtxEpInstallResult(Succeeded: false, FailureDetail: registerFailure);
    }
}
