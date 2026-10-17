using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal static class NemotronAsrEncoderTrtProfiles
{
    // Shapes must match the bundled encoder.onnx inputs: processed_signal is mel-major
    // [B,128,T=65] and caches are layers-first ([24,1,56,1024] / [24,1,1024,8]).
    private const string ProfileWithoutPromptIndex =
        "processed_signal:1x128x65,processed_signal_length:1,cache_last_channel:24x1x56x1024,cache_last_time:24x1x1024x8,cache_last_channel_len:1";

    private const string PromptIndexSuffix = ",prompt_index:1";

    private static readonly ConcurrentDictionary<string, bool> PromptInputCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> BuildOptions(string encoderModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderModelPath);
        return BuildOptions(EncoderHasPromptInput(encoderModelPath));
    }

    internal static IReadOnlyDictionary<string, string> BuildOptions(bool hasPromptInput)
    {
        string shapes = hasPromptInput
            ? ProfileWithoutPromptIndex + PromptIndexSuffix
            : ProfileWithoutPromptIndex;

        return new Dictionary<string, string>
        {
            // Bundled Nemotron export expects mel-major [B,mel,T]; TRT profiles must match encoder inputs.
            ["trt_profile_min_shapes"] = shapes,
            ["trt_profile_max_shapes"] = shapes,
            ["trt_profile_opt_shapes"] = shapes,
        };
    }

    // Profile options deliberately stay free of enable_cuda_graph: EpContextTrtProfiles.Resolve
    // hands this dict to the benchmark path where the session flag must follow the benchmark's own
    // cuda-graph mode. Production callers compose the flag on top via
    // OnnxExecutionSessionFactory.WithCudaGraphsEnabled so their sessions are graph-enabled
    // (NemotronAsrGreedyDecoder binds fixed-address buffers).
    //
    // decoder_joint inputs are all fixed-shape, so it needs no profile; the graph flag is the
    // only option the bound decoder path requires. Shared between the engine and the smoke tester
    // so both hit the same pool-key fingerprint.
    internal static readonly IReadOnlyDictionary<string, string> DecoderJointCudaGraphOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enable_cuda_graph"] = "1",
        };

    internal static bool EncoderHasPromptInput(string encoderModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderModelPath);
        return PromptInputCache.GetOrAdd(encoderModelPath, static path =>
        {
            using InferenceSession session = new(path);
            return session.InputMetadata.ContainsKey("prompt_index");
        });
    }
}
