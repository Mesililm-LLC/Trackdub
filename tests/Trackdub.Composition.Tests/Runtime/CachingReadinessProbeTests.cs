using Trackdub.Composition.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Tests.Runtime;

/// <summary>
/// Guards the fix for the double-probe defect: <c>providers list</c> ran each shared readiness
/// probe twice (once via <see cref="OnnxExecutionProviderDiscovery"/> and again via the
/// *RuntimeReadinessService wrappers). The caching decorators memoize the first result so each
/// underlying probe runs exactly once per <c>allowProviderDownloads</c> value.
/// </summary>
public sealed class CachingReadinessProbeTests
{
    private static TensorRtRtxReadinessReport EligibleReport() =>
        new(
            ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
            Route: TensorRtRtxPlatformRoute.PluginEpAbi,
            Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
            IsHardwareEligible: true,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "TensorRT RTX EP plugin is not installed.");

    [Fact]
    public async Task DiscoveryAndRemediation_ShareCachedProbe_InvokesUnderlyingProbeOnce()
    {
        var counting = new CountingTensorRtRtxReadinessProbe(EligibleReport());
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        // The discovery graph and the runtime-readiness-service wrapper resolve the SAME cached
        // probe singleton, exactly as CompositionRoot wires them.
        var discovery = new OnnxExecutionProviderDiscovery(
            new NullOpenVinoAvailabilityProvider(),
            new LinuxNativeGpuRuntimeProbe(),
            new StubNativeCudaTensorRtWindowsPolicy(),
            new StubMigraphxReadinessProbe(),
            new StubDnnlReadinessProbe(),
            cached,
            new StubOpenVinoCatalogReadinessProbe(),
            new StubQnnCatalogReadinessProbe(),
            new StubVitisAiCatalogReadinessProbe(),
            isTensorRtRtxEnabled: static _ => Task.FromResult(true));
        var wrapper = new TensorRtRtxRuntimeReadinessService(cached);

        var hardwareProfile = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090");

        // Mirror ProvidersListHandler.ListAsync: discovery pass, then remediation pass.
        await discovery.DiscoverAsync(hardwareProfile, CancellationToken.None);
        await wrapper.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.Equal(1, counting.CallCount);
    }

    [Fact]
    public async Task CachedResult_EqualsFirstProbeResult_NoFabrication()
    {
        TensorRtRtxReadinessReport first = EligibleReport();
        var counting = new CountingTensorRtRtxReadinessProbe(first);
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        TensorRtRtxReadinessReport a = await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        TensorRtRtxReadinessReport b = await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        // The reused value is the actual first probe result, not a fabricated one.
        Assert.Same(first, a);
        Assert.Same(a, b);
        Assert.Equal(1, counting.CallCount);
    }

    [Fact]
    public async Task AllowProviderDownloadsTrue_IsNotServedCachedFalseResult()
    {
        var falseReport = EligibleReport();
        var trueReport = EligibleReport() with { Detail = "downloads allowed" };
        var counting = new CountingTensorRtRtxReadinessProbe(
            allow => allow ? trueReport : falseReport);
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        TensorRtRtxReadinessReport disabled =
            await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        TensorRtRtxReadinessReport enabled =
            await cached.ProbeAsync(allowProviderDownloads: true, CancellationToken.None);

        Assert.Same(falseReport, disabled);
        Assert.Same(trueReport, enabled);
        // One probe per distinct allowProviderDownloads key.
        Assert.Equal(2, counting.CallCount);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareSingleInFlightProbe()
    {
        var gate = new TaskCompletionSource();
        var counting = new CountingTensorRtRtxReadinessProbe(EligibleReport(), gate.Task);
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        Task<TensorRtRtxReadinessReport>[] callers = Enumerable
            .Range(0, 16)
            .Select(_ => cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, counting.CallCount);
    }

    [Fact]
    public async Task FaultedProbe_IsNotCached_AndReprobesOnNextCall()
    {
        var counting = new CountingTensorRtRtxReadinessProbe(EligibleReport())
        {
            ThrowOnFirstCall = true,
        };
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None));

        // The faulted probe must not be cached permanently; a retry re-runs the probe.
        TensorRtRtxReadinessReport recovered =
            await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.True(recovered.IsHardwareEligible);
        Assert.Equal(2, counting.CallCount);
    }

    [Fact]
    public async Task CanceledProbe_IsNotCached_AndReprobesOnNextCall()
    {
        var counting = new CountingTensorRtRtxReadinessProbe(EligibleReport())
        {
            CancelOnFirstCall = true,
        };
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None));

        // The canceled underlying probe must not be cached permanently; a retry re-runs the probe.
        TensorRtRtxReadinessReport recovered =
            await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.True(recovered.IsHardwareEligible);
        Assert.Equal(2, counting.CallCount);
    }

    [Fact]
    public async Task Invalidate_ForcesReprobe_AfterStateChange()
    {
        TensorRtRtxReadinessReport preInstall = EligibleReport();
        TensorRtRtxReadinessReport postInstall = EligibleReport() with
        {
            Blocker = TensorRtRtxReadinessBlocker.None,
            IsOrtProviderListed = true,
            IsRegisteredWithOrt = true,
            Detail = "TensorRT RTX EP plugin installed and registered.",
        };

        // Model a pre/post-install state transition: the underlying probe returns the stale
        // "not present" report until the simulated install flips it to a ready report.
        TensorRtRtxReadinessReport current = preInstall;
        var counting = new CountingTensorRtRtxReadinessProbe(_ => current);
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        TensorRtRtxReadinessReport before =
            await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.Same(preInstall, before);
        Assert.False(before.IsReady);

        // Simulate a successful install that changes process state.
        current = postInstall;

        // Without invalidation the cached pre-install snapshot would be served; invalidate first.
        ((IReadinessProbeCache)cached).Invalidate();

        TensorRtRtxReadinessReport after =
            await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.Same(postInstall, after);
        Assert.True(after.IsReady);
        Assert.Equal(2, counting.CallCount);
    }

    [Fact]
    public async Task ProgressBearingInstallProbe_InvalidatesCache_SoLaterStatusProbeReprobes()
    {
        MigraphxReadinessReport preInstall = MigraphxEpNotPresentReport();
        MigraphxReadinessReport postInstall = MigraphxReadyReport();

        MigraphxReadinessReport current = preInstall;
        var counting = new CountingMigraphxReadinessProbe(() => current);
        var cached = new CachingMigraphxReadinessProbe(counting);

        // A status-only probe caches the stale pre-install snapshot.
        MigraphxReadinessReport stale = await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.Same(preInstall, stale);

        // A progress-bearing download-enabled install probe changes process state, so it must
        // invalidate the memoized results when it completes; otherwise the next status-only caller
        // would keep receiving the stale pre-install snapshot.
        current = postInstall;
        MigraphxReadinessReport installed = await cached.ProbeAsync(
            allowProviderDownloads: true,
            CancellationToken.None,
            new RecordingProgress());

        MigraphxReadinessReport fresh = await cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.Same(postInstall, installed);
        Assert.Same(postInstall, fresh);
        Assert.Equal(3, counting.CallCount);
    }

    [Fact]
    public async Task IndividualCallerCancellation_DoesNotPoisonConcurrentCallers()
    {
        var gate = new TaskCompletionSource();
        var counting = new CountingTensorRtRtxReadinessProbe(EligibleReport(), gate.Task);
        var cached = new CachingTensorRtRtxReadinessProbe(counting);

        using var cts = new CancellationTokenSource();

        // Start multiple concurrent calls: the first with a token that will be cancelled mid-flight,
        // and others with valid tokens. All callers share the same underlying in-flight probe.
        Task<TensorRtRtxReadinessReport>[] callers =
        [
            cached.ProbeAsync(allowProviderDownloads: false, cts.Token),
            cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None),
            cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None),
            cached.ProbeAsync(allowProviderDownloads: false, CancellationToken.None),
        ];

        // Cancel the first caller's token mid-flight, while the underlying probe is still in-progress.
        cts.Cancel();

        // Release the shared probe.
        gate.SetResult();

        // The first caller sees OperationCanceledException from WaitAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callers[0]);

        // The other callers (with valid tokens) receive the successful probe result; they are not
        // poisoned by the first caller's cancellation.
        TensorRtRtxReadinessReport result1 = await callers[1];
        TensorRtRtxReadinessReport result2 = await callers[2];
        TensorRtRtxReadinessReport result3 = await callers[3];

        Assert.True(result1.IsHardwareEligible);
        Assert.True(result2.IsHardwareEligible);
        Assert.True(result3.IsHardwareEligible);

        // The underlying probe completed successfully exactly once, despite the first caller's
        // cancellation. The shared probe was never cancelled.
        Assert.Equal(1, counting.CallCount);
    }

    private sealed class CountingTensorRtRtxReadinessProbe : ITensorRtRtxReadinessProbe
    {
        private readonly Func<bool, TensorRtRtxReadinessReport> _factory;
        private readonly Task? _gate;
        private int _callCount;

        public CountingTensorRtRtxReadinessProbe(TensorRtRtxReadinessReport report, Task? gate = null)
            : this(_ => report, gate)
        {
        }

        public CountingTensorRtRtxReadinessProbe(Func<bool, TensorRtRtxReadinessReport> factory, Task? gate = null)
        {
            _factory = factory;
            _gate = gate;
        }

        public bool ThrowOnFirstCall { get; init; }

        public bool CancelOnFirstCall { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<TensorRtRtxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (_gate is not null)
            {
                await _gate.ConfigureAwait(false);
            }

            if (ThrowOnFirstCall && call == 1)
            {
                throw new InvalidOperationException("Simulated probe failure.");
            }

            if (CancelOnFirstCall && call == 1)
            {
                throw new OperationCanceledException("Simulated probe cancellation.");
            }

            return _factory(allowProviderDownloads);
        }
    }

    private sealed class CountingMigraphxReadinessProbe : IMigraphxReadinessProbe
    {
        private readonly Func<MigraphxReadinessReport> _factory;
        private int _callCount;

        public CountingMigraphxReadinessProbe(Func<MigraphxReadinessReport> factory) => _factory = factory;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<MigraphxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default,
            IProgress<double>? progress = null)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_factory());
        }
    }

    private static MigraphxReadinessReport MigraphxEpNotPresentReport() =>
        new(
            MigraphxProviderIds.WinMl,
            MigraphxPlatformRoute.WinMlCatalog,
            MigraphxReadinessBlocker.EpNotPresent,
            IsHardwareEligible: true,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "MIGraphX execution provider is not installed.");

    private static MigraphxReadinessReport MigraphxReadyReport() =>
        new(
            MigraphxProviderIds.WinMl,
            MigraphxPlatformRoute.WinMlCatalog,
            MigraphxReadinessBlocker.None,
            IsHardwareEligible: true,
            IsOrtProviderListed: true,
            IsRegisteredWithOrt: true,
            Detail: "MIGraphX execution provider installed and registered.");

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();

        public void Report(double value) => Reports.Add(value);
    }

    private sealed class StubNativeCudaTensorRtWindowsPolicy : INativeCudaTensorRtWindowsPolicy
    {
        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class StubMigraphxReadinessProbe : IMigraphxReadinessProbe
    {
        public Task<MigraphxReadinessReport> ProbeAsync(bool allowProviderDownloads, CancellationToken cancellationToken = default, IProgress<double>? progress = null) =>
            Task.FromResult(new MigraphxReadinessReport(
                MigraphxProviderIds.WinMl,
                MigraphxPlatformRoute.None,
                MigraphxReadinessBlocker.PlatformUnsupported,
                IsHardwareEligible: false,
                IsOrtProviderListed: false,
                IsRegisteredWithOrt: false,
                Detail: "stub"));
    }

    private sealed class StubDnnlReadinessProbe : IDnnlReadinessProbe
    {
        public Task<DnnlReadinessReport> ProbeAsync(bool allowProviderDownloads, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnnlReadinessReport(
                DnnlProviderIds.NativeOrt,
                DnnlReadinessBlocker.UnsupportedRid,
                IsSupportedRid: false,
                IsOrtProviderListed: false,
                CanAppendSessionOptions: false,
                SmokeTestPassed: false,
                Detail: "stub"));
    }

    private sealed class StubOpenVinoCatalogReadinessProbe : IOpenVinoCatalogReadinessProbe
    {
        public Task<WinMlCatalogReadinessReport> ProbeAsync(bool allowProviderDownloads, CancellationToken cancellationToken = default) =>
            Task.FromResult(WinMlCatalogStub(OpenVinoCatalogProviderIds.WinMl));
    }

    private sealed class StubQnnCatalogReadinessProbe : IQnnCatalogReadinessProbe
    {
        public Task<WinMlCatalogReadinessReport> ProbeAsync(bool allowProviderDownloads, CancellationToken cancellationToken = default) =>
            Task.FromResult(WinMlCatalogStub(QnnProviderIds.WinMl));
    }

    private sealed class StubVitisAiCatalogReadinessProbe : IVitisAiCatalogReadinessProbe
    {
        public Task<WinMlCatalogReadinessReport> ProbeAsync(bool allowProviderDownloads, CancellationToken cancellationToken = default) =>
            Task.FromResult(WinMlCatalogStub(VitisAiProviderIds.WinMl));
    }

    private static WinMlCatalogReadinessReport WinMlCatalogStub(string providerId) =>
        new(
            providerId,
            WinMlCatalogPlatformRoute.None,
            WinMlCatalogReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "stub");
}
