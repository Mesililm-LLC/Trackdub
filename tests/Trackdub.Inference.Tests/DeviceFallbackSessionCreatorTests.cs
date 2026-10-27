using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class DeviceFallbackSessionCreatorTests
{
    private static readonly Exception OomException =
        new("[ErrorCode:RuntimeException] CUDA error: out of memory");

    private static readonly Exception DeviceFailureException =
        new("[ErrorCode:RuntimeException] DXGI_ERROR_DEVICE_REMOVED");

    private static readonly Exception UnrelatedException =
        new InvalidOperationException("something else entirely");

    private static StageRuntimePlan MakePlan(int? deviceIndex, string? adapter = null) =>
        new()
        {
            Stage = RuntimeStage.Asr,
            ExecutionProvider = ExecutionProviderKind.Cuda,
            DeviceIndex = deviceIndex,
            DeviceAdapterDescription = adapter
        };

    [Fact]
    public async Task CreateWithDeviceFallbackAsync_OomOnFirstDevice_FallsBackAndReportsDegradation()
    {
        var provider = new PipelineDeviceExclusionProvider();
        DeviceExclusionSet exclusions = provider.BeginRun();
        StageRuntimePlan initialPlan = MakePlan(0, "device-0");
        StageRuntimePlan fallbackPlan = MakePlan(1, "device-1");
        int callCount = 0;

        Task<string> CreateSessionAsync(StageRuntimePlan plan, CancellationToken ct)
        {
            callCount++;
            if (plan.DeviceIndex == 0)
            {
                throw OomException;
            }

            return Task.FromResult("session-on-device-1");
        }

        Task<StageRuntimePlan> ReplanAsync(CancellationToken ct) => Task.FromResult(fallbackPlan);

        DeviceFallbackSessionCreator.Result<string> result = await DeviceFallbackSessionCreator
            .CreateWithDeviceFallbackAsync(
                initialPlan,
                CreateSessionAsync,
                ReplanAsync,
                provider,
                cancellationToken: CancellationToken.None);

        Assert.Equal("session-on-device-1", result.Lease);
        Assert.NotNull(result.Degradation);
        Assert.Equal(DeviceDegradationKind.MemoryExhausted, result.Degradation!.Kind);
        Assert.Equal(0, result.Degradation.FailedDeviceIndex);
        Assert.Equal(1, result.Degradation.FallbackDeviceIndex);
        Assert.Equal(2, callCount);
        Assert.True(exclusions.IsExcluded(0));
        Assert.False(exclusions.IsExcluded(1));
    }

    [Fact]
    public async Task CreateWithDeviceFallbackAsync_DeviceFailureOnFirstDevice_FallsBackAndReportsDegradation()
    {
        var provider = new PipelineDeviceExclusionProvider();
        DeviceExclusionSet exclusions = provider.BeginRun();
        StageRuntimePlan initialPlan = MakePlan(0, "device-0");
        StageRuntimePlan fallbackPlan = MakePlan(1, "device-1");

        Task<string> CreateSessionAsync(StageRuntimePlan plan, CancellationToken ct) =>
            plan.DeviceIndex == 0
                ? throw DeviceFailureException
                : Task.FromResult("session-on-device-1");

        Task<StageRuntimePlan> ReplanAsync(CancellationToken ct) => Task.FromResult(fallbackPlan);

        DeviceFallbackSessionCreator.Result<string> result = await DeviceFallbackSessionCreator
            .CreateWithDeviceFallbackAsync(
                initialPlan,
                CreateSessionAsync,
                ReplanAsync,
                provider,
                cancellationToken: CancellationToken.None);

        Assert.Equal(DeviceDegradationKind.DeviceFailed, result.Degradation!.Kind);
        Assert.True(exclusions.IsExcluded(0));
    }

    [Fact]
    public async Task CreateWithDeviceFallbackAsync_AllDevicesExhausted_PropagatesLastExceptionAndBoundsRetries()
    {
        var provider = new PipelineDeviceExclusionProvider();
        DeviceExclusionSet exclusions = provider.BeginRun();
        StageRuntimePlan devicePlan0 = MakePlan(0, "device-0");
        StageRuntimePlan devicePlan1 = MakePlan(1, "device-1");
        StageRuntimePlan noDevicePlan = MakePlan(null);
        int callCount = 0;
        int replanCount = 0;

        Task<string> CreateSessionAsync(StageRuntimePlan plan, CancellationToken ct)
        {
            callCount++;
            throw OomException;
        }

        Task<StageRuntimePlan> ReplanAsync(CancellationToken ct)
        {
            replanCount++;
            return Task.FromResult(replanCount == 1 ? devicePlan1 : noDevicePlan);
        }

        Exception thrown = await Assert.ThrowsAsync<Exception>(() =>
            DeviceFallbackSessionCreator.CreateWithDeviceFallbackAsync(
                devicePlan0,
                CreateSessionAsync,
                ReplanAsync,
                provider,
                cancellationToken: CancellationToken.None));

        Assert.Same(OomException, thrown);
        Assert.Equal(3, callCount);
        Assert.True(exclusions.IsExcluded(0));
        Assert.True(exclusions.IsExcluded(1));
    }

    [Fact]
    public async Task CreateWithDeviceFallbackAsync_UnrelatedException_PropagatesImmediatelyWithNoExclusions()
    {
        var provider = new PipelineDeviceExclusionProvider();
        DeviceExclusionSet exclusions = provider.BeginRun();
        StageRuntimePlan initialPlan = MakePlan(0, "device-0");
        int callCount = 0;
        int replanCount = 0;

        Task<string> CreateSessionAsync(StageRuntimePlan plan, CancellationToken ct)
        {
            callCount++;
            throw UnrelatedException;
        }

        Task<StageRuntimePlan> ReplanAsync(CancellationToken ct)
        {
            replanCount++;
            return Task.FromResult(initialPlan);
        }

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeviceFallbackSessionCreator.CreateWithDeviceFallbackAsync(
                initialPlan,
                CreateSessionAsync,
                ReplanAsync,
                provider,
                cancellationToken: CancellationToken.None));

        Assert.Same(UnrelatedException, thrown);
        Assert.Equal(1, callCount);
        Assert.Equal(0, replanCount);
        Assert.False(exclusions.IsExcluded(0));
    }

    [Fact]
    public async Task CreateWithDeviceFallbackAsync_repeatedMemoryExhaustionOnSameDevice_stopsRetrying()
    {
        StageRuntimePlan plan = MakePlan(0, "device-0");
        int createCount = 0;
        await Assert.ThrowsAsync<Exception>(() => DeviceFallbackSessionCreator.CreateWithDeviceFallbackAsync(
            plan,
            (_, _) =>
            {
                createCount++;
                return Task.FromException<string>(new Exception("[ErrorCode:RuntimeException] CUDA error: out of memory"));
            },
            _ => Task.FromResult(plan),
            exclusionProvider: null,
            maxAttempts: 8));

        Assert.Equal(2, createCount);
    }
}
