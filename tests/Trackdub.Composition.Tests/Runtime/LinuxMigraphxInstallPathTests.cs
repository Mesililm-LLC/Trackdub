using Trackdub.Application.Runtime;
using Trackdub.Composition.Runtime;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class LinuxMigraphxInstallPathTests
{
    [Theory]
    [InlineData(MigraphxReadinessBlocker.EpNotPresent)]
    [InlineData(MigraphxReadinessBlocker.EpDownloadFailed)]
    public async Task Readiness_offers_install_on_the_LinML_route(MigraphxReadinessBlocker blocker)
    {
        var service = new MigraphxRuntimeReadinessService(new StubProbe(LinuxReport(blocker, ready: false)));

        MigraphxRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(allowProviderDownloads: false);

        Assert.True(snapshot.IsSupportedPlatform);
        Assert.True(snapshot.CanInstallWinMlProvider);
        Assert.Equal("Linux (LinML catalog)", snapshot.RouteDisplay);
        Assert.Contains("LinML", snapshot.InstallHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_points_at_rocm_when_the_runtime_is_missing()
    {
        var service = new MigraphxRuntimeReadinessService(
            new StubProbe(LinuxReport(MigraphxReadinessBlocker.OrtProviderUnavailable, ready: false)));

        MigraphxRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(allowProviderDownloads: false);

        Assert.False(snapshot.CanInstallWinMlProvider);
        Assert.Contains("ROCm", snapshot.InstallHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_reports_ready_on_the_LinML_route()
    {
        var service = new MigraphxRuntimeReadinessService(
            new StubProbe(LinuxReport(MigraphxReadinessBlocker.None, ready: true)));

        MigraphxRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(allowProviderDownloads: false);

        Assert.True(snapshot.IsReady);
        Assert.Equal("Linux (LinML catalog)", snapshot.RouteDisplay);
    }

    [Fact]
    public async Task Advisor_offers_MIGraphX_for_an_AMD_GPU_on_Linux()
    {
        var advisor = new StarterPackGpuSetupAdvisor(
            new FixedHardware(new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "AMD Radeon RX 7900 XTX")),
            migraphxReadiness: new MigraphxRuntimeReadinessService(
                new StubProbe(LinuxReport(MigraphxReadinessBlocker.EpNotPresent, ready: false))));

        StarterPackGpuSetupHint? hint = await advisor.ResolveAsync(new StarterPackCompatibilityReport(
            "premium",
            "default",
            "balanced_gpu",
            [new StageCompatibilityEntry(
                StageNames.Vad, "vad", "default", "directml", "default", "cpu",
                FallbackApplied: true, FallbackReason: "ep_unavailable", Runnable: true)],
            AllStagesRunnable: true,
            AnyFallbackApplied: true));

        Assert.NotNull(hint);
        Assert.Equal(StarterPackGpuRuntimeKind.AmdMigraphx, hint!.RuntimeKind);
        Assert.True(hint.CanInstall);
    }

    [Fact]
    public async Task Orchestrator_installs_MIGraphX_on_Linux_and_refreshes_the_cached_probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var inner = new StubProbe(LinuxReport(MigraphxReadinessBlocker.EpNotPresent, ready: false));
        var cached = new CachingMigraphxReadinessProbe(inner);
        var orchestrator = new GpuRuntimeInstallOrchestrator(
            migraphxReadiness: new MigraphxRuntimeReadinessService(cached),
            migraphxReadinessProbe: cached);

        // A stale downloads-allowed result must not short-circuit the install.
        _ = await cached.ProbeAsync(allowProviderDownloads: true);
        inner.Report = LinuxReport(MigraphxReadinessBlocker.None, ready: true);

        GpuRuntimeInstallResult result = await orchestrator.InstallAsync(
            StarterPackGpuRuntimeKind.AmdMigraphx, new Progress<string>());
        MigraphxReadinessReport afterInstall = await cached.ProbeAsync(allowProviderDownloads: false);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.True(afterInstall.IsReady);
        Assert.Equal([true, true, false], inner.AllowDownloadsCalls);
    }

    private sealed class FixedHardware(HardwareProfile profile) : IHardwareProfileProvider
    {
        public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private static MigraphxReadinessReport LinuxReport(MigraphxReadinessBlocker blocker, bool ready) =>
        new(
            MigraphxProviderIds.LinMl,
            MigraphxPlatformRoute.LinMlCatalog,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ready,
            IsRegisteredWithOrt: ready,
            Detail: "detail");

    private sealed class StubProbe(MigraphxReadinessReport report) : IMigraphxReadinessProbe
    {
        public MigraphxReadinessReport Report { get; set; } = report;

        public List<bool> AllowDownloadsCalls { get; } = [];

        public Task<MigraphxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            AllowDownloadsCalls.Add(allowProviderDownloads);
            return Task.FromResult(Report);
        }
    }
}
