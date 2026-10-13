using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Sdk.Tests;

public sealed class InMemoryStudioSettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_returns_configured_hardware_overrides()
    {
        var service = new InMemoryStudioSettingsService(new HeadlessTrackdubOptions
        {
            HardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["Vad"] = ExecutionProviderKind.DirectMl,
                ["AsrGenAi"] = ExecutionProviderKind.DirectMl,
                ["AsrOnnxRuntime"] = ExecutionProviderKind.DirectMl,
                ["AsrNemotron"] = ExecutionProviderKind.DirectMl,
                ["Separation"] = ExecutionProviderKind.DirectMl,
                ["Diarization"] = ExecutionProviderKind.DirectMl,
                ["Translation"] = ExecutionProviderKind.DirectMl,
                ["Tts"] = ExecutionProviderKind.DirectMl,
                ["LipSync"] = ExecutionProviderKind.DirectMl,
                ["LipSynthesis"] = ExecutionProviderKind.DirectMl,
            },
        });

        StudioSettings settings = await service.LoadAsync(CancellationToken.None);

        Assert.NotNull(settings.HardwareOverrides);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides!["AsrNemotron"]);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides["LipSync"]);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides["LipSynthesis"]);
    }

    [Fact]
    public async Task LoadAsync_seeds_nvidia_trt_rtx_license_from_persisted_settings()
    {
        var service = new InMemoryStudioSettingsService(
            new HeadlessTrackdubOptions(),
            StudioSettings.Default with { NvidiaTensorRtRtxLicenseAccepted = true });

        StudioSettings settings = await service.LoadAsync(CancellationToken.None);

        Assert.True(settings.NvidiaTensorRtRtxLicenseAccepted);
        Assert.Empty(settings.HardwareOverrides!);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task LoadAsync_accepts_nvidia_trt_rtx_license_from_environment(string? value, bool expected)
    {
        var service = new InMemoryStudioSettingsService(
            new HeadlessTrackdubOptions(),
            persistedSettings: null,
            getEnvironmentVariable: name =>
                name == InMemoryStudioSettingsService.AcceptNvidiaTensorRtRtxLicenseEnvironmentVariable ? value : null);

        StudioSettings settings = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(expected, settings.NvidiaTensorRtRtxLicenseAccepted);
    }

    [Fact]
    public async Task LoadAsync_preserves_nvidia_afx_settings_from_persisted_settings()
    {
        var service = new InMemoryStudioSettingsService(
            new HeadlessTrackdubOptions(),
            StudioSettings.Default with
            {
                EnableNvidiaAfx = true,
                NvidiaAfxProfile = NvidiaAfxProfile.TelephonyUpscale,
                NvidiaAfxIntensityRatio = 0.75f,
                NvidiaAfxLicenseAccepted = true,
                NvidiaAfxRuntimeDirectory = @"C:\afx-runtime",
            });

        StudioSettings settings = await service.LoadAsync(CancellationToken.None);

        Assert.True(settings.EnableNvidiaAfx);
        Assert.Equal(NvidiaAfxProfile.TelephonyUpscale, settings.NvidiaAfxProfile);
        Assert.Equal(0.75f, settings.NvidiaAfxIntensityRatio);
        Assert.True(settings.NvidiaAfxLicenseAccepted);
        Assert.Equal(@"C:\afx-runtime", settings.NvidiaAfxRuntimeDirectory);
    }
}
