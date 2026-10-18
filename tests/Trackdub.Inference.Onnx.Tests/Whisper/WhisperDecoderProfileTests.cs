using Trackdub.Domain;
using Trackdub.Inference.Onnx.Whisper;

namespace Trackdub.Inference.Onnx.Tests.Whisper;

public sealed class WhisperDecoderProfileTests
{
    [Fact]
    public void DecoderWithAdditionalMask_KeepsShapeProfilesWithoutEnablingCudaGraphs()
    {
        // Synthetic ONNX Identity graph, IR 9 / opset 13. Inputs are int64 input_ids
        // [1,tokens], float encoder_hidden_states [1,frames,4], and int64
        // attention_mask [1,tokens]. Output is Identity(encoder_hidden_states).
        // No pretrained weights, accelerator, Python, or external files are required.
        byte[] model = Convert.FromBase64String(
            "CAk65wEKKQoVZW5jb2Rlcl9oaWRkZW5fc3RhdGVzEgZvdXRwdXQiCElkZW50aXR5Ehh3aGlzcGVyLXByb2ZpbGUtbWV0YWRhdGFaIQoJaW5wdXRfaWRzEhQKEggHEg4KAggBCggSBnRva2Vuc1oxChVlbmNvZGVyX2hpZGRlbl9zdGF0ZXMSGAoWCAESEgoCCAEKCBIGZnJhbWVzCgIIBFomCg5hdHRlbnRpb25fbWFzaxIUChIIBxIOCgIIAQoIEgZ0b2tlbnNiIgoGb3V0cHV0EhgKFggBEhIKAggBCggSBmZyYW1lcwoCCARCBAoAEA0=");
        string modelPath = Path.Join(Path.GetTempPath(), $"whisper-profile-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(modelPath, model);

            Dictionary<string, string> options = WhisperOnnxAudioTranscriptionEngine
                .BuildTrtDecoderOptions(modelPath);

            Assert.Equal("input_ids:1x1,encoder_hidden_states:1x1x4", options["trt_profile_min_shapes"]);
            Assert.Equal("input_ids:1x448,encoder_hidden_states:1x1500x4", options["trt_profile_opt_shapes"]);
            Assert.Equal(options["trt_profile_opt_shapes"], options["trt_profile_max_shapes"]);
            Assert.False(WhisperOnnxAudioTranscriptionEngine.ShouldEnableTrtRtxCudaGraphs(
                ExecutionProviderKind.TensorRTRtx,
                ["input_ids", "encoder_hidden_states", "attention_mask"]));
        }
        finally
        {
            File.Delete(modelPath);
        }
    }
}
