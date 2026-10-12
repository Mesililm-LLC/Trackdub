using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.ExecutionProviders.Linux;

/// <summary>
/// Linux-specific execution provider bootstrapper.
/// Supports CPU execution, optional CUDA/TensorRT for NVIDIA GPUs, and OpenVINO and MIGraphX through
/// the LinML catalog's plugin EPs.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxExecutionProviderBootstrapper : IExecutionProviderBootstrapper
{
    private readonly ILinuxOpenVinoCatalog _openVinoCatalog;
    private readonly ILinuxMigraphxCatalog _migraphxCatalog;
    private readonly ILinuxNativeGpuRuntimeProbe _linuxRuntimeProbe;
    private readonly ITensorRtRtxProviderBootstrap _tensorRtRtxPluginBootstrap;
    private readonly IDnnlReadinessProbe _dnnlReadinessProbe = new DnnlReadinessProbe();

    public LinuxExecutionProviderBootstrapper(IOpenVinoAvailabilityProvider openVino)
        : this(openVino, new LinuxNativeGpuRuntimeProbe(), CreateDefaultTrtRtxBootstrap())
    {
    }

    public LinuxExecutionProviderBootstrapper(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe)
        : this(openVino, linuxRuntimeProbe, CreateDefaultTrtRtxBootstrap())
    {
    }

    public LinuxExecutionProviderBootstrapper(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        ITensorRtRtxProviderBootstrap tensorRtRtxPluginBootstrap)
        : this(openVino, linuxRuntimeProbe, tensorRtRtxPluginBootstrap, DefaultOpenVinoCatalog)
    {
    }

    public LinuxExecutionProviderBootstrapper(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        ITensorRtRtxProviderBootstrap tensorRtRtxPluginBootstrap,
        ILinuxOpenVinoCatalog openVinoCatalog)
        : this(openVino, linuxRuntimeProbe, tensorRtRtxPluginBootstrap, openVinoCatalog, DefaultMigraphxCatalog)
    {
    }

    public LinuxExecutionProviderBootstrapper(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        ITensorRtRtxProviderBootstrap tensorRtRtxPluginBootstrap,
        ILinuxOpenVinoCatalog openVinoCatalog,
        ILinuxMigraphxCatalog migraphxCatalog)
    {
        ArgumentNullException.ThrowIfNull(openVino);
        _openVinoCatalog = openVinoCatalog ?? throw new ArgumentNullException(nameof(openVinoCatalog));
        _migraphxCatalog = migraphxCatalog ?? throw new ArgumentNullException(nameof(migraphxCatalog));
        _linuxRuntimeProbe = linuxRuntimeProbe ?? throw new ArgumentNullException(nameof(linuxRuntimeProbe));
        _tensorRtRtxPluginBootstrap = tensorRtRtxPluginBootstrap
            ?? throw new ArgumentNullException(nameof(tensorRtRtxPluginBootstrap));
    }

    private static ILinuxOpenVinoCatalog DefaultOpenVinoCatalog =>
#if LINML
        LinMlOpenVinoCatalog.Default;
#else
        UnavailableLinuxOpenVinoCatalog.Instance;
#endif

    private static ILinuxMigraphxCatalog DefaultMigraphxCatalog =>
#if LINML
        LinMlMigraphxCatalog.Default;
#else
        UnavailableLinuxMigraphxCatalog.Instance;
#endif

    /// <summary>
    /// Builds a real-provider TRT-RTX bootstrap without Infrastructure or Application dependencies.
    /// Resolves the default installed-bundle path; explicit StudioSettings directory is only
    /// available via the DI-wired <see cref="CompositionRoot"/> path.
    /// </summary>
    private static ITensorRtRtxProviderBootstrap CreateDefaultTrtRtxBootstrap()
    {
        string userDataRoot = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub");
        return TensorRtRtxProviderBootstrapFactory.CreateWithDefaultInstallPath(userDataRoot);
    }

    public async Task<ExecutionProviderBootstrapResult> BootstrapAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (provider == ExecutionProviderKind.TensorRTRtx)
        {
            return await BootstrapTensorRtRtxPluginAsync(allowDownloads, cancellationToken).ConfigureAwait(false);
        }

        bool nvidiaDriver = _linuxRuntimeProbe.IsNvidiaDriverLoaded();

        ExecutionProviderBootstrapResult result = provider switch
        {
            ExecutionProviderKind.Cpu =>
                new(provider, provider, Succeeded: true, Detail: "CPU available on Linux."),

            ExecutionProviderKind.Cuda =>
                nvidiaDriver
                    ? new(provider, provider, Succeeded: true, Detail: "NVIDIA driver detected.")
                    : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                        Detail: "No NVIDIA driver found (/proc/driver/nvidia/gpus/ absent). Falling back to CPU.",
                        FailureReason: "NVIDIA driver not loaded."),

            ExecutionProviderKind.TensorRt =>
                nvidiaDriver && _linuxRuntimeProbe.IsNativeTensorRtAvailable()
                    ? new(provider, provider, Succeeded: true, Detail: "NVIDIA driver + libnvinfer detected.")
                    : nvidiaDriver
                        ? new(provider, ExecutionProviderKind.Cuda, Succeeded: false,
                            Detail: "libnvinfer not found; TensorRT unavailable. Falling back to CUDA.",
                            FailureReason: "libnvinfer not found.")
                        : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                            Detail: "No NVIDIA driver; TensorRT unavailable. Falling back to CPU.",
                            FailureReason: "NVIDIA driver not loaded."),

            ExecutionProviderKind.OpenVino =>
                await ResolveOpenVinoAsync(provider, allowDownloads, cancellationToken).ConfigureAwait(false),

            ExecutionProviderKind.Dnnl => await ResolveDnnlAsync(provider, cancellationToken).ConfigureAwait(false),

            ExecutionProviderKind.DirectMl =>
                new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                    Detail: $"{provider} is Windows-only. Falling back to CPU.",
                    FailureReason: $"{provider} not available on Linux."),

            ExecutionProviderKind.Migraphx =>
                await ResolveMigraphxAsync(provider, allowDownloads, cancellationToken).ConfigureAwait(false),

            ExecutionProviderKind.CoreMl =>
                new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                    Detail: "CoreML is macOS-only. Falling back to CPU.",
                    FailureReason: "CoreML not available on Linux."),

            _ =>
                new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                    Detail: $"{provider} is not supported on Linux. Falling back to CPU.",
                    FailureReason: $"{provider} unsupported on Linux."),
        };

        return result;
    }

    // The session appends the OpenVINO plugin EP, so readiness is the catalog's. The standalone
    // OpenVINO runtime (IOpenVinoAvailabilityProvider) loads libopenvino_c.so but registers no
    // ORT execution provider, so it cannot back an ORT session.
    private async Task<ExecutionProviderBootstrapResult> ResolveOpenVinoAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        LinuxPluginCatalogResult catalog = await _openVinoCatalog
            .EnsureReadyAsync(allowDownloads, cancellationToken)
            .ConfigureAwait(false);
        return catalog.IsReady
            ? new(provider, provider, Succeeded: true, Detail: catalog.Detail)
            : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                Detail: $"{catalog.Detail} Falling back to CPU.",
                FailureReason: catalog.Detail);
    }

    private async Task<ExecutionProviderBootstrapResult> ResolveMigraphxAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        // An ONNX Runtime build with MIGraphX compiled in needs no plugin.
        if (_linuxRuntimeProbe.IsMigraphxOrtProviderAvailable())
        {
            return new(provider, provider, Succeeded: true,
                Detail: "MIGraphXExecutionProvider is listed by ONNX Runtime.");
        }

        LinuxPluginCatalogResult catalog = await _migraphxCatalog
            .EnsureReadyAsync(allowDownloads, cancellationToken)
            .ConfigureAwait(false);
        return catalog.IsReady
            ? new(provider, provider, Succeeded: true, Detail: catalog.Detail)
            : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                Detail: $"{catalog.Detail} Falling back to CPU.",
                FailureReason: catalog.Detail);
    }

    private async Task<ExecutionProviderBootstrapResult> ResolveDnnlAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        DnnlReadinessReport report = await _dnnlReadinessProbe
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return report.IsReady
            ? new(provider, provider, Succeeded: true, Detail: report.Detail)
            : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                Detail: $"{report.Detail} Falling back to CPU.",
                FailureReason: report.Detail);
    }

    public Task<ExecutionProviderBootstrapResult> CheckReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken) =>
        BootstrapAsync(provider, allowDownloads: false, cancellationToken);

    private async Task<ExecutionProviderBootstrapResult> BootstrapTensorRtRtxPluginAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        // Bootstrap never triggers bundle downloads — explicit Install paths only (Model Manager, CLI).
        _ = allowProviderDownloads;
        const bool allowDownloadsDuringBootstrap = false;

        TensorRtRtxBootstrapResult plugin = await _tensorRtRtxPluginBootstrap
            .EnsureRegisteredAsync(allowDownloadsDuringBootstrap, cancellationToken)
            .ConfigureAwait(false);

        if (plugin.Succeeded)
        {
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.TensorRTRtx,
                ExecutionProviderKind.TensorRTRtx,
                Succeeded: true,
                Detail: plugin.Detail,
                FailureReason: null);
        }

        bool cudaFallbackVerified =
            _linuxRuntimeProbe.IsNvidiaDriverLoaded() && _linuxRuntimeProbe.IsCudaOrtProviderAvailable();
        ExecutionProviderKind selectedProvider = cudaFallbackVerified
            ? ExecutionProviderKind.Cuda
            : ExecutionProviderKind.Cpu;
        string detail = cudaFallbackVerified
            ? $"{plugin.Detail} Verified fallback: CUDA."
            : $"{plugin.Detail} CUDA fallback was not verified; using CPU.";

        return new ExecutionProviderBootstrapResult(
            ExecutionProviderKind.TensorRTRtx,
            selectedProvider,
            Succeeded: false,
            Detail: detail,
            FailureReason: plugin.Detail);
    }
}
