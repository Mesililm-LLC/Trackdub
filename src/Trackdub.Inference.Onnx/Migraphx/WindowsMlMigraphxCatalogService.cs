using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Onnx.WindowsMl;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsMlMigraphxCatalogService
{
    public async Task<MigraphxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (bool eligible, MigraphxReadinessBlocker hardwareBlocker, string hardwareDetail) = WindowsMigraphxHardwareGate.Evaluate();
        if (!eligible)
        {
            return new MigraphxBootstrapResult(false, MigraphxProviderIds.WinMl, hardwareBlocker, hardwareDetail);
        }

        CatalogPreparationPhase phase = CatalogPreparationPhase.Register;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(allowProviderDownloads ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));

            ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
            ExecutionProvider? migraphx = catalog.FindAllProviders()
                .FirstOrDefault(provider =>
                    string.Equals(provider.Name, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.Ordinal));

            if (migraphx is null)
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.EpNotPresent,
                    $"{MigraphxProviderConstants.OrtExecutionProviderName} is not offered by the Windows ML catalog on this machine.");
            }

            CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
                WindowsMlEnsureReady.MapReadyState(migraphx.ReadyState),
                allowProviderDownloads,
                MigraphxProviderConstants.OrtExecutionProviderName);
            phase = plan.Phase;
            if (plan.StopWithoutAcquisition)
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.EpNotPresent,
                    plan.StopDetail ?? $"{MigraphxProviderConstants.OrtExecutionProviderName} is not installed.");
            }

            if (plan.CallEnsureReady)
            {
                ExecutionProviderReadyResult readyResult = await WindowsMlEnsureReady
                    .EnsureReadyBoundedAsync(migraphx, timeoutCts.Token, progress)
                    .ConfigureAwait(false);
                CatalogPreparationOutcome outcome = WindowsMlCatalogPreparationPolicy.Classify(
                    WindowsMlEnsureReady.MapEnsureStatus(readyResult.Status),
                    plan.Phase,
                    MigraphxProviderConstants.OrtExecutionProviderName,
                    WindowsMlEnsureReady.FormatHResult(readyResult),
                    readyResult.DiagnosticText);
                if (outcome.Disposition is not CatalogPreparationDisposition.Register)
                {
                    return new MigraphxBootstrapResult(
                        false,
                        MigraphxProviderIds.WinMl,
                        outcome.Disposition is CatalogPreparationDisposition.Pending
                            ? MigraphxReadinessBlocker.EpPreparationPending
                            : plan.Phase is CatalogPreparationPhase.Acquire
                                ? MigraphxReadinessBlocker.EpDownloadFailed
                                : MigraphxReadinessBlocker.EpRegisterFailed,
                        outcome.Detail);
                }
            }

            if (!migraphx.TryRegister())
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.EpRegisterFailed,
                    $"TryRegister failed for {MigraphxProviderConstants.OrtExecutionProviderName}.");
            }

            if (!MigraphxOrtProbe.IsProviderListed())
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.OrtProviderUnavailable,
                    $"{MigraphxProviderConstants.OrtExecutionProviderName} registered with catalog but ONNX Runtime does not list the provider.");
            }

            return new MigraphxBootstrapResult(
                true,
                MigraphxProviderIds.WinMl,
                null,
                "MIGraphX execution provider registered with ONNX Runtime.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MigraphxBootstrapResult(
                false,
                MigraphxProviderIds.WinMl,
                phase == CatalogPreparationPhase.Acquire
                    ? MigraphxReadinessBlocker.EpDownloadFailed
                    : MigraphxReadinessBlocker.EpRegisterFailed,
                WindowsMlCatalogPreparationPolicy.TimeoutDetail(
                    MigraphxProviderConstants.OrtExecutionProviderName,
                    phase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MigraphxBootstrapResult(
                false,
                MigraphxProviderIds.WinMl,
                MigraphxReadinessBlocker.EpRegisterFailed,
                ex.Message);
        }
    }
}
