using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.FaceAnalysis;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Media.Extraction;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;

namespace Trackdub.Composition.LipSynthesis;

internal static class LatentSyncLipSynthesisRegistration
{
    /// <summary>
    /// Settings first, then environment variables. Settings that cannot be read fall back to the
    /// environment so a damaged settings file never stops a configured install from working.
    /// </summary>
    internal static NvidiaMaxineLipSyncOptions ResolveMaxineOptions(IStudioSettingsService? settingsService)
    {
        StudioSettings? settings = null;
        if (settingsService is not null)
        {
            try
            {
                settings = settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                settings = null;
            }
        }

        return NvidiaMaxineLipSyncOptions.Resolve(
            settings?.MaxineLipSyncHostPath,
            settings?.MaxineLipSyncArSdkRoot,
            settings?.MaxineLipSyncLanguage);
    }

    internal static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<FfmpegVideoFrameExtractor>();
        services.TryAddSingleton<IVideoFrameExtractor>(
            sp => sp.GetRequiredService<FfmpegVideoFrameExtractor>());
        services.TryAddSingleton<IVideoFrameAssembler>(
            sp => sp.GetRequiredService<FfmpegVideoFrameExtractor>());

        services.TryAddSingleton<IAudioSegmentExtractor, FfmpegAudioSegmentExtractor>();

        services.TryAddSingleton(sp =>
            new LatentSyncOnnxLipSynthesisEngine(
                sp.GetRequiredService<IRuntimePlanner>(),
                sp.GetRequiredService<BenchmarkModelPathResolver>(),
                sp.GetRequiredService<IVideoFrameExtractor>(),
                sp.GetRequiredService<IVideoFrameAssembler>(),
                sp.GetRequiredService<IAudioSegmentExtractor>(),
                sp.GetRequiredService<BundledModelManifestRegistry>()));
        services.TryAddSingleton(sp =>
        {
            IStudioSettingsService? settingsService = sp.GetService<IStudioSettingsService>();
            return new NvidiaMaxineLipSynthesisEngine(() => ResolveMaxineOptions(settingsService));
        });

        // Maxine first: it runs whenever a local AR SDK is configured; LatentSync stays the fallback.
        services.TryAddSingleton<ILipSynthesisEngine>(sp =>
            new PreferredLipSynthesisEngine(
            [
                sp.GetRequiredService<NvidiaMaxineLipSynthesisEngine>(),
                sp.GetRequiredService<LatentSyncOnnxLipSynthesisEngine>(),
            ]));

        services.TryAddSingleton<IFaceDetector>(sp =>
            new ScfrdOnnxFaceDetector(
                sp.GetRequiredService<BenchmarkModelPathResolver>(),
                sp.GetRequiredService<IVideoFrameExtractor>()));

        services.TryAddSingleton<GeometryLandmarkProvider>(sp =>
            new GeometryLandmarkProvider(
                sp.GetRequiredService<IFaceDetector>(),
                sp.GetRequiredService<IVideoFrameExtractor>(),
                sp.GetRequiredService<BenchmarkModelPathResolver>()));
        services.TryAddSingleton<IFaceLandmarkProvider>(
            sp => sp.GetRequiredService<GeometryLandmarkProvider>());

        services.TryAddSingleton<IFacePoseEstimator>(sp =>
            new PoseFromLandmarksEstimator(
                sp.GetRequiredService<IFaceLandmarkProvider>()));
    }
}
