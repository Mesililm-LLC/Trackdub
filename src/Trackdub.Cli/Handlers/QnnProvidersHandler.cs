using System.Text.Json;

using Trackdub.Application.Runtime;
using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Headless Qualcomm QNN Windows ML catalog EP status and install for operators.
/// </summary>
internal static class QnnProvidersHandler
{
    private const string LicenseReference =
        "Qualcomm QNN license (LICENSE.pdf in the Qualcomm Neural Processing SDK ZIP); see docs/legal/THIRD_PARTY_NOTICES.md and https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html";

    public static async Task<int> StatusAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        IQnnCatalogRuntimeReadinessService readinessService =
            factory.GetRequiredService<IQnnCatalogRuntimeReadinessService>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        WinMlCatalogRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        var payload = new QnnStatusOutput
        {
            Ready = snapshot.IsReady,
            SupportedPlatform = snapshot.IsSupportedPlatform,
            ProviderId = snapshot.ProviderId,
            Route = snapshot.RouteDisplay,
            StatusLabel = snapshot.StatusLabel,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsHardwareEligible = snapshot.IsHardwareEligible,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            IsRegisteredWithOrt = snapshot.IsRegisteredWithOrt,
            CanInstallWinMlProvider = snapshot.CanInstallWinMlProvider,
            LicenseAccepted = settings.QualcommQnnLicenseAccepted,
            InstallHint = snapshot.InstallHint,
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonOptions.Default)).ConfigureAwait(false);
        return snapshot.IsReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    public static async Task<int> InstallAsync(
        TrackdubSessionFactory factory,
        bool acceptLicense,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        IQnnCatalogEpInstaller installer = factory.GetRequiredService<IQnnCatalogEpInstaller>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool licenseAccepted = settings.QualcommQnnLicenseAccepted;
        if (acceptLicense && !licenseAccepted)
        {
            await HeadlessPersistedEpLicenses
                .PersistQualcommQnnAcceptanceAsync(
                    factory.GetRequiredService<IAppStoragePaths>(),
                    cancellationToken)
                .ConfigureAwait(false);
            licenseAccepted = true;
            await progressOutput.WriteLineAsync(
                    $"Accepted Qualcomm QNN license flag in studio settings. Reference: {LicenseReference}")
                .ConfigureAwait(false);
        }

        if (!licenseAccepted)
        {
            var blocked = new QnnInstallOutput
            {
                Succeeded = false,
                FailureDetail =
                    "Qualcomm QNN license not accepted. Pass --accept-license after reviewing the license terms, or accept in Model Manager.",
                LicenseReference = LicenseReference,
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, CliJsonOptions.Default))
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        var progress = new Progress<string>(message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                progressOutput.WriteLine(message);
            }
        });

        WinMlCatalogEpInstallResult result = await installer
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        // Installing registers the EP in this process, so the cached pre-install readiness snapshot
        // would hide it; clear the cache so the verification probe below re-runs for real.
        if (factory.GetRequiredService<IQnnCatalogReadinessProbe>() is IReadinessProbeCache probeCache)
        {
            probeCache.Invalidate();
        }

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await factory
            .GetRequiredService<IQnnCatalogRuntimeReadinessService>()
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        var payload = new QnnInstallOutput
        {
            Succeeded = result.Succeeded,
            FailureDetail = result.FailureDetail,
            Ready = snapshot.IsReady,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            LicenseReference = LicenseReference,
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonOptions.Default))
            .ConfigureAwait(false);

        return result.Succeeded && snapshot.IsReady
            ? Program.ExitSuccess
            : Program.ExitPipelineFailure;
    }

    private sealed class QnnStatusOutput
    {
        public bool Ready { get; init; }
        public bool SupportedPlatform { get; init; }
        public string? ProviderId { get; init; }
        public string? Route { get; init; }
        public string? StatusLabel { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsHardwareEligible { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public bool IsRegisteredWithOrt { get; init; }
        public bool CanInstallWinMlProvider { get; init; }
        public bool LicenseAccepted { get; init; }
        public string? InstallHint { get; init; }
    }

    private sealed class QnnInstallOutput
    {
        public bool Succeeded { get; init; }
        public bool Ready { get; init; }
        public string? FailureDetail { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public string? LicenseReference { get; init; }
    }
}
