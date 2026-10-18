using Trackdub.Composition.StarterPacks;
using Trackdub.Domain;

namespace Trackdub.Composition.Tests;

public sealed class TrtRtxSmokeCatalogTests
{
    [Fact]
    public void TargetsForProvider_Cuda_ContainsKokoroAndParakeet()
    {
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = TrtRtxSmokeCatalog.TargetsForProvider(ExecutionProviderKind.Cuda);

        Assert.Contains(targets, target => target.Label == "tts-kokoro");
        Assert.Contains(targets, target => target.Label == "asr-parakeet-tdt-0.6b-v3");
    }

    [Fact]
    public void TargetsForProvider_TensorRt_ExcludesKokoroAndContainsTrtFp16Madlad()
    {
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = TrtRtxSmokeCatalog.TargetsForProvider(ExecutionProviderKind.TensorRt);

        Assert.DoesNotContain(targets, target => target.Label == "tts-kokoro");
        Assert.Contains(targets, target => target.Label == "translation-madlad-trt-fp16");
    }

    [Fact]
    public void TargetsForProvider_TensorRTRtx_ExcludesKokoro()
    {
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = TrtRtxSmokeCatalog.TargetsForProvider(ExecutionProviderKind.TensorRTRtx);

        Assert.DoesNotContain(targets, target => target.Label == "tts-kokoro");
    }

    [Fact]
    public void TargetsForProvider_NoDuplicateLabels()
    {
        foreach (ExecutionProviderKind provider in Enum.GetValues<ExecutionProviderKind>())
        {
            IReadOnlyList<TrtRtxSmokeCatalog.Target> targets = TrtRtxSmokeCatalog.TargetsForProvider(provider);

            Assert.Equal(
                targets.Count,
                targets.Select(target => target.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void TargetsForProvider_UnsupportedProvider_ReturnsEmpty()
    {
        Assert.Empty(TrtRtxSmokeCatalog.TargetsForProvider(ExecutionProviderKind.Cpu));
    }
}
