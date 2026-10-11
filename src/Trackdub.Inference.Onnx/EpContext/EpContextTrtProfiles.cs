using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.Madlad;
using Trackdub.Inference.Onnx.NemotronAsr;
using Trackdub.Inference.Onnx.SortFormer;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Resolves the TensorRT optimization profile a production session passes for a given graph, so
/// EP-context AOT builds the same engine. Without it TRT-RTX assumes a fully dynamic range, which
/// some graphs (SortFormer streaming) cannot satisfy, and CompileModel() emits no engine.
/// </summary>
internal static class EpContextTrtProfiles
{
    public static IReadOnlyDictionary<string, string>? Resolve(string sourceModelPath)
    {
        if (MadladTrtProfiles.Resolve(sourceModelPath) is { } madladProfile)
        {
            return madladProfile;
        }

        IReadOnlyCollection<string> inputNames;
        try
        {
            // Metadata only: a CPU session never touches the TRT plugin.
            using InferenceSession session = new(sourceModelPath);
            inputNames = session.InputMetadata.Keys.ToArray();
        }
        catch (OnnxRuntimeException)
        {
            return null;
        }

        return Resolve(inputNames);
    }

    internal static IReadOnlyDictionary<string, string>? Resolve(IReadOnlyCollection<string> inputNames)
    {
        var names = new HashSet<string>(inputNames, StringComparer.Ordinal);
        if (SortFormerDiarizationEngine.IsStreamingExportInputSet(names))
        {
            return SortFormerDiarizationEngine.TrtOptions;
        }

        if (names.Contains("processed_signal") && names.Contains("cache_last_channel"))
        {
            return NemotronAsrEncoderTrtProfiles.BuildOptions(hasPromptInput: names.Contains("prompt_index"));
        }

        return null;
    }
}
