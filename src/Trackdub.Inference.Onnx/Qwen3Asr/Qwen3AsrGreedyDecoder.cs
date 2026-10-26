using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrGreedyDecoder
{
    public static IReadOnlyList<int> Decode(
        OnnxExecutionSessionFactory.Qwen3AsrSessionLease sessionLease,
        Qwen3AsrEmbedTokens embedTokens,
        Tensor<float> audioFeatures,
        IReadOnlyList<int> promptIds,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionLease);
        ArgumentNullException.ThrowIfNull(embedTokens);
        ArgumentNullException.ThrowIfNull(audioFeatures);
        ArgumentNullException.ThrowIfNull(promptIds);

        bool usesInputIds = sessionLease.DecoderInitSession.InputMetadata.ContainsKey("input_ids");
        int promptLength = promptIds.Count;
        var positionIds = new DenseTensor<long>(
            Enumerable.Range(0, promptLength).Select(static id => (long)id).ToArray(),
            [1, promptLength]);

        IReadOnlyList<NamedOnnxValue> kvState;
        Tensor<float> logits;
        using (Qwen3AsrInputSet initInputs = usesInputIds
                   ? BuildDecoderInitInputIds(audioFeatures, promptIds, positionIds)
                   : BuildDecoderInitInputEmbeds(embedTokens, audioFeatures, promptIds, positionIds))
        using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> initResults =
               sessionLease.DecoderInitSession.RunWithRetry(initInputs.Values, cancellationToken: cancellationToken))
        {
            logits = ExtractLogits(initResults);
            kvState = CloneKvState(initResults);
        }

        int nextToken = ArgMax(logits, logits.Dimensions[1] - 1);
        var outputTokens = new List<int> { nextToken };
        if (IsEos(nextToken))
        {
            DisposeKvState(kvState);
            return outputTokens;
        }

        int position = promptLength;
        // Reused per step: the step inputs built from it are consumed (and their
        // results cloned) before the next lookup overwrites it.
        var tokenEmbedding = new float[embedTokens.HiddenSize];
        try
        {
            for (int step = 1; step < maxTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                embedTokens.Lookup(nextToken, tokenEmbedding.AsSpan());
                using Qwen3AsrInputSet stepInputs = BuildDecoderStepInputs(tokenEmbedding, position, kvState);
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> stepResults =
                    sessionLease.DecoderStepSession.RunWithRetry(stepInputs.Values, cancellationToken: cancellationToken);

                DisposeKvState(kvState);
                kvState = CloneKvState(stepResults);
                logits = ExtractLogits(stepResults);
                nextToken = ArgMax(logits, logits.Dimensions[1] - 1);
                outputTokens.Add(nextToken);
                position++;
                if (IsEos(nextToken))
                {
                    break;
                }
            }
        }
        finally
        {
            DisposeKvState(kvState);
        }

        return outputTokens;
    }

    internal static void RunSmokeInitAndStep(
        OnnxExecutionSessionFactory.Qwen3AsrSessionLease sessionLease,
        Tensor<float> audioFeatures,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionLease);
        ArgumentNullException.ThrowIfNull(audioFeatures);
        if (audioFeatures.Dimensions.Length < 3)
        {
            throw new InvalidOperationException(
                $"Qwen3-ASR encoder smoke output rank {audioFeatures.Dimensions.Length} is below 3.");
        }

        int audioLen = audioFeatures.Dimensions[1];
        int hiddenSize = audioFeatures.Dimensions[2];
        if (audioLen <= 0 || hiddenSize <= 0)
        {
            throw new InvalidOperationException(
                $"Qwen3-ASR encoder smoke output shape [{audioFeatures.Dimensions[0]}, {audioFeatures.Dimensions[1]}, {audioFeatures.Dimensions[2]}] is not runnable.");
        }

        IReadOnlyList<int> promptIds = Qwen3AsrPromptBuilder.BuildPromptIds(audioLen);
        var positionIds = new DenseTensor<long>(
            Enumerable.Range(0, promptIds.Count).Select(static id => (long)id).ToArray(),
            [1, promptIds.Count]);

        IReadOnlyList<NamedOnnxValue> kvState;
        using (Qwen3AsrInputSet initInputs = sessionLease.DecoderInitSession.InputMetadata.ContainsKey("input_ids")
                   ? BuildDecoderInitInputIds(audioFeatures, promptIds, positionIds)
                   : BuildDecoderInitSmokeEmbeds(audioFeatures, promptIds, positionIds, hiddenSize))
        using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> initResults =
               sessionLease.DecoderInitSession.RunWithRetry(
                   initInputs.Values, maxAttempts: 1, cancellationToken: cancellationToken, provider: provider))
        {
            _ = ExtractLogits(initResults);
            kvState = CloneKvState(initResults);
        }

        try
        {
            using Qwen3AsrInputSet stepInputs = BuildDecoderStepInputs(new float[hiddenSize], promptIds.Count, kvState);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ =
                sessionLease.DecoderStepSession.RunWithRetry(
                    stepInputs.Values, maxAttempts: 1, cancellationToken: cancellationToken, provider: provider);
        }
        finally
        {
            DisposeKvState(kvState);
        }
    }

    private static Qwen3AsrInputSet BuildDecoderInitSmokeEmbeds(
        Tensor<float> audioFeatures,
        IReadOnlyList<int> promptIds,
        DenseTensor<long> positionIds,
        int hiddenSize)
    {
        (int audioStart, int audioEnd) = Qwen3AsrPromptBuilder.GetAudioPadRange(promptIds);
        int audioLen = audioEnd - audioStart;
        if (audioFeatures.Dimensions[1] != audioLen)
        {
            throw new InvalidOperationException(
                $"Audio feature length {audioFeatures.Dimensions[1]} does not match audio_pad count {audioLen}.");
        }

        var embedBuffer = new float[promptIds.Count * hiddenSize];
        for (int tokenIndex = audioStart; tokenIndex < audioEnd; tokenIndex++)
        {
            for (int hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                embedBuffer[(tokenIndex * hiddenSize) + hiddenIndex] =
                    audioFeatures[0, tokenIndex - audioStart, hiddenIndex];
            }
        }

        return new Qwen3AsrInputSet(
        [
            NamedOnnxValue.CreateFromTensor("input_embeds", new DenseTensor<float>(embedBuffer, [1, promptIds.Count, hiddenSize])),
            NamedOnnxValue.CreateFromTensor("position_ids", positionIds),
        ]);
    }

    private static Qwen3AsrInputSet BuildDecoderInitInputIds(
        Tensor<float> audioFeatures,
        IReadOnlyList<int> promptIds,
        DenseTensor<long> positionIds)
    {
        (int audioStart, _) = Qwen3AsrPromptBuilder.GetAudioPadRange(promptIds);
        long[] promptArray = promptIds.Select(static token => (long)token).ToArray();
        var inputIds = new DenseTensor<long>(promptArray, [1, promptArray.Length]);
        var audioOffset = new DenseTensor<long>(new long[] { audioStart }, [1]);

        return new Qwen3AsrInputSet(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("position_ids", positionIds),
            NamedOnnxValue.CreateFromTensor("audio_features", audioFeatures),
            NamedOnnxValue.CreateFromTensor("audio_offset", audioOffset),
        ]);
    }

    private static Qwen3AsrInputSet BuildDecoderInitInputEmbeds(
        Qwen3AsrEmbedTokens embedTokens,
        Tensor<float> audioFeatures,
        IReadOnlyList<int> promptIds,
        DenseTensor<long> positionIds)
    {
        (int audioStart, int audioEnd) = Qwen3AsrPromptBuilder.GetAudioPadRange(promptIds);
        int audioLen = audioEnd - audioStart;
        if (audioFeatures.Dimensions[1] != audioLen)
        {
            throw new InvalidOperationException(
                $"Audio feature length {audioFeatures.Dimensions[1]} does not match audio_pad count {audioLen}.");
        }

        int hiddenSize = embedTokens.HiddenSize;
        var embedBuffer = new float[promptIds.Count * hiddenSize];
        for (int tokenIndex = 0; tokenIndex < promptIds.Count; tokenIndex++)
        {
            embedTokens.Lookup(
                promptIds[tokenIndex],
                embedBuffer.AsSpan(tokenIndex * hiddenSize, hiddenSize));
        }

        var inputEmbeds = new DenseTensor<float>(embedBuffer, [1, promptIds.Count, hiddenSize]);
        for (int tokenIndex = audioStart; tokenIndex < audioEnd; tokenIndex++)
        {
            for (int hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                inputEmbeds[0, tokenIndex, hiddenIndex] = audioFeatures[0, tokenIndex - audioStart, hiddenIndex];
            }
        }

        return new Qwen3AsrInputSet(
        [
            NamedOnnxValue.CreateFromTensor("input_embeds", inputEmbeds),
            NamedOnnxValue.CreateFromTensor("position_ids", positionIds),
        ]);
    }

    private static Qwen3AsrInputSet BuildDecoderStepInputs(
        float[] tokenEmbedding,
        int position,
        IReadOnlyList<NamedOnnxValue> kvState)
    {
        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                "input_embeds",
                new DenseTensor<float>(tokenEmbedding, [1, 1, tokenEmbedding.Length])),
            NamedOnnxValue.CreateFromTensor(
                "position_ids",
                new DenseTensor<long>(new long[] { position }, [1, 1])),
        };

        foreach (NamedOnnxValue state in kvState)
        {
            string inputName = state.Name.Replace("present_", "past_", StringComparison.Ordinal);
            values.Add(CloneValueAsInput(inputName, state));
        }

        return new Qwen3AsrInputSet(values);
    }

    private static IReadOnlyList<NamedOnnxValue> CloneKvState(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results) =>
        results
            .Where(static result => !result.Name.Equals("logits", StringComparison.Ordinal))
            .Select(static result => CloneValueAsInput(result.Name, result))
            .ToArray();

    private static NamedOnnxValue CloneValueAsInput(string inputName, NamedOnnxValue value)
    {
        Tensor<float> tensor = value.AsTensor<float>();
        int[] dimensions = tensor.Dimensions.ToArray();
        var buffer = new float[tensor.Length];
        CopyTensorValues(tensor, buffer);
        return NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(buffer, dimensions));
    }

    private static void CopyTensorValues(Tensor<float> tensor, Span<float> destination)
    {
        if (tensor is DenseTensor<float> denseTensor)
        {
            denseTensor.Buffer.Span.CopyTo(destination);
            return;
        }

        int index = 0;
        foreach (float value in tensor)
        {
            destination[index++] = value;
        }
    }

    private static DenseTensor<float> ExtractLogits(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        Tensor<float> tensor = results.Single(static result => result.Name == "logits").AsTensor<float>();
        int[] dimensions = tensor.Dimensions.ToArray();
        var buffer = new float[tensor.Length];
        CopyTensorValues(tensor, buffer);
        return new DenseTensor<float>(buffer, dimensions);
    }

    private static void DisposeKvState(IReadOnlyList<NamedOnnxValue> kvState)
    {
        foreach (IDisposable value in kvState.OfType<IDisposable>())
        {
            value.Dispose();
        }
    }

    private static int ArgMax(Tensor<float> logits, int timeIndex)
    {
        int vocabularySize = logits.Dimensions[2];
        int bestToken = 0;
        float bestValue = float.NegativeInfinity;
        for (int tokenIndex = 0; tokenIndex < vocabularySize; tokenIndex++)
        {
            float value = logits[0, timeIndex, tokenIndex];
            if (value > bestValue)
            {
                bestValue = value;
                bestToken = tokenIndex;
            }
        }

        return bestToken;
    }

    private static bool IsEos(int tokenId) =>
        Array.IndexOf(Qwen3AsrPromptTokens.EosTokenIds, tokenId) >= 0;
}
