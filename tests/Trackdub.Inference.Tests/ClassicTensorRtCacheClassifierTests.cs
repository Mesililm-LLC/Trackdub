using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Tests;

public sealed class ClassicTensorRtCacheClassifierTests
{
    private static EngineCacheProbe.Snapshot Snapshot(int fileCount, long totalBytes) =>
        new("classic-trt-test", DirectoryExists: true, fileCount, totalBytes);

    [Fact]
    public void Classify_returns_compiled_when_cache_grew()
    {
        Assert.Equal(
            "compiled",
            ClassicTensorRtCacheClassifier.Classify(Snapshot(0, 0), Snapshot(1, 4096)));
        Assert.Equal(
            "compiled",
            ClassicTensorRtCacheClassifier.Classify(Snapshot(1, 4096), Snapshot(1, 8192)));
    }

    [Fact]
    public void Classify_returns_reused_when_cache_was_populated_and_did_not_grow()
    {
        Assert.Equal(
            "reused",
            ClassicTensorRtCacheClassifier.Classify(Snapshot(2, 8192), Snapshot(2, 8192)));
    }

    [Fact]
    public void Classify_returns_failed_when_cache_stayed_empty()
    {
        Assert.Equal(
            "failed",
            ClassicTensorRtCacheClassifier.Classify(Snapshot(0, 0), Snapshot(0, 0)));
    }

    [Fact]
    public void CaptureSnapshot_requires_engines_for_the_warmed_graph()
    {
        string directory = Path.Join(
            Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"), "classic-trt", "key");
        try
        {
            Directory.CreateDirectory(Path.Join(directory, "nested"));
            File.WriteAllBytes(Path.Join(directory, "model-a_0.engine"), new byte[16]);
            File.WriteAllBytes(Path.Join(directory, "nested", "model-a_1.engine"), new byte[8]);
            File.WriteAllBytes(Path.Join(directory, "model-b_0.profile"), new byte[4]);
            File.WriteAllBytes(Path.Join(directory, "model-b_0.timing"), new byte[4]);
            File.WriteAllBytes(Path.Join(directory, "model-b_0.engine"), []);

            EngineCacheProbe.Snapshot snapshot = ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model-a_");

            Assert.True(snapshot.DirectoryExists);
            Assert.Equal(directory, snapshot.DirectoryPath);
            Assert.Equal(2, snapshot.FileCount);
            Assert.Equal(24, snapshot.TotalBytes);

            EngineCacheProbe.Snapshot before = ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model-b_");
            File.WriteAllBytes(Path.Join(directory, "model-a_2.engine"), new byte[32]);
            EngineCacheProbe.Snapshot after = ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model-b_");
            Assert.Equal("failed", ClassicTensorRtCacheClassifier.Classify(before, after));

            File.WriteAllBytes(Path.Join(directory, "model-b_0.engine"), new byte[16]);
            after = ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model-b_");
            Assert.Equal("compiled", ClassicTensorRtCacheClassifier.Classify(before, after));
            Assert.Equal("reused", ClassicTensorRtCacheClassifier.Classify(after, after));
            File.Delete(Path.Join(directory, "model-b_0.engine"));
            Assert.Equal("failed", ClassicTensorRtCacheClassifier.Classify(after,
                ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model-b_")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildClassicTensorRtWarmInput_replaces_dynamic_dimensions_with_one()
    {
        NamedOnnxValue input = EpContextWarmupService.BuildClassicTensorRtWarmInput(
            "audio_signal", TensorElementType.Float, [-1, 128, -1]);

        Assert.Equal("audio_signal", input.Name);
        Assert.Equal([1, 128, 1], input.AsTensor<float>().Dimensions.ToArray());
    }

    [Fact]
    public void BuildClassicTensorRtWarmInput_builds_zero_length_tensor_for_int64()
    {
        NamedOnnxValue input = EpContextWarmupService.BuildClassicTensorRtWarmInput(
            "length", TensorElementType.Int64, [-1]);

        Assert.Equal([1], input.AsTensor<long>().Dimensions.ToArray());
        Assert.Equal(0L, input.AsTensor<long>()[0]);
    }

    [Fact]
    public void CaptureSnapshot_reports_missing_directory_as_empty()
    {
        string directory = Path.Join(
            Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"), "missing");

        EngineCacheProbe.Snapshot snapshot = ClassicTensorRtCacheClassifier.CaptureSnapshot(directory, "model_");

        Assert.False(snapshot.DirectoryExists);
        Assert.Equal(0, snapshot.FileCount);
        Assert.Equal(0, snapshot.TotalBytes);
    }
}
