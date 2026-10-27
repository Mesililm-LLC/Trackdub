using System.Buffers;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Models;

/// <summary>
/// Token sampling with partial selection. Top-k uses a bounded min-heap to find the
/// cutoff value (plus a stable index-order tie fill) instead of a full-vocabulary sort,
/// and top-p extracts candidates in descending order only until the nucleus mass is
/// reached. The final multinomial scan still walks token-index order over the same
/// softmax the full-sort implementation produced, so for the same random value the
/// selected token is identical.
/// </summary>
internal static class TokenSampling
{
    public static int Sample(ReadOnlySpan<float> logits, float temperature, int topK, float topP, float randomValue)
    {
        int length = logits.Length;
        float[] probs = ArrayPool<float>.Shared.Rent(length);
        try
        {
            logits.CopyTo(probs);
            Span<float> span = probs.AsSpan(0, length);

            if (temperature > 0)
            {
                for (int i = 0; i < length; i++)
                {
                    span[i] /= temperature;
                }
            }

            ApplyTopK(span, topK);
            ApplyTopP(span, topP);

            float maxLogit = float.NegativeInfinity;
            for (int i = 0; i < length; i++)
            {
                if (span[i] > maxLogit)
                {
                    maxLogit = span[i];
                }
            }

            double sumExp = 0;
            for (int i = 0; i < length; i++)
            {
                span[i] = MathF.Exp(span[i] - maxLogit);
                sumExp += span[i];
            }

            double cumSum = 0;
            for (int i = 0; i < length; i++)
            {
                cumSum += span[i] / sumExp;
                if (randomValue < cumSum)
                {
                    return i;
                }
            }

            return length - 1;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probs);
        }
    }

    /// <summary>
    /// Masks all but the top <paramref name="topK"/> logits. Ties at the cutoff keep the
    /// lowest token indices, matching a stable descending full sort.
    /// </summary>
    internal static void ApplyTopK(Span<float> logits, int topK)
    {
        if (topK <= 0 || topK >= logits.Length)
        {
            return;
        }

        float threshold = FindKthLargest(logits, topK);
        int greaterCount = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > threshold)
            {
                greaterCount++;
            }
        }

        int equalBudget = topK - greaterCount;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > threshold)
            {
                continue;
            }

            if (logits[i] == threshold && equalBudget > 0)
            {
                equalBudget--;
                continue;
            }

            logits[i] = float.NegativeInfinity;
        }
    }

    /// <summary>
    /// Masks logits outside the top-p nucleus. Candidates are extracted from a max-heap
    /// in descending order (lowest index first on ties) only until the cumulative
    /// softmax mass reaches <paramref name="topP"/>; the vocabulary is never sorted.
    /// </summary>
    internal static void ApplyTopP(Span<float> logits, float topP)
    {
        if (topP >= 1.0f || logits.Length == 0)
        {
            return;
        }

        float maxLogit = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > maxLogit)
            {
                maxLogit = logits[i];
            }
        }

        if (float.IsNegativeInfinity(maxLogit))
        {
            return;
        }

        double sumExp = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            sumExp += Math.Exp((double)logits[i] - maxLogit);
        }

        var heap = new PriorityQueue<int, (float Value, int Index)>(DescendingComparer);
        for (int i = 0; i < logits.Length; i++)
        {
            if (!float.IsNegativeInfinity(logits[i]))
            {
                heap.Enqueue(i, (logits[i], i));
            }
        }

        var keep = new bool[logits.Length];
        double cumulative = 0;
        while (heap.Count > 0)
        {
            int index = heap.Dequeue();
            keep[index] = true;
            cumulative += Math.Exp((double)logits[index] - maxLogit) / sumExp;
            if (cumulative >= topP)
            {
                break;
            }
        }

        for (int i = 0; i < logits.Length; i++)
        {
            if (!keep[i])
            {
                logits[i] = float.NegativeInfinity;
            }
        }
    }

    /// <summary>Returns the k-th largest value (1-based) using a bounded min-heap of size k.</summary>
    internal static float FindKthLargest(ReadOnlySpan<float> values, int k)
    {
        var heap = new PriorityQueue<float, float>();
        for (int i = 0; i < values.Length; i++)
        {
            if (heap.Count < k)
            {
                heap.Enqueue(values[i], values[i]);
            }
            else if (values[i] > heap.Peek())
            {
                heap.Dequeue();
                heap.Enqueue(values[i], values[i]);
            }
        }

        return heap.Peek();
    }

    // PriorityQueue dequeues the "smallest" priority; rank larger logits (and lower
    // indices on ties) as smaller so extraction is stable descending order.
    private static readonly IComparer<(float Value, int Index)> DescendingComparer =
        Comparer<(float Value, int Index)>.Create(static (x, y) =>
        {
            int byValue = y.Value.CompareTo(x.Value);
            return byValue != 0 ? byValue : x.Index.CompareTo(y.Index);
        });
}
