using System.Text;

namespace Trackdub.Inference.Onnx.Benchmarking;

/// <summary>Versioned, portable synthetic-v1 streams; semantic state and integer inputs stay separate.</summary>
internal static class BenchmarkInputGenerator
{
    internal static void Fill(
        Span<float> values, int seed, string graphRole, string tensorName, float scale = 1f)
    {
        ArgumentNullException.ThrowIfNull(graphRole);
        ArgumentNullException.ThrowIfNull(tensorName);
        if (!float.IsFinite(scale) || scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        uint state = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes($"{graphRole}/{tensorName}"))
        {
            state = unchecked((state ^ value) * 16777619);
        }

        state ^= unchecked((uint)seed);
        if (state == 0)
        {
            state = 0x6D2B79F5;
        }

        for (int index = 0; index < values.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            values[index] = (((state >> 8) / 8388608f) - 1f) * scale;
        }
    }
}
