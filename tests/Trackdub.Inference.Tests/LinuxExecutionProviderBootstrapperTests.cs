using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.ExecutionProviders.Linux;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Tests for LinuxExecutionProviderBootstrapper: CUDA/TensorRT pre-flight, OpenVINO delegation,
/// and Windows/macOS-only EPs rejected with correct fallbacks.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxExecutionProviderBootstrapperTests
{
    // ── CPU always succeeds ──────────────────────────────────────────────────

    [Fact]
    public async Task Cpu_always_succeeds()
    {
        var bootstrapper = Make(openVinoAvailable: false);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.Cpu, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    // ── Windows-only EPs rejected ────────────────────────────────────────────

    [Fact]
    public async Task DirectMl_falls_back_to_Cpu_on_Linux()
    {
        var bootstrapper = Make(openVinoAvailable: false);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.DirectMl, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    [Fact]
    public async Task TensorRTRtx_falls_back_to_Cpu_on_Linux()
    {
        var bootstrapper = Make(openVinoAvailable: false);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    [Fact]
    public async Task TensorRTRtx_returns_early_without_download_when_bundle_not_installed()
    {
        const string pluginDirectory = "/opt/trt-rtx";
        var pluginBootstrap = new RecordingTensorRtRtxProviderBootstrap(pluginDirectory);

        var bootstrapper = new LinuxExecutionProviderBootstrapper(
            new StubOpenVinoProvider(isAvailable: false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false),
            pluginBootstrap);

        ExecutionProviderBootstrapResult result = await bootstrapper.BootstrapAsync(
            ExecutionProviderKind.TensorRTRtx,
            allowDownloads: true,
            default);

        Assert.True(pluginBootstrap.WasCalled);
        Assert.False(pluginBootstrap.LastAllowProviderDownloads);
        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cuda, result.SelectedProvider);
        Assert.Contains("Verified fallback: CUDA", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TensorRTRtx_uses_injected_plugin_bootstrap()
    {
        const string pluginDirectory = "/opt/trt-rtx";
        var pluginBootstrap = new RecordingTensorRtRtxProviderBootstrap(pluginDirectory);

        var bootstrapper = new LinuxExecutionProviderBootstrapper(
            new StubOpenVinoProvider(isAvailable: false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false),
            pluginBootstrap);

        ExecutionProviderBootstrapResult result = await bootstrapper.BootstrapAsync(
            ExecutionProviderKind.TensorRTRtx,
            allowDownloads: false,
            default);

        Assert.True(pluginBootstrap.WasCalled);
        Assert.False(pluginBootstrap.LastAllowProviderDownloads);
        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cuda, result.SelectedProvider);
        Assert.Contains(pluginDirectory, pluginBootstrap.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TensorRTRtx_bootstrap_ignores_allowDownloads_true()
    {
        var pluginBootstrap = new RecordingTensorRtRtxProviderBootstrap("/opt/trt-rtx");
        var bootstrapper = new LinuxExecutionProviderBootstrapper(
            new StubOpenVinoProvider(isAvailable: false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false),
            pluginBootstrap);

        await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: true, default);

        Assert.True(pluginBootstrap.WasCalled);
        Assert.False(pluginBootstrap.LastAllowProviderDownloads);
    }

    // ── macOS-only EPs rejected ──────────────────────────────────────────────

    [Fact]
    public async Task CoreMl_falls_back_to_Cpu_on_Linux()
    {
        var bootstrapper = Make(openVinoAvailable: false);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.CoreMl, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    // ── OpenVINO delegation ──────────────────────────────────────────────────

    [Fact]
    public async Task OpenVino_succeeds_when_catalog_plugin_is_ready()
    {
        var bootstrapper = Make(openVinoAvailable: true);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.OpenVino, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.OpenVino, result.SelectedProvider);
    }

    [Fact]
    public async Task OpenVino_falls_back_to_Cpu_when_catalog_plugin_is_not_ready()
    {
        var bootstrapper = Make(openVinoAvailable: false);

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.OpenVino, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
        Assert.Equal("stub: not ready", result.FailureReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenVino_passes_allowDownloads_to_catalog(bool allowDownloads)
    {
        var catalog = new StubOpenVinoCatalog(isReady: true);
        var bootstrapper = Make(catalog);

        await bootstrapper.BootstrapAsync(ExecutionProviderKind.OpenVino, allowDownloads, default);

        Assert.Equal([allowDownloads], catalog.EnsureCalls);
    }

    [Fact]
    public async Task OpenVino_ignores_standalone_runtime_when_catalog_is_not_ready()
    {
        // The standalone runtime registers no ORT EP, so it cannot make an OpenVINO session work.
        var bootstrapper = new LinuxExecutionProviderBootstrapper(
            new StubOpenVinoProvider(isAvailable: true),
            new StubLinuxNativeGpuRuntimeProbe(false, false),
            FailingTrtRtx(),
            new StubOpenVinoCatalog(isReady: false));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.OpenVino, allowDownloads: true, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    [Fact]
    public async Task CheckReadiness_for_OpenVino_never_allows_downloads()
    {
        var catalog = new StubOpenVinoCatalog(isReady: true);
        var bootstrapper = Make(catalog);

        await bootstrapper.CheckReadinessAsync(ExecutionProviderKind.OpenVino, default);

        Assert.Equal([false], catalog.EnsureCalls);
    }

    [Fact]
    public async Task Cuda_succeeds_when_nvidia_driver_is_detected_Async()
    {
        var bootstrapper = Make(
            openVinoAvailable: false,
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.Cuda, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cuda, result.SelectedProvider);
    }

    [Fact]
    public async Task Cuda_falls_back_to_Cpu_when_the_driver_is_loaded_but_the_Cuda_provider_is_not_usable_Async()
    {
        var bootstrapper = Make(
            openVinoAvailable: false,
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false, cudaOrtProviderAvailable: false));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.Cuda, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
        Assert.Contains("CUDAExecutionProvider", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TensorRt_falls_back_to_Cpu_when_neither_TensorRt_nor_Cuda_is_usable_Async()
    {
        var bootstrapper = Make(
            openVinoAvailable: false,
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false, cudaOrtProviderAvailable: false));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRt, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
        Assert.Contains("libnvinfer", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TensorRt_falls_back_to_Cuda_when_nvidia_driver_exists_without_libnvinfer_Async()
    {
        var bootstrapper = Make(
            openVinoAvailable: false,
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRt, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cuda, result.SelectedProvider);
        Assert.Contains("libnvinfer", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TensorRt_succeeds_when_nvidia_driver_and_libnvinfer_are_detected_Async()
    {
        var bootstrapper = Make(
            openVinoAvailable: false,
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: true));

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRt, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.TensorRt, result.SelectedProvider);
    }

    [Fact]
    public async Task TensorRTRtx_plugin_failure_uses_Cpu_when_Cuda_Ep_is_not_verified_Async()
    {
        var bootstrapper = new LinuxExecutionProviderBootstrapper(
            new StubOpenVinoProvider(isAvailable: false),
            new StubLinuxNativeGpuRuntimeProbe(
                nvidiaDriverLoaded: true,
                nativeTensorRtAvailable: false,
                cudaOrtProviderAvailable: false),
            new StubTensorRtRtxProviderBootstrap(new TensorRtRtxBootstrapResult(
                Succeeded: false,
                ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
                Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                Detail: "TensorRT RTX plugin missing.")));

        var result = await bootstrapper.BootstrapAsync(
            ExecutionProviderKind.TensorRTRtx,
            allowDownloads: false,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
        Assert.Contains("CUDA fallback was not verified", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verified fallback: CUDA", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── MIGraphX through the LinML catalog ───────────────────────────────────

    [Fact]
    public async Task Migraphx_succeeds_when_catalog_plugin_is_ready()
    {
        var catalog = new StubMigraphxCatalog(LinuxPluginCatalogStatus.Ready);

        var result = await MakeWithMigraphx(catalog).BootstrapAsync(ExecutionProviderKind.Migraphx, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Migraphx, result.SelectedProvider);
        Assert.Equal([false], catalog.EnsureCalls);
    }

    [Theory]
    [InlineData(LinuxPluginCatalogStatus.NotInstalled)]
    [InlineData(LinuxPluginCatalogStatus.Unsupported)]
    [InlineData(LinuxPluginCatalogStatus.Failed)]
    public async Task Migraphx_falls_back_to_Cpu_when_catalog_plugin_is_not_ready(LinuxPluginCatalogStatus status)
    {
        var catalog = new StubMigraphxCatalog(status);

        var result = await MakeWithMigraphx(catalog).BootstrapAsync(ExecutionProviderKind.Migraphx, allowDownloads: true, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
        Assert.Equal("stub detail", result.FailureReason);
        Assert.Equal([true], catalog.EnsureCalls);
    }

    [Fact]
    public async Task Migraphx_uses_a_built_in_ort_provider_without_the_catalog()
    {
        var catalog = new StubMigraphxCatalog(LinuxPluginCatalogStatus.NotInstalled);
        var probe = new StubLinuxNativeGpuRuntimeProbe(false, false, amdGpuPresent: true, migraphxOrtProviderAvailable: true);

        var result = await MakeWithMigraphx(catalog, probe).BootstrapAsync(ExecutionProviderKind.Migraphx, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Migraphx, result.SelectedProvider);
        Assert.Empty(catalog.EnsureCalls);
    }

    // ── CheckReadiness mirrors Bootstrap ────────────────────────────────────

    [Fact]
    public async Task CheckReadiness_returns_same_result_as_Bootstrap_for_OpenVino()
    {
        var bootstrapper = Make(openVinoAvailable: true);

        var bootstrap = await bootstrapper.BootstrapAsync(ExecutionProviderKind.OpenVino, allowDownloads: false, default);
        var readiness = await bootstrapper.CheckReadinessAsync(ExecutionProviderKind.OpenVino, default);

        Assert.Equal(bootstrap.Succeeded, readiness.Succeeded);
        Assert.Equal(bootstrap.SelectedProvider, readiness.SelectedProvider);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LinuxExecutionProviderBootstrapper Make(
        bool openVinoAvailable,
        ILinuxNativeGpuRuntimeProbe? linuxRuntimeProbe = null) =>
        new(
            new StubOpenVinoProvider(openVinoAvailable),
            linuxRuntimeProbe ?? new StubLinuxNativeGpuRuntimeProbe(false, false),
            FailingTrtRtx(),
            new StubOpenVinoCatalog(openVinoAvailable));

    private static LinuxExecutionProviderBootstrapper Make(StubOpenVinoCatalog catalog) =>
        new(
            new StubOpenVinoProvider(isAvailable: false),
            new StubLinuxNativeGpuRuntimeProbe(false, false),
            FailingTrtRtx(),
            catalog);

    private static LinuxExecutionProviderBootstrapper MakeWithMigraphx(
        StubMigraphxCatalog catalog,
        ILinuxNativeGpuRuntimeProbe? linuxRuntimeProbe = null) =>
        new(
            new StubOpenVinoProvider(isAvailable: false),
            linuxRuntimeProbe ?? new StubLinuxNativeGpuRuntimeProbe(false, false, amdGpuPresent: true),
            FailingTrtRtx(),
            new StubOpenVinoCatalog(isReady: false),
            catalog);

    private static StubTensorRtRtxProviderBootstrap FailingTrtRtx() =>
        new(new TensorRtRtxBootstrapResult(
            Succeeded: false,
            ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
            Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
            Detail: "TensorRT RTX plugin missing."));

    private sealed class StubOpenVinoCatalog(bool isReady) : ILinuxOpenVinoCatalog
    {
        public List<bool> EnsureCalls { get; } = [];

        public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken)
        {
            EnsureCalls.Add(allowDownloads);
            return Task.FromResult(new LinuxPluginCatalogResult(
                isReady ? LinuxPluginCatalogStatus.Ready : LinuxPluginCatalogStatus.NotInstalled,
                isReady ? "stub: ready" : "stub: not ready"));
        }

        public bool TryAppend(Microsoft.ML.OnnxRuntime.SessionOptions options, bool cpuOnly, out string? failureReason)
        {
            failureReason = isReady ? null : "stub: not ready";
            return isReady;
        }
    }

    private sealed class StubMigraphxCatalog(LinuxPluginCatalogStatus status) : ILinuxMigraphxCatalog
    {
        public List<bool> EnsureCalls { get; } = [];

        public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken)
        {
            EnsureCalls.Add(allowDownloads);
            return Task.FromResult(new LinuxPluginCatalogResult(status, "stub detail"));
        }
    }

    private sealed class StubOpenVinoProvider : IOpenVinoAvailabilityProvider
    {
        public StubOpenVinoProvider(bool isAvailable) => IsAvailable = isAvailable;
        public bool IsAvailable { get; }
        public bool UseOpenVinoCpuProxy => false;
    }

    private sealed class StubLinuxNativeGpuRuntimeProbe(
        bool nvidiaDriverLoaded,
        bool nativeTensorRtAvailable,
        bool cudaOrtProviderAvailable = true,
        bool amdGpuPresent = false,
        bool migraphxOrtProviderAvailable = false)
        : ILinuxNativeGpuRuntimeProbe
    {
        public bool IsNvidiaDriverLoaded() => nvidiaDriverLoaded;

        public bool IsAmdGpuPresent() => amdGpuPresent;

        public bool IsNativeTensorRtAvailable() => nativeTensorRtAvailable;

        public bool IsCudaOrtProviderAvailable() => cudaOrtProviderAvailable;

        public bool IsMigraphxOrtProviderAvailable() => migraphxOrtProviderAvailable;
    }

    private sealed class StubTensorRtRtxProviderBootstrap(TensorRtRtxBootstrapResult result)
        : ITensorRtRtxProviderBootstrap
    {
        public Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class RecordingTensorRtRtxProviderBootstrap(string pluginDirectory)
        : ITensorRtRtxProviderBootstrap
    {
        public bool WasCalled { get; private set; }

        public string? LastDetail { get; private set; }

        public bool? LastAllowProviderDownloads { get; private set; }

        public Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastAllowProviderDownloads = allowProviderDownloads;
            cancellationToken.ThrowIfCancellationRequested();

            LastDetail = $"TensorRT RTX EP ABI plugin directory '{pluginDirectory}' was not found.";
            return Task.FromResult(new TensorRtRtxBootstrapResult(
                false,
                TensorRtRtxProviderIds.PluginEpAbi,
                TensorRtRtxReadinessBlocker.EpNotPresent,
                LastDetail));
        }
    }
}
