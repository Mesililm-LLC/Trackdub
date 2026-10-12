using Trackdub.Application.Runtime;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;
using Trackdub.Infrastructure.Settings;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Benchmarks;

internal static class BenchmarkTensorRtRtxBootstrap
{
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static ITensorRtRtxProviderBootstrap Create()
    {
        var storagePaths = new TrackdubStoragePaths();
        var settingsService = new JsonStudioSettingsService(storagePaths);
        var bundleInstaller = CreateBundleInstaller(storagePaths, settingsService);

        return TensorRtRtxProviderBootstrapFactory.Create(
            async cancellationToken =>
            {
                StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
                return settings.TensorRtRtxPluginDirectory;
            },
            cancellationToken =>
            {
                _ = cancellationToken;
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                {
                    return ValueTask.FromResult<string?>(null);
                }

                string runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
                string installDirectory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
                    storagePaths.UserDataRoot,
                    runtimeIdentifier);
                return ValueTask.FromResult<string?>(installDirectory);
            },
            async (allowProviderDownloads, cancellationToken) =>
            {
                if (!allowProviderDownloads)
                {
                    return new TensorRtRtxBundleEnsureResult(
                        false,
                        null,
                        "Provider downloads are disabled for this probe.");
                }

                StudioSettings bundleSettings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!bundleSettings.NvidiaTensorRtRtxLicenseAccepted)
                {
                    return new TensorRtRtxBundleEnsureResult(
                        false,
                        null,
                        "NVIDIA TensorRT RTX license not accepted. Accept the license in Model Manager or run "
                        + "'trackdub providers trt-rtx install --accept-license' before benchmark auto-download.");
                }

                TrtRtxEpBundleInstallResult result = await bundleInstaller
                    .EnsureBundleAsync(new Progress<string>(_ => { }), cancellationToken)
                    .ConfigureAwait(false);
                return new TensorRtRtxBundleEnsureResult(
                    result.Succeeded,
                    result.InstallDirectory,
                    result.FailureDetail);
            },
            isLicenseAccepted: async cancellationToken =>
                (await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false))
                    .NvidiaTensorRtRtxLicenseAccepted);
    }

    private static TrtRtxEpBundleInstaller CreateBundleInstaller(
        TrackdubStoragePaths storagePaths,
        IStudioSettingsService settingsService)
    {
        var logger = new DebugApplicationLogger();
        var downloader = new TrtRtxEpBundleDownloader(s_httpClient, logger);
        return new TrtRtxEpBundleInstaller(storagePaths, downloader, settingsService, logger);
    }
}
