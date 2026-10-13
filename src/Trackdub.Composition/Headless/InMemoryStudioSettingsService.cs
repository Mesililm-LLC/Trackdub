using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Composition.Headless;

/// <summary>
/// In-memory implementation of <see cref="IStudioSettingsService"/> for headless usage.
/// Stores all mutations in memory without file I/O.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class InMemoryStudioSettingsService : IStudioSettingsService
{
    private const int RecentProjectLimit = 10;

    /// <summary>
    /// Non-interactive acceptance of the NVIDIA TensorRT RTX license for headless hosts such as
    /// containers. Set to 1/true/yes only after reviewing the license terms.
    /// </summary>
    public const string AcceptNvidiaTensorRtRtxLicenseEnvironmentVariable =
        "TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE";

    private readonly object _lock = new();
    private StudioSettings _settings;

    public InMemoryStudioSettingsService(
        HeadlessTrackdubOptions options,
        StudioSettings? persistedSettings = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool licenseAcceptedByEnvironment = IsTruthy(
            (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(
                AcceptNvidiaTensorRtRtxLicenseEnvironmentVariable));

        StudioSettings persisted = persistedSettings ?? StudioSettings.Default;
        IReadOnlyDictionary<string, ExecutionProviderKind> effectiveHardwareOverrides =
            options.HardwareOverrides is { Count: > 0 }
                ? new Dictionary<string, ExecutionProviderKind>(options.HardwareOverrides)
                : persisted.HardwareOverrides is { Count: > 0 }
                    ? new Dictionary<string, ExecutionProviderKind>(persisted.HardwareOverrides)
                    : new Dictionary<string, ExecutionProviderKind>();
        _settings = StudioSettings.Default with
        {
            HardwareOverrides = effectiveHardwareOverrides,
            // Process-local CLI pin wins; otherwise honor desktop settings.json hardware prefs.
            RequirePreferredExecutionProviders =
                options.RequirePreferredExecutionProviders
                || (options.HardwareOverrides is null && persisted.RequirePreferredExecutionProviders),
            WindowsMlExecutionDevicePolicy = options.WindowsMlExecutionDevicePolicy,
            NvidiaTensorRtRtxLicenseAccepted =
                options.NvidiaTensorRtRtxLicenseAccepted
                || persisted.NvidiaTensorRtRtxLicenseAccepted
                || licenseAcceptedByEnvironment,
            AmdRyzenAiLicenseAccepted = persisted.AmdRyzenAiLicenseAccepted,
            IntelOpenVinoLicenseAccepted = persisted.IntelOpenVinoLicenseAccepted,
            QualcommQnnLicenseAccepted = persisted.QualcommQnnLicenseAccepted,
            TensorRtRtxPluginDirectory =
                options.TensorRtRtxPluginDirectory ?? persisted.TensorRtRtxPluginDirectory,
            // Preserve host/user timing prefs so CLI/SDK runs honor settings.json.
            TtsTiming = persisted.TtsTiming ?? StudioSettings.Default.TtsTiming,
            TtsMaxConcurrency = persisted.TtsMaxConcurrency,
            // Preserve AFX preferences from desktop settings.json for headless CLI/SDK runs.
            EnableNvidiaAfx = persisted.EnableNvidiaAfx,
            NvidiaAfxProfile = persisted.NvidiaAfxProfile,
            NvidiaAfxIntensityRatio = persisted.NvidiaAfxIntensityRatio,
            NvidiaAfxLicenseAccepted = persisted.NvidiaAfxLicenseAccepted,
            NvidiaAfxRuntimeDirectory = persisted.NvidiaAfxRuntimeDirectory,
        };
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    public Task<StudioSettings> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings);
        }
    }

    public Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    public Task<StudioSettings> TouchRecentProjectAsync(
        string projectPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        lock (_lock)
        {
            string normalizedPath = Path.GetFullPath(projectPath);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RecentProjectEntry entry = new(projectName.Trim(), normalizedPath, now);

            RecentProjectEntry[] updatedRecentProjects =
            [
                entry,
                .. _settings.RecentProjects
                    .Where(candidate => !string.Equals(candidate.ProjectPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => candidate.LastOpenedAtUtc)
                    .Take(RecentProjectLimit - 1)
            ];

            _settings = _settings with { RecentProjects = updatedRecentProjects };
            return Task.FromResult(_settings);
        }
    }
}
