using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.ParakeetTdt;

/// <summary>
/// Greedy Token-and-Duration Transducer decoding (NeMo <c>GreedyTDTInfer</c>, mirrored from the
/// parakeet-rs port of this export). The joint emits <c>vocab.Count</c> token logits (blank last)
/// followed by one logit per duration class; token and duration are chosen independently.
/// </summary>
internal sealed class ParakeetTdtGreedyDecoder(
    InferenceSession decoderJointSession,
    ParakeetTdtVocab vocab,
    ExecutionProviderKind? provider)
{
    private const int MaxSymbolsPerStep = 10;
    private const int HiddenDim = 1024;

    internal readonly record struct EmittedToken(int TokenId, int Frame, int Duration);

    public IReadOnlyList<EmittedToken> Decode(
        Tensor<float> encoded,
        int encodedLength,
        CancellationToken cancellationToken = default)
    {
        int frameCount = Math.Min(encodedLength, encoded.Dimensions[2]);
        int[] stateShape = ResolveStateShape();
        // Hoisted out of the decode loop: input element types cannot change for this session.
        TensorElementType targetsElementType = decoderJointSession.InputMetadata["targets"].ElementDataType;
        TensorElementType targetLengthElementType = decoderJointSession.InputMetadata["target_length"].ElementDataType;
        var state1 = new DenseTensor<float>(stateShape);
        var state2 = new DenseTensor<float>(stateShape);
        int lastToken = vocab.BlankId;
        var tokens = new List<EmittedToken>();
        int frame = 0;
        int symbolsAtFrame = 0;

        while (frame < frameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DenseTensor<float> encoderFrame = SliceFrame(encoded, frame);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = decoderJointSession.RunWithRetry(
                BuildInputs(encoderFrame, lastToken, state1, state2, targetsElementType, targetLengthElementType),
                cancellationToken: cancellationToken,
                provider: provider);

            float[] logits = results.Single(static value => value.Name == "outputs").AsTensor<float>().ToArray();
            int durationCount = logits.Length - vocab.Count;
            if (durationCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Parakeet joint emitted {logits.Length} logits for a {vocab.Count}-entry vocab; expected token + duration logits.");
            }

            int token = ArgMax(logits, 0, vocab.Count);
            // NeMo TDT durations for this family are [0, 1, ..., N-1] frames.
            int duration = ArgMax(logits, vocab.Count, durationCount);

            if (token != vocab.BlankId)
            {
                tokens.Add(new EmittedToken(token, frame, duration));
                lastToken = token;
                state1 = CloneTensor(results.Single(static value => value.Name == "output_states_1").AsTensor<float>());
                state2 = CloneTensor(results.Single(static value => value.Name == "output_states_2").AsTensor<float>());
                symbolsAtFrame++;
            }

            if (duration > 0)
            {
                frame += duration;
                symbolsAtFrame = 0;
            }
            else if (token == vocab.BlankId || symbolsAtFrame >= MaxSymbolsPerStep)
            {
                frame++;
                symbolsAtFrame = 0;
            }
        }

        return tokens;
    }

    private static List<NamedOnnxValue> BuildInputs(
        DenseTensor<float> encoderFrame,
        int lastToken,
        DenseTensor<float> state1,
        DenseTensor<float> state2,
        TensorElementType targetsElementType,
        TensorElementType targetLengthElementType) =>
    [
        NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderFrame),
        CreateIntegerInput("targets", targetsElementType, lastToken, [1, 1]),
        CreateIntegerInput("target_length", targetLengthElementType, 1, [1]),
        NamedOnnxValue.CreateFromTensor("input_states_1", state1),
        NamedOnnxValue.CreateFromTensor("input_states_2", state2),
    ];

    private static NamedOnnxValue CreateIntegerInput(string name, TensorElementType elementType, int value, int[] dimensions) =>
        elementType switch
        {
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(new[] { value }, dimensions)),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new long[] { value }, dimensions)),
            TensorElementType other => throw new NotSupportedException($"Parakeet decoder input '{name}' has unsupported type {other}."),
        };

    private int[] ResolveStateShape()
    {
        int[] dims = decoderJointSession.InputMetadata["input_states_1"].Dimensions;
        // [layers, batch, hidden]; batch is dynamic in the export.
        return [dims[0] > 0 ? dims[0] : 2, 1, dims[2] > 0 ? dims[2] : 640];
    }

    private static DenseTensor<float> SliceFrame(Tensor<float> encoded, int frame)
    {
        var data = new float[HiddenDim];
        for (int hidden = 0; hidden < HiddenDim; hidden++)
        {
            data[hidden] = encoded[0, hidden, frame];
        }

        return new DenseTensor<float>(data, [1, HiddenDim, 1]);
    }

    private static DenseTensor<float> CloneTensor(Tensor<float> tensor) =>
        new(tensor.ToArray(), tensor.Dimensions.ToArray());

    private static int ArgMax(float[] values, int offset, int count)
    {
        int best = 0;
        float bestValue = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            if (values[offset + index] > bestValue)
            {
                bestValue = values[offset + index];
                best = index;
            }
        }

        return best;
    }
}
