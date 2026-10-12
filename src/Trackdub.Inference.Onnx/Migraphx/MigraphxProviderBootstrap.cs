using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Migraphx;

public sealed class MigraphxProviderBootstrap : IMigraphxProviderBootstrap
{
    private readonly IMigraphxReadinessProbe _probe;

    public MigraphxProviderBootstrap(IMigraphxReadinessProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<MigraphxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        MigraphxReadinessReport report = await _probe
            .ProbeAsync(allowProviderDownloads, cancellationToken, progress)
            .ConfigureAwait(false);

        if (report.IsReady)
        {
            return new MigraphxBootstrapResult(true, report.ProviderId, null, report.Detail);
        }

        return new MigraphxBootstrapResult(false, report.ProviderId, report.Blocker, report.Detail);
    }
}
