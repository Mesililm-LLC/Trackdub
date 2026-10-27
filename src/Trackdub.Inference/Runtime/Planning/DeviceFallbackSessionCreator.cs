using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Creates an inference session, and if session creation fails with a classified device
/// exception (OOM or device failure), excludes the failed device and re-plans onto the next
/// ranked device, retrying up to a bounded number of attempts. On a successful fallback it
/// returns a <see cref="DeviceDegradationReport"/> describing what failed and where it recovered.
/// </summary>
public static class DeviceFallbackSessionCreator
{
    public sealed record Result<TLease>(
        TLease Lease,
        StageRuntimePlan Plan,
        DeviceDegradationReport? Degradation);

    public static async Task<Result<TLease>> CreateWithDeviceFallbackAsync<TLease>(
        StageRuntimePlan initialPlan,
        Func<StageRuntimePlan, CancellationToken, Task<TLease>> createSessionAsync,
        Func<CancellationToken, Task<StageRuntimePlan>> replanAsync,
        IPipelineDeviceExclusionProvider? exclusionProvider,
        int maxAttempts = 8,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialPlan);
        ArgumentNullException.ThrowIfNull(createSessionAsync);
        ArgumentNullException.ThrowIfNull(replanAsync);

        StageRuntimePlan plan = initialPlan;
        (DeviceDegradationKind Kind, int DeviceIndex, string Adapter, string Detail)? pending = null;
        ExceptionDispatchInfo? lastDeviceException = null;
        const int maxSessionCreateAttempts = 4;
        int attempts = Math.Clamp(maxAttempts, 1, maxSessionCreateAttempts);
        int? exhaustedDeviceIndex = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TLease lease = await createSessionAsync(plan, cancellationToken).ConfigureAwait(false);
                DeviceDegradationReport? report = pending is null
                    ? null
                    : new DeviceDegradationReport(
                        pending.Value.Kind,
                        pending.Value.DeviceIndex,
                        pending.Value.Adapter,
                        pending.Value.Detail,
                        plan.DeviceIndex,
                        plan.DeviceAdapterDescription);
                return new Result<TLease>(lease, plan, report);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && DeviceOomExceptionHelper.ClassifyDeviceException(ex) is DeviceDegradationKind kind
                && plan.DeviceIndex is int failedDeviceIndex)
            {
                lastDeviceException = ExceptionDispatchInfo.Capture(ex);
                string adapter = plan.DeviceAdapterDescription ?? $"device {failedDeviceIndex}";
                if (kind == DeviceDegradationKind.MemoryExhausted)
                {
                    DeviceOomExceptionHelper.TryMarkDeviceExhausted(ex, failedDeviceIndex, exclusionProvider, logger);
                }
                else
                {
                    DeviceOomExceptionHelper.TryMarkDeviceFailed(failedDeviceIndex, ex.Message, exclusionProvider, logger);
                }

                pending = (kind, failedDeviceIndex, adapter, ex.Message);
                if (kind == DeviceDegradationKind.MemoryExhausted)
                {
                    bool alreadySeen = exhaustedDeviceIndex == failedDeviceIndex;
                    exhaustedDeviceIndex = failedDeviceIndex;
                    if (alreadySeen)
                    {
                        lastDeviceException.Throw();
                    }
                }

                plan = await replanAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        lastDeviceException?.Throw();
        throw new InvalidOperationException(
            "Device fallback exhausted all candidate devices during session creation.");
    }
}
