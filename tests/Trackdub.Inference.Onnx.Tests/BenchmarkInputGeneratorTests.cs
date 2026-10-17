using Trackdub.Inference.Onnx.Benchmarking;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class BenchmarkInputGeneratorTests
{
    [Fact]
    public void Fill_Seed42MatchesFrozenSyntheticV1Bits()
    {
        var values = new float[8];
        BenchmarkInputGenerator.Fill(values, 42, "single", "input");

        Assert.Equal(
            new[] { -1086824456, -1092696096, 1049553792, -1083230784, -1086548022, 1064427652, 1062043234, -1084630198 },
            values.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void Fill_IsDeterministicFiniteAndNonzero()
    {
        var first = new float[1024];
        var second = new float[1024];

        BenchmarkInputGenerator.Fill(first, 42, "single", "input");
        BenchmarkInputGenerator.Fill(second, 42, "single", "input");

        Assert.Equal(first, second);
        Assert.Contains(first, value => value < 0);
        Assert.Contains(first, value => value > 0);
        Assert.All(first, value =>
        {
            Assert.True(float.IsFinite(value));
            Assert.InRange(value, -1f, 1f);
        });
    }

    [Fact]
    public void Fill_DifferentSeedRoleAndInputNameProduceDifferentValues()
    {
        var original = new float[16];
        var differentSeed = new float[16];
        var differentRole = new float[16];
        var differentName = new float[16];
        BenchmarkInputGenerator.Fill(original, 42, "single", "input");
        BenchmarkInputGenerator.Fill(differentSeed, 43, "single", "input");
        BenchmarkInputGenerator.Fill(differentRole, 42, "encoder", "input");
        BenchmarkInputGenerator.Fill(differentName, 42, "single", "audio");

        Assert.False(original.SequenceEqual(differentSeed));
        Assert.False(original.SequenceEqual(differentRole));
        Assert.False(original.SequenceEqual(differentName));
    }

    [Fact]
    public void Fill_RespectsProfileScale()
    {
        var values = new float[128];
        BenchmarkInputGenerator.Fill(values, 42, "single", "input", 0.1f);
        Assert.All(values, value => Assert.InRange(value, -0.1f, 0.1f));
    }
}
