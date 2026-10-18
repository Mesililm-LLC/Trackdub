using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Runtime.Planning;

public sealed record StageRuntimeRequirements(
    RuntimeStage Stage,
    ModelTask RequiredTask,
    IReadOnlyList<string> PreferredModelAliases,
    IReadOnlyList<ExecutionProviderKind> AllowedProvidersThisMilestone,
    IReadOnlyList<string> PreferredGpuVariants,
    IReadOnlyList<string> PreferredCpuVariants,
    IReadOnlyDictionary<string, IReadOnlyList<ExecutionProviderKind>>? AllowedProvidersByEngineFamily = null,
    IReadOnlyList<string>? AllowedEngineFamilies = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    bool PreferTopRankedModelUntilReady = false);

internal static class Milestone5PlanningPolicy
{
    public static IReadOnlyList<ExecutionProviderKind> SupportedProvidersThisMilestone { get; } =
    [
        ExecutionProviderKind.TensorRTRtx,
        ExecutionProviderKind.Migraphx,
        ExecutionProviderKind.OpenVinoCatalog,
        ExecutionProviderKind.Qnn,
        ExecutionProviderKind.VitisAi,
        ExecutionProviderKind.TensorRt,
        ExecutionProviderKind.Cuda,
        ExecutionProviderKind.OpenVino,
        ExecutionProviderKind.CoreMl,
        ExecutionProviderKind.DirectMl,
        ExecutionProviderKind.Dnnl,
        ExecutionProviderKind.Cpu
    ];
}

internal static class StageRuntimeRequirementsCatalog
{
    private static IReadOnlyList<ExecutionProviderKind> DefaultOnnxStageAllowedProviders =>
        Milestone5PlanningPolicy.SupportedProvidersThisMilestone;

    private static IReadOnlyList<ExecutionProviderKind> WithoutTensorRtFamilies(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [.. providers.Where(static p => p is not ExecutionProviderKind.TensorRTRtx and not ExecutionProviderKind.TensorRt)];

    // Approved-export policy for the bundled GenAI graphs, not a platform ban on DirectML or
    // TensorRT. Dated observation (GenAI 0.17.1 on Windows ML 2.4, CPU/CUDA-targeted exports):
    // the bundled Whisper export segfaults in the encoder pass on DirectML, and the bundled
    // Qwen2.5 export fails its first DirectML kernel (0x80070057). NvTensorRtRtx has terminated
    // the host on these same exports. Keep the guard until an export built for that provider is
    // retested in an isolated process. Do not treat the observation as a provider-wide restriction.
    private static IReadOnlyList<ExecutionProviderKind> GenAiProviders(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [.. WithoutTensorRtFamilies(providers).Where(static p => p is not ExecutionProviderKind.DirectMl)];

    private static IReadOnlyList<ExecutionProviderKind> CpuFirst(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [ExecutionProviderKind.Cpu, .. providers.Where(static p => p is not ExecutionProviderKind.Cpu)];

    private static IReadOnlyList<ExecutionProviderKind> WithoutTensorRtRtx(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [.. providers.Where(static p => p is not ExecutionProviderKind.TensorRTRtx)];

    private static IReadOnlyList<ExecutionProviderKind> PreferDirectMl(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [ExecutionProviderKind.DirectMl, .. providers.Where(static p => p != ExecutionProviderKind.DirectMl)];

    public static IReadOnlyDictionary<RuntimeStage, StageRuntimeRequirements> All { get; } =
        new Dictionary<RuntimeStage, StageRuntimeRequirements>
        {
            [RuntimeStage.Vad] = new(
                RuntimeStage.Vad,
                ModelTask.Vad,
                ["silero-vad", "silero"],
                // TensorRT RTX is excluded for VAD. silero-vad's If/else-branch subgraph fails
                // the TensorRT RTX build on every attempt (EP ABI 0.4.2/cu13, TensorRT-RTX 1.6.1:
                // "squeeze index (1) must be less than length (0)", then IConditionalOutputLayer rank
                // mismatch). Session-init fallback recovers it to DirectML, but each run paid a failed
                // engine build and emitted native stderr that was misattributed to TTS in #329.
                // The model is tiny, so DirectML/CPU cost nothing meaningful. Classic TensorRT is a
                // separate provider with no evidence of the failure, so it stays allowed.
                WithoutTensorRtRtx(DefaultOnnxStageAllowedProviders),
                ["fp16", "q4f16"],
                ["int8", "quantized", "uint8", "q4"]),
            [RuntimeStage.Asr] = new(
                RuntimeStage.Asr,
                ModelTask.Asr,
                [
                    "parakeet-tdt-0.6b-v3",
                    "qwen3-asr-0.6b",
                    "qwen3-asr-balanced",
                    "qwen3-asr-1.7b",
                    "qwen3-asr-quality",
                    "whisper-tiny-onnx",
                    "whisper-tiny",
                    "whisper-tiny-local",
                    "whisper-tiny-genai",
                ],
                DefaultOnnxStageAllowedProviders,
                ["default", "fp16"],
                ["default", "int8", "quantized", "uint8", "q4"],
                // Parakeet TDT 0.6B v3 is the default ASR POC model: the bundled ONNX encoder and
                // decoder passed the live Linux TRT-RTX smoke and cache-warm tests on an RTX 5070.
                // Qwen3 remains available but its bundled TRT graph falls back, so prefer DML (or
                // CUDA where usable) ahead of TRT; Whisper's stock ONNX export is denied TRT below.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["qwen3-asr"] = PreferDirectMl(DefaultOnnxStageAllowedProviders),
                    ["whisper-onnx"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["whisper-genai"] = GenAiProviders(DefaultOnnxStageAllowedProviders),
                },
                // Nemotron ASR is not in the shipping auto-planning lane: quality-tier ranking
                // previously selected it ahead of working ONNX models and produced empty
                // transcripts. Explicit Nemotron override still resolves by alias if needed.
                AllowedEngineFamilies: ["parakeet-tdt", "qwen3-asr", "whisper-onnx", "whisper-genai"]),
            [RuntimeStage.Translation] = new(
                RuntimeStage.Translation,
                ModelTask.Translation,
                ["opus-en-es", "helsinki-opus-en-es", "opus-en-fr", "opus-en-de", "opus-en-it", "opus-en-pt", "opus-es-en", "helsinki-opus-es-en", "madlad400-mt", "madlad400"],
                DefaultOnnxStageAllowedProviders,
                ["trt-fp16", "int4-kv", "merged-decoder", "quantized", "fp16"],
                ["int4-kv", "merged-decoder", "quantized", "int8", "fp16"],
                // Encoder-decoder InferenceSession ctor stack-overflows under TensorRT RTX (ORT 1.24.5).
                // phi-genai loads through ORT GenAI, whose NvTensorRtRtx device can terminate
                // the process (native stack overflow) during model init/generation.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["opus-mt"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    // MADLAD-400 3B: TensorRT RTX runs the trt-fp16 variant (the only one listing trt-rtx)
                    // with bucketed static shapes, ~19x faster than the int4 KV-cache decoder on CPU at fp16
                    // quality. Otherwise CPU-first: the int4 decoder translates a warm clip in ~8 s on CPU,
                    // while DirectML pages on a 12 GB card (int4 81-89 s, fp16 160+ s) because the model and
                    // DirectML's working set do not fit beside the desktop. DirectML stays reachable through
                    // an explicit provider pin.
                    ["madlad"] = [ExecutionProviderKind.TensorRTRtx, .. CpuFirst(WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders))],
                    ["phi-genai"] = GenAiProviders(DefaultOnnxStageAllowedProviders),
                }),
            [RuntimeStage.Diarization] = new(
                RuntimeStage.Diarization,
                ModelTask.Diarization,
                ["diar-streaming-sortformer-4spk-v2.1", "sortformer-diarizer-4spk-v2.1", "sortformer-4spk", "nvidia-streaming-sortformer-4spk-v2.1"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"]),
            [RuntimeStage.Separation] = new(
                RuntimeStage.Separation,
                ModelTask.Separation,
                ["spleeter"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["spleeter"],
                PreferTopRankedModelUntilReady: true),
            [RuntimeStage.OverlapRescue] = new(
                RuntimeStage.OverlapRescue,
                ModelTask.OverlapRescue,
                ["sepformer"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["sepformer"]),
            [RuntimeStage.SpeechEnhancement] = new(
                RuntimeStage.SpeechEnhancement,
                ModelTask.SpeechEnhancement,
                ["deepfilternet3", "deepfilter"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["deepfilternet3"]),
            [RuntimeStage.Tts] = new(
                RuntimeStage.Tts,
                ModelTask.Tts,
                ["kokoro-onnx", "kokoro", "chatterbox-turbo-onnx", "chatterbox-turbo", "chatterbox-onnx", "chatterbox",
                    "cosyvoice-300m", "cosyvoice",
                    "qwen3-tts-0.6b-customvoice", "qwen3-tts-0.6b", "qwen3-tts", "qwen-tts",
                    "qwen3-tts-1.7b-customvoice", "qwen3-tts-1.7b",
                    "qwen3-tts-0.6b-base", "qwen3-tts-1.7b-base"],
                DefaultOnnxStageAllowedProviders,
                ["int8", "q4f16", "fp16", "q4", "default", "int4"],
                ["int8", "q4", "quantized", "default"],
                // Per-graph TrtRtxUnsupportedOpScanner + session-init fallback isolate failures.
                // CosyVoice multi-graph packages compile under TRT RTX in micro-benchmarks
                // (token_generator 788MB, text_encoder, speech_tokenizer); the family-level
                // deny was over-broad. kokoro runs on CUDA or CPU only: DirectML fails its decoder
                // ConvTranspose (0x80070057) and TensorRT RTX cannot infer its duration-dependent
                // shapes; on Windows CUDA comes from the ORT 1.30 inference worker (ADR-0017).
                // chatterbox/qwen3-tts keep the deny until their multi-graph init is smoke-clean.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kokoro"] = [ExecutionProviderKind.Cuda, ExecutionProviderKind.Cpu],
                    ["chatterbox"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["qwen3-tts"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                },
                AllowedEngineFamilies: ["kokoro", "chatterbox", "cosyvoice", "qwen3-tts"]),
            [RuntimeStage.LipSync] = new(
                RuntimeStage.LipSync,
                ModelTask.ForcedAlignment,
                ["wav2vec2-lv60-espeak", "wav2vec2-lv60-espeak-cv-ft-onnx"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["performance", "default"],
                AllowedEngineFamilies: ["onnx-ctc-phoneme-aligner"],
                PreferTopRankedModelUntilReady: true),
            [RuntimeStage.TextRefinement] = new(
                RuntimeStage.TextRefinement,
                ModelTask.TextRefinement,
                ["qwen2.5-1.5b-instruct", "qwen-polisher", "text-refiner"],
                DefaultOnnxStageAllowedProviders,
                ["default", "fp16"],
                ["default", "int8", "quantized"],
                // ORT GenAI NvTensorRtRtx terminates the process (native stack overflow) when
                // loading/running bundled GenAI models, e.g. qwen-instruct (Qwen2.5-1.5B). A
                // smoke failure cannot gate a fatal crash, so GenAI families never see TRT RTX.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["qwen-instruct"] = GenAiProviders(DefaultOnnxStageAllowedProviders),
                    ["phi-genai"] = GenAiProviders(DefaultOnnxStageAllowedProviders),
                }),
            [RuntimeStage.LipSynthesis] = new(
                RuntimeStage.LipSynthesis,
                ModelTask.LipSynthesis,
                ["latentsync-1.6", "latentsync"],
                DefaultOnnxStageAllowedProviders,
                ["fp16", "default"],
                ["int8", "default"],
                // LatentSync UNet exports MultiHeadAttention, which TensorRT RTX cannot import.
                AllowedProvidersByEngineFamily: new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["latentsync-diffusion"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                },
                AllowedEngineFamilies: ["latentsync-diffusion"]),
        };
}
